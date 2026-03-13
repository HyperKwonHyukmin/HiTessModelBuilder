using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Initialization;
using HiTessModelBuilder.Pipeline;
using HiTessModelBuilder.Services.Logging; 
using HiTessModelBuilder.Pipeline.ElementModifier; 
using System;
using System.IO;

namespace HiTessModelBuilder
{
  class MainApp
  {
    static void Main(string[] args)
    {
      string struCsv = PathManager.Current.Stru;
      string pipeCsv = PathManager.Current.Pipe;
      string equipCsv = PathManager.Current.Equip;

      // [수정] Stru가 null이면 Pipe, Pipe도 null이면 Equip을 경로 기준으로 잡음 (우선순위 체인)
      string sourceForFileName = struCsv ?? pipeCsv ?? "Default_Model";
      string targetPath = struCsv ?? pipeCsv ?? equipCsv;

      if (string.IsNullOrWhiteSpace(targetPath))
      {
        Console.WriteLine("처리할 데이터 파일(Stru, Pipe, Equip)이 하나도 지정되지 않았습니다.");
        return;
      }

      // 안전하게 폴더명과 파일명 추출
      string CsvFolderPath = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
      string inputFileName = Path.GetFileName(targetPath) ?? "Default_Model";

      // 1. Logger 초기화 (using 문을 사용하여 블록을 벗어나면 자동으로 파일이 닫히게 함)
      using (var logger = new PipelineLogger(CsvFolderPath, inputFileName))
      {
        try
        {
          logger.LogInfo("=== HiTess Model Builder 파이프라인 시작 ===");

          // ★ [디버깅 제어용 스위치]
          // true : 모든 U-Bolt를 완벽한 강체(123456)로 구속하여 해석 에러 원인(Singularity 등) 배제
          // false: CSV에 정의된 Rest 정보(열팽창 진행방향 릴리즈 등)를 그대로 적용
          bool testUboltRigid = false;
          (RawCsvDesignData? rawCsvDesignData, FeModelContext context) =
            FeModelLoader.LoadAndBuild(struCsv, pipeCsv, equipCsv, forceUboltRigid: testUboltRigid, csvDebug: true, FeModelDebug: true);

          // 2. 파이프라인 생성 시 Logger 인스턴스 주입 (다음 스텝에서 Pipeline 생성자 수정 필요)
          var pipeline = new FeModelProcessPipeline(
              rawCsvDesignData, context, CsvFolderPath, inputFileName,
              pipelineDebug: true, verboseDebug: false, logger: logger);

          pipeline.RunFocusingOn(7);

          // [2] Mesh Size 입력 (예시: 500mm, 실무에서는 args 또는 UI에서 받음)
          double meshSize = 500.0;
          logger.LogInfo($"\n[Finalizing] 최종 모델링 최적화 진행 중... (MeshSize: {meshSize}mm)");

          // [3] 최종 Mesh 분할 실행
          ElementMeshingModifier.Run(context, meshSize, logger.LogDelegate);

          // [4] 최종 Sanity Check (Meshing 후 위상 무결성 확인)
          var finalSpcNodes = HiTessModelBuilder.Pipeline.Preprocess.StructuralSanityInspector.Inspect(
              context, true, true, false, isFinalStage: true, log: logger.LogDelegate);

          // [5] 최종 BDF 출력 (변경된 파일명 규칙 적용)
          BdfExporter.Export(context, CsvFolderPath, sourceForFileName, finalSpcNodes);

          logger.LogSuccess("=== 파이프라인 및 BDF 추출 정상 종료 ===");


        }
        catch (Exception ex)
        {
          // 프로그램이 뻗어버리는 치명적 오류(Unhandled Exception) 발생 시 상세 내용 파일 기록
          logger.LogError("파이프라인 실행 중 치명적인 오류가 발생하여 중단되었습니다.", ex);
          logger.LogInfo("위 에러 스택 트레이스를 구조시스템연구실에 전달해 주세요.");
        }
      } // <- 이 시점에 로그 파일 쓰기가 완료되고 파일이 잠금 해제됨
      


    }
  }
}
