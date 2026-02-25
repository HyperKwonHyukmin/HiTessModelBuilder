using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Initialization;
using HiTessModelBuilder.Pipeline;
using System;


namespace HiTessModelBuilder
{
  class MainApp
  {
    static void Main(string[] args)
    {
      string StrucCsv = PathManager.Current.Stru;
      string PipeCsv = PathManager.Current.Pipe;
      string EquipCsv = PathManager.Current.Equip;

      //string StrucCsv = args[0]; // 첫 번째 드래그 앤 드롭된 파일
      //string PipeCsv = null; // 첫 번째 드래그 앤 드롭된 파일
      //string EquipCsv = null; // 첫 번째 드래그 앤 드롭된 파일

      string CsvFolderPath = Path.GetDirectoryName(StrucCsv);
      string inputFileName = Path.GetFileName(StrucCsv);

      (RawStructureDesignData? rawStructureDesignData, FeModelContext context) =
        FeModelLoader.LoadAndBuild(StrucCsv, PipeCsv, EquipCsv, csvDebug: false, FeModelDebug: false);

      var pipeline = new FeModelProcessPipeline(rawStructureDesignData, context, CsvFolderPath,
        inputFileName, pipelineDebug: true, verboseDebug: false);

      // STAGE_00: 원본 데이터의 물리적 무결성 및 초기 연결 상태(Topology) 점검
      // STAGE_01: 타 부재의 노드가 요소 경로 내 존재할 경우, 해당 위치를 분할하여 절점 공유
      // STAGE_02: 교차 부재(X/T) 간 신규 절점 생성 및 불필요한 미세 꼬투리(Dangling) 제거
      // STAGE_03: 해석 에러 방지를 위해 1mm 미만 미세 요소를 제거하고 인접 노드를 강제 통합(Healing)
      //           동일 직선상에서 미세하게 어긋난 평행 부재의 인접 노드들을 병합하여 연결성 최적화
      // STAGE_04: 부재 연장 적용 후 (가능한 경우만), 변화된 위상이 안정화될 때까지 전체 공정을 반복 수행(Convergence Loop)
      pipeline.RunFocusingOn(4);

      //BdfExporter.Export(context, CsvFolderPath, "Test");


    }

  }
}
