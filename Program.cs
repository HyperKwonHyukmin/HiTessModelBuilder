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
      pipeline.RunFocusingOn(5);

      //BdfExporter.Export(context, CsvFolderPath, "Test");


    }

  }
}
