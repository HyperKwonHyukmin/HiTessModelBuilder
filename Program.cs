using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Initialzation;
using HiTessModelBuilder.Pipeline;
using System;
using System.Security.Cryptography.X509Certificates;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
        inputFileName, pipelineDebug: true);
      pipeline.Run(); 

      //BdfExporter.Export(context, CsvFolderPath, "Test");


    }

  }
}
