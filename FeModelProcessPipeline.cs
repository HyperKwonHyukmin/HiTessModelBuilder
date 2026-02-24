using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Preprocess;
using HiTessModelBuilder.Pipeline.ElementModifier;
using System;
using System.Collections.Generic;
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

    public void Run()
    {
      if (this._pipelineDebug) Console.WriteLine("\n[Pipeline Started] Processing FE Model...");

      ExportBaseline();
      RunStagedPipeline();
    }

    private void ExportBaseline()
    {
      string stageName = "STAGE_00";
      Console.WriteLine($"================ {stageName} =================");
      var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, _verboseDebug);
      BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
    }

    private void RunStagedPipeline()
    {
      //Stage 01 : 요소(Element) 경로 상에 존재하는 노드(Node)를 기준으로 요소를 분할(Split)
      RunStage("STAGE_01", () => ElementSplitByExistingNodesRun(_pipelineDebug, _verboseDebug));

      //Stage 02 : 서로 교차하는(X자, T자 등) 요소들을 찾아 교차점에 노드를 생성하고, 해당 노드를 기준으로 요소를 분할
      RunStage("STAGE_02", () => ElementIntersectionSplitRun(_pipelineDebug, _verboseDebug));
    }

    private void RunStage(string stageName, Action action, string customExportName = null)
    {
      Console.WriteLine($"================ {stageName} =================");
      action();
      var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, _verboseDebug);
      BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
    }

    /// <summary>
    /// 요소(Element) 경로 상에 존재하는 노드(Node)를 기준으로 요소를 분할(Split)하는 단계를 실행합니다.
    /// </summary>
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
      var opt = new ElementIntersectionSplitModifier.Options(1.0, 1e-9, 200.0, 1e-6, 0.05, true, true, false, isDebug);
      ElementIntersectionSplitModifier.Run(_context, opt, Console.WriteLine);
          }
  }
}
