using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.ElementModifier;
using HiTessModelBuilder.Pipeline.Preprocess;
using HiTessModelBuilder.Services.Debugging;
using HiTessModelBuilder.Services.Logging;
using System;

namespace HiTessModelBuilder.Pipeline
{
  public class FeModelProcessPipeline
  {
    private readonly RawCsvDesignData _rawStructureDesignData;
    private readonly FeModelContext _context;
    private readonly string _inputFileName;
    private readonly string _csvFolderPath;
    private readonly bool _pipelineDebug;
    private readonly bool _verboseDebug;
    private readonly PipelineLogger _logger;
    public bool UseExplicitWeldSpc { get; set; } = true; // 기본값 켜짐 (없으면 자동 Fallback됨)

    public FeModelProcessPipeline(RawCsvDesignData? rawStructureDesignData, FeModelContext context,
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

          bool isTarget = (i == targetStage);
          bool currentVerbose = isTarget ? this._verboseDebug : false;

          // ★ [핵심 분리 1] Modifier용 디버그 (타겟 스테이지에서만 켬 -> 수렴 루프 도배 방지)
          bool modifierDebug = isTarget ? this._pipelineDebug : false;

          // 스테이지 진입 배너는 파이프라인 디버그가 켜져 있다면 매 스테이지마다 출력
          if (this._pipelineDebug)
            Console.WriteLine($"\n================ {stageName} =================");

          switch (i)
          {
            case 0:
              break;
            case 1:
              ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
              break;
            case 2:
              ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
              ElementIntersectionSplitRun(modifierDebug, currentVerbose);
              break;
            case 3:
              ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
              ElementIntersectionSplitRun(modifierDebug, currentVerbose);
              ElementShortCollapseRun(modifierDebug, currentVerbose);
              ElementCollinearNodeMergeRun(modifierDebug, currentVerbose);
              break;
            case 4:
              int iteration = 0;
              int maxIterations = 10;
              int extendedCount = 0;
              do
              {
                iteration++;
                // 수렴 루프 진행 로그도 modifierDebug에 묶어서 과거 스테이지에서는 숨김
                if (modifierDebug || currentVerbose)
                  Console.WriteLine($"\n      [Stage 4 - Iteration {iteration}] 수렴 루프 진행 중...");

                extendedCount = ElementExtendToIntersectRun(modifierDebug, currentVerbose);

                if (extendedCount > 0)
                {
                  ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
                  ElementIntersectionSplitRun(modifierDebug, currentVerbose);
                  ElementShortCollapseRun(modifierDebug, currentVerbose);
                  ElementCollinearNodeMergeRun(modifierDebug, currentVerbose);
                }
              } while (extendedCount > 0 && iteration < maxIterations);

              if (modifierDebug && iteration >= maxIterations)
              {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"      [경고] Stage 4 최대 반복 횟수({maxIterations}회) 초과. 수렴하지 못했을 수 있습니다.");
                Console.ResetColor();
              }
              break;

            case 5:
              int iteration5 = 0;
              int maxIterations5 = 10;
              int translatedCount = 0;
              do
              {
                iteration5++;
                if (modifierDebug || currentVerbose)
                  Console.WriteLine($"\n      [Stage 5 - Iteration {iteration5}] 강체 그룹 병진 이동 수렴 루프 진행 중...");

                translatedCount = ElementGroupTranslationRun(modifierDebug, currentVerbose);

                if (translatedCount > 0)
                {
                  ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
                  ElementIntersectionSplitRun(modifierDebug, currentVerbose);
                  ElementShortCollapseRun(modifierDebug, currentVerbose);
                  ElementCollinearNodeMergeRun(modifierDebug, currentVerbose);

                  extendedCount = 0;
                  int extendIteration = 0;
                  do
                  {
                    extendIteration++;
                    extendedCount = ElementExtendToIntersectRun(modifierDebug, currentVerbose);
                    if (extendedCount > 0)
                    {
                      ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
                      ElementIntersectionSplitRun(modifierDebug, currentVerbose);
                      ElementShortCollapseRun(modifierDebug, currentVerbose);
                      ElementCollinearNodeMergeRun(modifierDebug, currentVerbose);
                    }
                  } while (extendedCount > 0 && extendIteration < 10);
                }
              } while (translatedCount > 0 && iteration5 < maxIterations5);

              if (modifierDebug && iteration5 >= maxIterations5)
              {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"      [경고] Stage 5 최대 반복 횟수({maxIterations5}회) 초과.");
                Console.ResetColor();
              }
              break;

            case 6:
              int rbeCount = ElementRbeConnectionRun(modifierDebug, currentVerbose);
              if (rbeCount > 0)
              {
                ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
              }
              break;

