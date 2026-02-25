using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementModifier;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.Preprocess;
using System;

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
    /// 데이터를 초기화하지 않고 동일한 _context 위에 
    /// 매 루프마다 이전 알고리즘 세트를 누적 반복(Multi-pass) 적용합니다.
    /// </summary>
    public void RunFocusingOn(int targetStage)
    {
      if (_pipelineDebug)
      {
        Console.WriteLine($"\n[Pipeline] 타겟 STAGE_{targetStage:D2} 까지 알고리즘 누적 반복 적용을 시작합니다...");
      }

      for (int i = 0; i <= targetStage; i++)
      {
        string stageName = $"STAGE_{i:D2}";

        // 내가 지정한 최종 타겟 단계에서만 상세 로그(Verbose)를 켭니다.
        bool isTarget = (i == targetStage);
        bool currentVerbose = isTarget ? this._verboseDebug : false;

        if (_pipelineDebug || isTarget)
          Console.WriteLine($"\n================ {stageName} =================");

        // "알고리즘 누적 반복" 로직 적용
        switch (i)
        {
          case 0:
            /* Baseline: 수정 작업 없음 */
            break;
          case 1:
            ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
            break;
          case 2:
            ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
            ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
            break;
          case 3:
            ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
            ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
            ElementShortCollapseRun(_pipelineDebug, currentVerbose);
            break;
          case 4:
            ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
            ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
            ElementShortCollapseRun(_pipelineDebug, currentVerbose);
            ElementCollinearNodeMergeRun(_pipelineDebug, currentVerbose);
            break;
          case 5:
            ElementExtendToIntersectRun(_pipelineDebug, currentVerbose);
            ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
            ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
            ElementShortCollapseRun(_pipelineDebug, currentVerbose);
            ElementCollinearNodeMergeRun(_pipelineDebug, currentVerbose);
            break;
        }

        // 매 사이클이 끝날 때마다 상태를 점검하고 하이퍼메시 비교용 BDF를 찍어냅니다.
        var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, currentVerbose);
        BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
      }
    }

    // 전체 실행 래퍼 메서드
    public void Run()
    {
      RunFocusingOn(5);
    }

    // --- 이하 Modifier 실행 헬퍼 메서드들은 기존 코드와 동일합니다 ---

    private void ElementSplitByExistingNodesRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementSplitByExistingNodesModifier.Options(
          DistanceTol: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementSplitByExistingNodesModifier.Run(_context, opt, Console.WriteLine);
    }

    private void ElementIntersectionSplitRun(bool pDebug, bool vDebug)
    {
      var opt1 = new ElementIntersectionSplitModifier.Options(
          DistTol: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementIntersectionSplitModifier.Run(_context, opt1, Console.WriteLine);

      var opt2 = new ElementDanglingShortRemoveModifier.Options(
          LengthThreshold: 50.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementDanglingShortRemoveModifier.Run(_context, opt2, Console.WriteLine);
    }

    private void ElementShortCollapseRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementShortCollapseModifier.Options(
          Tolerance: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementShortCollapseModifier.Run(_context, opt, Console.WriteLine);
    }

    private void ElementCollinearNodeMergeRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementCollinearNodeMergeModifier.Options(
          DistanceTolerance: 30.0, AngleToleranceDeg: 3.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementCollinearNodeMergeModifier.Run(_context, opt, Console.WriteLine);
    }

    private void ElementExtendToIntersectRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementExtendToIntersectModifier.Options(
          ExtraMargin: 10.0, CoplanarTolerance: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug,
          DiagnosticSourceEid: 374, DiagnosticTargetEid: 373);
      ElementExtendToIntersectModifier.Run(_context, opt, Console.WriteLine);
    }
  }
}
