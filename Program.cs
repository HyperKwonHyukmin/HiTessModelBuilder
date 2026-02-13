using System;
using HiTessModelBuilder.Services.Initialzation;
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

      FeModelLoader.LoadAndBuild(StrucCsv, PipeCsv, EquipCsv);
    }

  }
}
