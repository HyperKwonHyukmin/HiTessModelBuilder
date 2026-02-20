using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Parsers;
using HiTessModelBuilder.Services.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

      // CSV 파싱 시작 (Structure, Pipe, Equip)
      var csvParser = new CsvRawDataParser(StrucCsv, PipeCsv, EquipCsv, debugPrint: debugMode);
      var rawStructureDesignData = csvParser.Run();

      // 빈 Fe 모델 컨텍스트 생성
      var context = FeModelContext.CreateEmpty();

      // FE 인스턴스 생성 시작
      var builder = new RawFeModelBuilder(rawStructureDesignData, context, debugPrint: debugMode);
      builder.Build();
    }
  }

}
