using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementModifier;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.Preprocess;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Pipeline
{
  public class FeModelProcessPipeline
  {
    private readonly RawStructureDesignData _rawStructureDesignData;
    private readonly FeModelContext _context;
    private readonly string _inputFileName;
    private readonly string _csvFolderPath;
    private readonly bool _pipelineDebug;
    private readonly bool _verboseDebug;
    public FeModelProcessPipeline(RawStructureDesignData? rawStructureDesignData, FeModelContext context,
      string CsvFolderPath, string inputFileName, bool pipelineDebug, bool verboseDebug)
    {
      this._rawStructureDesignData = rawStructureDesignData;
      this._context = context;
      this._csvFolderPath = CsvFolderPath;
      this._inputFileName = inputFileName;
      this._pipelineDebug = pipelineDebug;
      this._verboseDebug = verboseDebug;

    }

    /// <summary>
    /// 등록된 파이프라인의 모든 스테이지를 순차적으로 실행합니다.
    /// </summary>
    public void Run()
    {
      if (this._pipelineDebug) Console.WriteLine("\n[Pipeline Started] Processing FE Model...");

      // Stage 00 : 아무런 수정 없이 초기 상태(Baseline) 검사 및 파일 출력
      RunStage("STAGE_00", () => { /* Baseline은 수정 Action이 없습니다. */ });

      // Stage 01 : 요소(Element) 경로 상에 존재하는 노드(Node)를 기준으로 요소를 분할(Split)
      RunStage("STAGE_01", () => ElementSplitByExistingNodesRun(_pipelineDebug, _verboseDebug));

      // Stage 02 : 서로 교차하는(X자, T자 등) 요소들을 찾아 교차점에 노드를 생성하고 분할
      //          : 길이가 매우 짧으면서 한쪽 끝이 허공에 떠 있는 불필요한 꼬투리 요소 삭제
      RunStage("STAGE_02", () => ElementIntersectionSplitRun(_pipelineDebug, _verboseDebug));

      // Stage 03 : 미세 요소 붕괴(Collapse) 및 메쉬 꿰매기 (Healing)
      //          : 이전 분할 과정에서 생긴 '지나치게 짧은 불량 요소(1mm 미만 등)'를 찾아 삭제하고,
      //          : 벌어진 양 끝 노드를 강제로 하나로 합쳐(Merge) 찢어진 메쉬를 꿰매기
      //          : (Nastran 등 해석 솔버에서 강성이 무한대가 되어 에러가 나는 것을 방지)
      RunStage("STAGE_03", () => ElementShortCollapseRun(_pipelineDebug, _verboseDebug));

      RunStage("STAGE_04", () => ElementCollinearOverlapGroupRun(_pipelineDebug, _verboseDebug));
    }

    private void RunStage(string stageName, Action action)
    {
      Console.WriteLine($"\n================ {stageName} =================");

      // 1. 해당 스테이지의 수정 작업(Modifier) 실행
      action();

      // 2. 구조 건전성 검사 (Topology, Geometry, Duplicates 등) 수행 및 SPC 대상 반환
      var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, _verboseDebug);

      // 3. 현재 상태를 BDF로 출력
      BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
    }

    private void ElementSplitByExistingNodesRun(bool pipelineDebug, bool verboseDebug)
    {
      // 수정된 파이프라인 호출부
      var opt = new ElementSplitByExistingNodesModifier.Options(
          DistanceTol: 1.0,
          PipelineDebug: pipelineDebug,
          VerboseDebug: verboseDebug
      );
      ElementSplitByExistingNodesModifier.Run(_context, opt, Console.WriteLine);
    }

    private void ElementIntersectionSplitRun(bool pipelineDebug, bool verboseDebug)
    {
      var opt1 = new ElementIntersectionSplitModifier.Options(
          DistTol: 1.0,
          PipelineDebug: pipelineDebug,
          VerboseDebug: verboseDebug
      );
      ElementIntersectionSplitModifier.Run(_context, opt1, Console.WriteLine);

      var opt2 = new ElementDanglingShortRemoveModifier.Options(
          LengthThreshold: 50.0,
          PipelineDebug: pipelineDebug,
          VerboseDebug: verboseDebug
      );
      ElementDanglingShortRemoveModifier.Run(_context, opt2, Console.WriteLine);
    }

    private void ElementShortCollapseRun(bool pipelineDebug, bool verboseDebug)
    {
      var opt = new ElementShortCollapseModifier.Options(
          Tolerance: 1.0,
          PipelineDebug: pipelineDebug,
          VerboseDebug: verboseDebug
      );
      ElementShortCollapseModifier.Run(_context, opt, Console.WriteLine);
    }

    private void ElementCollinearOverlapGroupRun(bool pipelineDebug, bool verboseDebug)
    {
      var overlapGroup = ElementCollinearOverlapGroupInspector.FindSegmentationGroups(_context, 3e-2, 20.0);
      ElementCollinearOverlapAlignSplitModifier.Run(_context, overlapGroup, 0.05, 1e-3, isDebug, Console.WriteLine, false);
    }


  }
}
