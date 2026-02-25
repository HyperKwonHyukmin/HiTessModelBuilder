using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.ElementModifier;
using HiTessModelBuilder.Pipeline.Preprocess;
using HiTessModelBuilder.Services.Logging;
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
    private readonly PipelineLogger _logger; 

    public FeModelProcessPipeline(RawStructureDesignData? rawStructureDesignData, FeModelContext context,
      string CsvFolderPath, string inputFileName, bool pipelineDebug, bool verboseDebug, PipelineLogger logger)
    {
      this._rawStructureDesignData = rawStructureDesignData;
      this._context = context;
      this._csvFolderPath = CsvFolderPath;
      this._inputFileName = inputFileName;
      this._pipelineDebug = pipelineDebug;
      this._verboseDebug = verboseDebug;
      this._logger = logger;
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
        try
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

            case 5:
              int iteration5 = 0;
              int maxIterations5 = 10; // 무한 루프 방지용 안전 장치
              int translatedCount = 0;

              do
              {
                iteration5++;
                if (_pipelineDebug || currentVerbose)
                  Console.WriteLine($"\n      [Stage 5 - Iteration {iteration5}] 강체 그룹 병진 이동 수렴 루프 진행 중...");

                // 1. 강체 그룹 병진 이동 시도 (이동된 그룹 개수 반환)
                translatedCount = ElementGroupTranslationRun(_pipelineDebug, currentVerbose);

                // 2. 그룹 이동이 1개라도 발생했다면, 후속 힐링 및 연장(Stage 04) 작업을 연쇄 수행합니다.
                if (translatedCount > 0)
                {
                  // [2-1. 기본 힐링] 이동 후 새롭게 겹치거나 교차하는 부분 꿰매기
                  ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
                  ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
                  ElementShortCollapseRun(_pipelineDebug, currentVerbose);
                  ElementCollinearNodeMergeRun(_pipelineDebug, currentVerbose);

                  // [2-2. 연장 수렴 루프 (Stage 04 재적용)] 
                  // 그룹이 이동하면서 새롭게 연장 조건(SearchDim 이내)에 들어온 부재들을 끝까지 찾아 붙여줍니다.
                  extendedCount = 0;
                  int extendIteration = 0;
                  do
                  {
                    extendIteration++;
                    extendedCount = ElementExtendToIntersectRun(_pipelineDebug, currentVerbose);

                    if (extendedCount > 0)
                    {
                      ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
                      ElementIntersectionSplitRun(_pipelineDebug, currentVerbose);
                      ElementShortCollapseRun(_pipelineDebug, currentVerbose);
                      ElementCollinearNodeMergeRun(_pipelineDebug, currentVerbose);
                    }
                  } while (extendedCount > 0 && extendIteration < 10);
                }

                // 더 이상 이동할 그룹이 없을 때까지(translatedCount == 0) 전체 과정을 반복
              } while (translatedCount > 0 && iteration5 < maxIterations5);

              if (_pipelineDebug && iteration5 >= maxIterations5)
              {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"      [경고] Stage 5 최대 반복 횟수({maxIterations5}회) 초과. 수렴하지 못했을 수 있습니다.");
                Console.ResetColor();
              }
              break;

            case 6: // [최종 힐링 단계]
              int rbeCount = ElementRbeConnectionRun(_pipelineDebug, currentVerbose);

              // RBE2를 생성하기 위해 선분 한가운데에 새로운 Node(수선의 발)를 찍었으므로,
              // 기존 Target 부재들이 이 Node를 공유하여 쪼개지도록 Split을 한 번 실행해 줍니다! (아키텍처의 마법)
              if (rbeCount > 0)
              {
                ElementSplitByExistingNodesRun(_pipelineDebug, currentVerbose);
              }
              break;
          }

          // 매 사이클이 끝날 때마다 상태를 점검하고 하이퍼메시 비교용 BDF를 찍어냅니다.
          var freeEndNodes = StructuralSanityInspector.Inspect(_context, _pipelineDebug, currentVerbose);
          BdfExporter.Export(_context, _csvFolderPath, stageName, freeEndNodes);
        }

        catch (Exception ex)
        {
          // 특정 Stage 돌다가 터지면 여기서 잡아서 파일에 씀
          _logger.LogError($"[Stage {i:D2}] 처리 중 예기치 않은 오류가 발생했습니다.", ex);
          throw; // 상위(Program.cs)로 에러를 던져서 전체 프로세스 중단
        }

      }
    }

    // --- 이하 Modifier 실행 헬퍼 메서드들은 기존 코드와 동일합니다 ---

    private void ElementSplitByExistingNodesRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementSplitByExistingNodesModifier.Options(
          DistanceTol: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementSplitByExistingNodesModifier.Run(_context, opt, _logger.LogDelegate);
    }

    private void ElementIntersectionSplitRun(bool pDebug, bool vDebug)
    {
      var opt1 = new ElementIntersectionSplitModifier.Options(
          DistTol: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementIntersectionSplitModifier.Run(_context, opt1, _logger.LogDelegate);

      var opt2 = new ElementDanglingShortRemoveModifier.Options(
          LengthThreshold: 50.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementDanglingShortRemoveModifier.Run(_context, opt2, _logger.LogDelegate);
    }

    private void ElementShortCollapseRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementShortCollapseModifier.Options(
          Tolerance: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementShortCollapseModifier.Run(_context, opt, _logger.LogDelegate);
    }

    private void ElementCollinearNodeMergeRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementCollinearNodeMergeModifier.Options(
          DistanceTolerance: 30.0, AngleToleranceDeg: 3.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementCollinearNodeMergeModifier.Run(_context, opt, _logger.LogDelegate);
    }

    private int ElementExtendToIntersectRun(bool pDebug, bool vDebug)
    {
      // (진단 옵션은 필요 없다면 제거)
      var opt = new ElementExtendToIntersectModifier.Options(
          ExtraMargin: 20.0, CoplanarTolerance: 1.0, PipelineDebug: pDebug, VerboseDebug: vDebug);

      // 실행 결과를 리턴합니다.
      return ElementExtendToIntersectModifier.Run(_context, opt, _logger.LogDelegate);
    }

    private int ElementGroupTranslationRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementGroupTranslationModifier.Options(
          ExtraMargin: 50.0, // 그룹 간의 허용 갭 (필요 시 조절 가능)
          PipelineDebug: pDebug,
          VerboseDebug: vDebug);

      return ElementGroupTranslationModifier.Run(_context, opt, _logger.LogDelegate);
    }

    private int ElementRbeConnectionRun(bool pDebug, bool vDebug)
    {
      var opt = new ElementRbeConnectionModifier.Options(
          ExtraMargin: 5.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      return ElementRbeConnectionModifier.Run(_context, opt, _logger.LogDelegate);
    }
  }
}
