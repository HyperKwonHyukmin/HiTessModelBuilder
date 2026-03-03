using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Initialization;
using HiTessModelBuilder.Pipeline;
using HiTessModelBuilder.Services.Logging; // 추가
using System;
using System.IO;

namespace HiTessModelBuilder
{
  class MainApp
  {
    static void Main(string[] args)
    {
      string StrucCsv = PathManager.Current.Stru;
      string PipeCsv = PathManager.Current.Pipe;
      string EquipCsv = PathManager.Current.Equip;

      // [수정] Stru가 null이면 Pipe, Pipe도 null이면 Equip을 경로 기준으로 잡음 (우선순위 체인)
      string targetPath = StrucCsv ?? PipeCsv ?? EquipCsv;

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

          (RawCsvDesignData? rawCsvDesignData, FeModelContext context) =
            FeModelLoader.LoadAndBuild(StrucCsv, PipeCsv, EquipCsv, csvDebug: false, FeModelDebug: false);

          // 2. 파이프라인 생성 시 Logger 인스턴스 주입 (다음 스텝에서 Pipeline 생성자 수정 필요)
          var pipeline = new FeModelProcessPipeline(
              rawCsvDesignData, context, CsvFolderPath, inputFileName,
              pipelineDebug: false, verboseDebug: false, logger: logger);

          pipeline.RunFocusingOn(6);

          logger.LogSuccess("=== 파이프라인 전체 프로세스 정상 종료 ===");
        }
        catch (Exception ex)
        {
          // 프로그램이 뻗어버리는 치명적 오류(Unhandled Exception) 발생 시 상세 내용 파일 기록
          logger.LogError("파이프라인 실행 중 치명적인 오류가 발생하여 중단되었습니다.", ex);
          logger.LogInfo("위 에러 스택 트레이스를 개발팀에 전달해 주세요.");
        }
      } // <- 이 시점에 로그 파일 쓰기가 완료되고 파일이 잠금 해제됨
    }
  }
}