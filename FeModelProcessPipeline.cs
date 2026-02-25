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
            ElementCollinearNodeMergeRun(_pipelineDebug, currentVerbose);
            break;

          // case4는 대상 element 없을때까지 반복 수행
          case 4:
            int iteration = 0;
            int maxIterations = 10; // 무한 루프 방지용 안전 장치
            int extendedCount = 0;

            do
            {
              iteration++;
              if (_pipelineDebug || currentVerbose)
                Console.WriteLine($"\n      [Stage 4 - Iteration {iteration}] 수렴 루프 진행 중...");

              // 1. 연장을 먼저 시도하고, 몇 개나 연장되었는지 받아옵니다.
              extendedCount = ElementExtendToIntersectRun(_pipelineDebug, currentVerbose);

              // 2. 단 1개라도 연장되었다면 위상이 변했을 것이므로 후속 힐링 작업을 수행합니다.
              if (extendedCount > 0)
              {
                ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
                ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
                ElementShortCollapseRun(_pipelineDebug, currentVerbose);
                ElementCollinearNodeMergeRun(_pipelineDebug, currentVerbose);
              }

              // 연장된 부재가 없으면(extendedCount == 0) 루프를 종료합니다.
            } while (extendedCount > 0 && iteration < maxIterations);

            if (_pipelineDebug && iteration >= maxIterations)
            {
              Console.ForegroundColor = ConsoleColor.Red;
              Console.WriteLine($"      [경고] Stage 4 최대 반복 횟수({maxIterations}회) 초과. 수렴하지 못했을 수 있습니다.");
              Console.ResetColor();
            }
            break;
        }

        // 매 사이클이 끝날 때마다 상태를 점검하고 하이퍼메시 비교용 BDF를 찍어냅니다.
        var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, currentVerbose);
        BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
      }
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

    // void -> int 로 변경
    private int ElementExtendToIntersectRun(bool pDebug, bool vDebug)
    {
      // (진단 옵션은 필요 없다면 제거)
      var opt = new ElementExtendToIntersectModifier.Options(
          ExtraMargin: 10.0, CoplanarTolerance: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);

      // 실행 결과를 리턴합니다.
      return ElementExtendToIntersectModifier.Run(_context, opt, Console.WriteLine);
    }
  }
}
