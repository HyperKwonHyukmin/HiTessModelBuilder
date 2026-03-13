using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Initialization;
using HiTessModelBuilder.Pipeline;
using HiTessModelBuilder.Pipeline.ElementModifier;
using HiTessModelBuilder.Services.Logging;
using System;
using System.IO;

namespace HiTessModelBuilder
{
  /// <summary>
  /// 프로그램 구동을 위한 환경 설정(인자) 클래스입니다.
  /// 향후 CommandLineParser 등을 통해 args 배열을 이 객체로 매핑하게 됩니다.
  /// </summary>
  public class AppOptions
  {
    // 파일 경로 설정
    public string? StruCsvPath { get; set; }
    public string? PipeCsvPath { get; set; }
    public string? EquipCsvPath { get; set; }

    // 모델링 알고리즘 파라미터
    public double MeshSize { get; set; } = 500.0;
    public bool ForceUboltRigid { get; set; } = true;

    // 디버깅 및 로깅 설정
    public bool CsvDebug { get; set; } = true;
    public bool FeModelDebug { get; set; } = true;
    public bool PipelineDebug { get; set; } = true;
    public bool VerboseDebug { get; set; } = false;
  }

  class MainApp
  {
    static void Main(string[] args)
    {
      // 1. 초기 환경 설정 및 인자 파싱
      AppOptions options = ParseArguments(args);

      if (string.IsNullOrWhiteSpace(options.StruCsvPath) &&
          string.IsNullOrWhiteSpace(options.PipeCsvPath) &&
          string.IsNullOrWhiteSpace(options.EquipCsvPath))
      {
        Console.WriteLine("처리할 데이터 파일(Stru, Pipe, Equip)이 하나도 지정되지 않았습니다.");
        return;
      }

      // 2. 메인 파이프라인 실행
      RunApplication(options);
    }

    /// <summary>
    /// 입력된 args를 분석하여 AppOptions 객체를 반환합니다.
    /// 현재는 개발 단계이므로 PathManager를 활용한 하드코딩 데이터를 주입합니다.
    /// </summary>
    private static AppOptions ParseArguments(string[] args)
    {
      // TODO: 향후 상용화 시 여기에 args 파싱 직을 추가합니다. (예: CommandLineParser 라이브러리 사용)
      // if (args.Length > 0) { ... }

      return new AppOptions
      {
        StruCsvPath = PathManager.Current.Stru,
        PipeCsvPath = PathManager.Current.Pipe,
        EquipCsvPath = PathManager.Current.Equip,

        // 여기에 원하는 테스트 값을 하드코딩하여 사용하시면 됩니다.
        MeshSize = 500.0,
        ForceUboltRigid = true,
        CsvDebug = true,
        FeModelDebug = true,
        PipelineDebug = true,
        VerboseDebug = false
      };
    }

    /// <summary>
    /// 설정된 AppOptions를 바탕으로 FE 모델 생성 파이프라인을 실행합니다.
    /// </summary>
    private static void RunApplication(AppOptions opt)
    {
      // 경로 및 기준 파일명 도출 (Stru 최우선, 없으면 Pipe)
      string sourceForFileName = opt.StruCsvPath ?? opt.PipeCsvPath ?? "Default_Model";
      string targetPath = opt.StruCsvPath ?? opt.PipeCsvPath ?? opt.EquipCsvPath!;
      string csvFolderPath = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;

      using (var logger = new PipelineLogger(csvFolderPath, sourceForFileName))
      {
        try
        {
          logger.LogInfo("=== HiTess Model Builder 파이프라인 시작 ===");
          logger.LogInfo($"[설정 요약] MeshSize: {opt.MeshSize}mm, ForceUboltRigid: {opt.ForceUboltRigid}");

          // [단계 1] 원시 데이터 로드 및 초기 FE 모델 빌드
          (RawCsvDesignData? rawCsvDesignData, FeModelContext context) =
            FeModelLoader.LoadAndBuild(
                opt.StruCsvPath!, opt.PipeCsvPath!, opt.EquipCsvPath!,
                forceUboltRigid: opt.ForceUboltRigid,
                csvDebug: opt.CsvDebug,
                FeModelDebug: opt.FeModelDebug);

          // [단계 2] 기하학 및 위상학 힐링 파이프라인 실행 (Stage 1 ~ 7)
          var pipeline = new FeModelProcessPipeline(
              rawCsvDesignData, context, csvFolderPath, sourceForFileName,
              pipelineDebug: opt.PipelineDebug, verboseDebug: opt.VerboseDebug, logger: logger);

          pipeline.RunFocusingOn(7);

          // [단계 3] 최종 부재 메쉬 분할 (Meshing)
          logger.LogInfo($"\n[Finalizing] 최종 모델링 최적화 진행 중... (MeshSize: {opt.MeshSize}mm)");
          ElementMeshingModifier.Run(context, opt.MeshSize, logger.LogDelegate);

          // [단계 4] 최종 무결성 검사 및 해석용 경계조건(SPC) 산출
          var finalSpcNodes = HiTessModelBuilder.Pipeline.Preprocess.StructuralSanityInspector.Inspect(
              context, true, opt.PipelineDebug, opt.VerboseDebug, isFinalStage: true, log: logger.LogDelegate);

          // [단계 5] 최종 BDF 포맷 추출 및 저장 (마지막 최종본에만 타임스탬프 추가)
          BdfExporter.Export(context, csvFolderPath, sourceForFileName, finalSpcNodes, appendTimestamp: true);

          logger.LogSuccess("=== 파이프라인 및 BDF 추출 정상 종료 ===");
        }
        catch (Exception ex)
        {
          logger.LogError("파이프라인 실행 중 치명적인 오류가 발생하여 중단되었습니다.", ex);
          logger.LogInfo("위 에러 스택 트레이스를 구조시스템연구실에 전달해 주세요.");
        }
      }
    }
  }
}
