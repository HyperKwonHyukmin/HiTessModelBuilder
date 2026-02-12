using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HiTessModelBuilder.Parsers;

namespace HiTessModelBuilder.Services.Initialzation
{
  public static class FeModelLoader
  {
    public static void LoadAndBuild(string StrucCsv, string PipeCsv, string EquipCsv, bool debugMode = false)
    {
      // Struc.csv는 필수 
      if (!File.Exists(StrucCsv))
        throw new FileNotFoundException($"Structure CSV not found: {StrucCsv}");

      if (debugMode) Console.WriteLine("\n[Loader] Parsing CSV Data...");

      var csvParser = new CsvRawDataParser(StrucCsv, PipeCsv, EquipCsv, debugPrint: debugMode);
      csvParser.Run();
    }
  }

}
