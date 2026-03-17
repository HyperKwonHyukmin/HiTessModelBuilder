using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Initialization;
using HiTessModelBuilder.Pipeline;
using HiTessModelBuilder.Services.Logging;
using System;
using System.IO;
using System.Linq;

// CMD 작동 명령어 : HiTessModelBuilder.exe --stru "C:\stru.csv" --mesh 300 --verbose true --nastran false

namespace HiTessModelBuilder
{
  public class AppOptions
  {
    public string? StruCsvPath { get; set; }
    public string? PipeCsvPath { get; set; }
    public string? EquipCsvPath { get; set; }

    public double MeshSize { get; set; } = 500.0;
    public bool ForceUboltRigid { get; set; } = false; 
    public bool RunNastran { get; set; } = true;
    public bool CsvDebug { get; set; } = true;
    public bool FeModelDebug { get; set; } = true;
    public bool PipelineDebug { get; set; } = true;
    public bool VerboseDebug { get; set; } = false;
  }

  class MainApp
  {
    static void Main(string[] args)
    {
      AppOptions options = ParseArguments(args);

      if (string.IsNullOrWhiteSpace(options.StruCsvPath) &&
          string.IsNullOrWhiteSpace(options.PipeCsvPath) &&
          string.IsNullOrWhiteSpace(options.EquipCsvPath))
      {
        Console.WriteLine("처리할 데이터 파일(Stru, Pipe, Equip)이 하나도 지정되지 않았습니다.");
        return;
      }

      RunApplication(options);
    }

    /// <summary>
    /// 커맨드라인 인자(args)를 분석하여 프로그램 실행 옵션 객체를 반환합니다.
    /// 
    /// [지원하는 입력 방식 2가지]
    /// 1. 순서 기반 (기존 방식): HiTessModelBuilder.exe "C:\stru.csv" "C:\pipe.csv" "null"
    /// 2. 태그 기반 (고급 방식): HiTessModelBuilder.exe --stru "C:\stru.csv" --mesh 300
    /// 3. 혼합 방식 (가장 권장): HiTessModelBuilder.exe "C:\stru.csv" "null" "C:\equip.csv" --mesh 300 --nastran false
    /// </summary>
    private static AppOptions ParseArguments(string[] args)
    {
      // 1. 기본값 초기화 (VS 버깅 시 기본 작동)
      var options = new AppOptions
      {
        StruCsvPath = PathManager.Current.Stru,
        PipeCsvPath = PathManager.Current.Pipe,
        EquipCsvPath = PathManager.Current.Equip,
        MeshSize = 500.0,
        ForceUboltRigid = false, 
        RunNastran = true,
        CsvDebug = true,
        FeModelDebug = true,
        PipelineDebug = true,
        VerboseDebug = false
      };

      if (args == null || args.Length == 0)
        return options;

      int positionalIndex = 0; // 순서대로 들어오는 파일 경로의 인덱스를 추적 (0:Stru, 1:Pipe, 2:Equip)

      // 2. 파싱 루프 (순서 기반과 태그 기반을 동시에 처리)
      for (int i = 0; i < args.Length; i++)
      {
        string arg = args[i].ToLower();

        // [A] 태그(명령어)로 시작하는 경우 (예: --mesh, --stru)
        if (arg.StartsWith("--"))
        {
          if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
          {
            string value = args[i + 1];
            bool isNullStr = value.Equals("null", StringComparison.OrdinalIgnoreCase);

            switch (arg)
            {
              // 명시적으로 파일 경로를 지정하는 경우
              case "--stru": options.StruCsvPath = isNullStr ? null : value; break;
              case "--pipe": options.PipeCsvPath = isNullStr ? null : value; break;
              case "--equip": options.EquipCsvPath = isNullStr ? null : value; break;

              // 추가 옵션들
              case "--mesh":
              case "--meshsize":
                if (double.TryParse(value, out double mesh)) options.MeshSize = mesh;
                break;
              // ★ [추가] U-Bolt 강제 고정 옵션 태그
              case "--ubolt":
              case "--forceuboltrigid":
                if (bool.TryParse(value, out bool forceUbolt)) options.ForceUboltRigid = forceUbolt;
                break;
              case "--nastran":
              case "--runnastran":
                if (bool.TryParse(value, out bool runNastran)) options.RunNastran = runNastran;
                break;
              case "--csvdebug":
                if (bool.TryParse(value, out bool csvDbg)) options.CsvDebug = csvDbg;
                break;
              case "--fedebug":
              case "--femodeldebug":
                if (bool.TryParse(value, out bool feDbg)) options.FeModelDebug = feDbg;
                break;
              case "--pipeline":
              case "--pipelinedebug":
                if (bool.TryParse(value, out bool pipeDbg)) options.PipelineDebug = pipeDbg;
                break;
              case "--verbose":
              case "--verbosedebug":
                if (bool.TryParse(value, out bool verbDbg)) options.VerboseDebug = verbDbg;
                break;
            }
            i++; // 옵션의 '값(value)'을 읽었으므로 인덱스 1칸 건너뜀
          }
        }
        // [B] 태그 없이 그냥 텍스트가 들어온 경우 (기존처럼 순서대로 경로를 넣은 것으로 간주)
        else
        {
          bool isNullStr = args[i].Equals("null", StringComparison.OrdinalIgnoreCase);

          if (positionalIndex == 0) options.StruCsvPath = isNullStr ? null : args[i];
          else if (positionalIndex == 1) options.PipeCsvPath = isNullStr ? null : args[i];
          else if (positionalIndex == 2) options.EquipCsvPath = isNullStr ? null : args[i];

          positionalIndex++; // 다음 순서 없는 문자열은 다음 파일 경로로 인식
        }
      }

      return options;
    }

