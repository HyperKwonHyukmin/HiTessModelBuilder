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

      string CsvFolderPath = Path.GetDirectoryName(StrucCsv);
      string inputFileName = Path.GetFileName(StrucCsv);

      (RawStructureDesignData? rawStructureDesignData, FeModelContext context) = 
        FeModelLoader.LoadAndBuild(StrucCsv, PipeCsv, EquipCsv, csvDebug:false, FeModelDebug:false);

      var pipeline = new FeModelProcessPipeline(rawStructureDesignData, context, CsvFolderPath, 
        inputFileName, pipelineDebug: true, verboseDebug: false);
      pipeline.Run(); 

      //BdfExporter.Export(context, CsvFolderPath, "Test");


    }

  }
}