            case 7:
              int uboltCount = UboltConnectionRun(modifierDebug, currentVerbose);
              if (uboltCount > 0)
              {
                ElementSplitByExistingNodesRun(modifierDebug, currentVerbose);
                ElementIntersectionSplitRun(modifierDebug, currentVerbose);
                ElementShortCollapseRun(modifierDebug, currentVerbose);
                ElementCollinearNodeMergeRun(modifierDebug, currentVerbose);
              }

              RigidConsolidationRun(modifierDebug, currentVerbose);

              // 1. 가까운 거리에 있는 Rigid들 끼리 통폐합하여 하나의 덩어리로 만듭니다.
              var proxMergeOpt = new RigidProximityMergeModifier.Options(Tolerance: 50.0, PipelineDebug: modifierDebug, VerboseDebug: currentVerbose);
              RigidProximityMergeModifier.Run(_context, proxMergeOpt, _logger.LogDelegate);

              // 2. 그리고도 연결되지 못한 채 허공에 떠 있는 고립 Rigid를 Element에 억지로 붙이거나 영구 삭제합니다.
              var snapOpt = new RigidFreeNodeSnapModifier.Options(Tolerance: 100.0, PipelineDebug: modifierDebug, VerboseDebug: currentVerbose);
              RigidFreeNodeSnapModifier.Run(_context, snapOpt, _logger.LogDelegate);
              break;
          }

          // ★ [핵심 분리 2] Sanity 검사
          var freeEndNodes = StructuralSanityInspector.Inspect(_context, true, this._pipelineDebug, currentVerbose, isTarget);

          // ★ [수정] stageName 뒤에 명시적으로 ".bdf" 확장자를 붙여서 넘겨줍니다.
          BdfExporter.Export(_context, _csvFolderPath, $"{stageName}.bdf", freeEndNodes);
        }
        catch (Exception ex)
        {
          _logger.LogError($"[Stage {i:D2}] 처리 중 예기치 않은 오류가 발생했습니다.", ex);
          throw;
        }
      }

      if (_pipelineDebug)
      {
        Console.WriteLine("\n================ FINAL CHECK =================");
      }
      StructuralSanityInspector.InspectRigidIntegrity(_context, _pipelineDebug, this._verboseDebug);
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
          DistanceTolerance: 50.0, AngleToleranceDeg: 3.0, PipelineDebug: pDebug, VerboseDebug: vDebug);
      ElementCollinearNodeMergeModifier.Run(_context, opt, _logger.LogDelegate);
    }

    private int ElementExtendToIntersectRun(bool pDebug, bool vDebug)
    {
      // (진단 옵션은 필요 없다면 제거)
      var opt = new ElementExtendToIntersectModifier.Options(
          ExtraMargin: 100.0, CoplanarTolerance: 10.0, PipelineDebug: pDebug, VerboseDebug: vDebug);

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

    /// <summary>
    /// UBOLT의 연결을 전담하는 헬퍼 메서드입니다.
    /// 일반 UBOLT의 수직 스냅(Snap) 연결 및 분할 힐링을 선행한 후, 
    /// 특수 형태인 BOX 타입 UBOLT의 4점 연결 로직을 수행합니다.
    /// </summary>
    private int UboltConnectionRun(bool pDebug, bool vDebug)
    {
      int totalProcessed = 0;

      // 1. 일반 UBOLT (수직 스냅) 처리
      // ★ [수정] 대구경 배관과 서포트 사이의 갭(Gap)을 고려하여 여유 마진을 150.0mm로 늘림
      var snapOpt = new UboltSnapToStructureModifier.Options(
          ExtraMargin: 100.0,
          PipelineDebug: pDebug,
          VerboseDebug: vDebug
      );
      int snapCount = UboltSnapToStructureModifier.Run(_context, snapOpt, _logger.LogDelegate);
      totalProcessed += snapCount;

      // 일반 UBOLT가 구조물 부재 중간에 새로운 노드를 찍었으므로, 분할 힐링 실행
      if (snapCount > 0)
      {
        ElementSplitByExistingNodesRun(pDebug, vDebug);
      }

      // 2. BOX 타입 UBOLT 처리
      var boxOpt = new UboltBoxConnectionModifier.Options(PipelineDebug: pDebug, VerboseDebug: vDebug);
      int boxCount = UboltBoxConnectionModifier.Run(_context, boxOpt, _logger.LogDelegate);
      totalProcessed += boxCount;

      // ★ [신규 추가] 3. Nastran 자유도 충돌 방지: 
      // 동일한 종속 노드(Dependent)를 공유하는 여러 개의 강체를 마스터-슬레이브 반전하여 1개로 병합
      RigidSharedDependentNodeMergeModifier.Run(_context, pDebug, vDebug, _logger.LogDelegate);

      return totalProcessed;
    }

    private void RigidConsolidationRun(bool pDebug, bool vDebug)
    {
      var opt = new RigidConsolidationModifier.Options(PipelineDebug: pDebug, VerboseDebug: vDebug);
      RigidConsolidationModifier.Run(_context, opt, _logger.LogDelegate);
    }
  }
}