    private static void RunApplication(AppOptions opt)
    {
      string sourceForFileName = opt.StruCsvPath ?? opt.PipeCsvPath ?? "Default_Model";
      string targetPath = opt.StruCsvPath ?? opt.PipeCsvPath ?? opt.EquipCsvPath!;
      string csvFolderPath = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
      string pureFileName = Path.GetFileNameWithoutExtension(sourceForFileName);

      using (var logger = new PipelineLogger(csvFolderPath, sourceForFileName))
      {
        try
        {
          logger.LogInfo("=== HiTess Model Builder 파이프라인 시작 ===");

          // ====================================================================
          // 1. 단일 모델 빌드 및 기하학 파이프라인 실행 (단 1회만 수행!)
          // ====================================================================
          logger.LogInfo("[단계 1] 원시 데이터 로드 및 초기 모델 빌드");
          // 처음엔 원본 자유도(false)를 유지한 채로 뼈대를 올립니다.
          (RawCsvDesignData? rawCsvDesignData, FeModelContext context) =
            FeModelLoader.LoadAndBuild(
                opt.StruCsvPath!, opt.PipeCsvPath!, opt.EquipCsvPath!,
                forceUboltRigid: false,
                csvDebug: opt.CsvDebug,
                FeModelDebug: opt.FeModelDebug);

          logger.LogInfo("\n[단계 2] 기하학 및 위상학 힐링 알고리즘 실행");
          var pipeline = new FeModelProcessPipeline(
              rawCsvDesignData, context, csvFolderPath, sourceForFileName,
              pipelineDebug: opt.PipelineDebug, verboseDebug: opt.VerboseDebug, logger: logger);

          pipeline.RunFocusingOn(7); // STAGE 파일들은 여기서 한 번만 출력됩니다.

          logger.LogInfo($"\n[단계 3] 모델링 최적화 진행 중... (MeshSize: {opt.MeshSize}mm)");
          HiTessModelBuilder.Pipeline.ElementModifier.ElementMeshingModifier.Run(context, opt.MeshSize, logger.LogDelegate);

          logger.LogInfo("\n[단계 4] 최종 무결성 검사 및 경계조건(SPC) 산출");
          var finalSpcNodes = HiTessModelBuilder.Pipeline.Preprocess.StructuralSanityInspector.Inspect(
              context, true, opt.PipelineDebug, opt.VerboseDebug, isFinalStage: true, log: logger.LogDelegate);

          // ====================================================================
          // [Phase 1] 검증용 모델 BDF 추출 및 Nastran 해석
          // ====================================================================
          logger.LogInfo("\n=======================================================");
          logger.LogInfo("[Phase 1] Nastran 검증 실행");
          logger.LogInfo("=======================================================");

          // 메모리 상에서 U-Bolt 강체들의 자유도를 모두 "123456"으로 덮어씌웁니다.
          ToggleUboltRigidity(context, forceRigid: true, logger);

          string verifyBdfName = $"{pureFileName}_Verification.bdf";
          BdfExporter.Export(context, csvFolderPath, verifyBdfName, finalSpcNodes);

          if (opt.RunNastran)
          {
            string verifyBdfPath = Path.Combine(csvFolderPath, verifyBdfName);
            bool isSuccess = HiTessModelBuilder.Services.Execution.NastranExecutionService.RunAndAnalyze(
                verifyBdfPath, logger.LogDelegate);

            if (isSuccess)
              logger.LogSuccess("\n[검증 결과] 모델 건전성 검사 통과! (FATAL 에러 없음)");
            else
              logger.LogWarning("\n[검증 결과] Nastran 해석 중 FATAL 에러가 검출되었습니다. 위 로그의 문맥을 확인하세요.");
          }

          // ====================================================================
          // [Phase 2] 최종 납품용 모델 BDF 추출
          // ====================================================================
          logger.LogInfo("\n=======================================================");
          logger.LogInfo("[Phase 2] 최종 납품용 모델 생성");
          logger.LogInfo("=======================================================");

          // 다시 원래 설계 부서가 입력했던 자유도(Rest)로 원상 복구합니다.
          ToggleUboltRigidity(context, forceRigid: opt.ForceUboltRigid, logger);

          string finalBdfName = $"{pureFileName}.bdf";
          BdfExporter.Export(context, csvFolderPath, finalBdfName, finalSpcNodes);

          logger.LogSuccess($"\n=== 모든 프로세스 정상 종료. 최종 모델({finalBdfName})이 추출되었습니다. ===");
        }
        catch (Exception ex)
        {
          logger.LogError("파이프라인 실행 중 치명적인 오류가 발생하여 중단되었습니다.", ex);
        }
      }
    }

    /// <summary>
    /// FeModelContext 내부의 모든 UBOLT RBE 자유도를 메모리 상에서 즉시 스위칭합니다.
    /// (파이프라인 재실행으로 인한 리소스 낭비를 방지)
    /// </summary>
    private static void ToggleUboltRigidity(FeModelContext context, bool forceRigid, PipelineLogger logger)
    {
      int changedCount = 0;
      var rbeIds = context.Rigids.Keys.ToList(); // 순회 중 컬렉션 변경 에러 방지

      foreach (int id in rbeIds)
      {
        var rbe = context.Rigids[id];
        if (rbe.ExtraData != null && rbe.ExtraData.TryGetValue("Type", out string? type) && type == "UBOLT")
        {
          // PipeModelBuilder가 백업해둔 원본 Rest 값 가져오기 (없으면 기본값 123456)
          string originalRest = rbe.ExtraData.GetValueOrDefault("Rest") ?? "123456";
          if (string.IsNullOrWhiteSpace(originalRest)) originalRest = "123456";

          // 적용할 새로운 자유도 문자열 결정
          string newCm = forceRigid ? "123456" : originalRest;

          // 실제 변경이 필요할 때만 덮어쓰기 (RigidInfo는 불변 객체이므로 새로 할당)
          if (rbe.Cm != newCm)
          {
            var extraCopy = rbe.ExtraData.ToDictionary(k => k.Key, v => v.Value);
            context.Rigids.AddWithID(id, rbe.IndependentNodeID, rbe.DependentNodeIDs, newCm, extraCopy);
            changedCount++;
          }
        }
      }

      string state = forceRigid ? "완전 구속(123456)" : "설계 원본(Rest)";
      logger.LogInfo($"   -> U-Bolt {changedCount}개의 자유도를 [{state}] 상태로 스위칭 완료.");
    }
  }
}
