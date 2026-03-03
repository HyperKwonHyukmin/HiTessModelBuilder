using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Parsers;
using HiTessModelBuilder.Services.Builders;
using HiTessModelBuilder.Services.Debugging;
using System;


namespace HiTessModelBuilder.Services.Initialization
{
  public static class FeModelLoader
  {
    public static (RawCsvDesignData? rawStructureDesignData, FeModelContext context)
      LoadAndBuild(string StrucCsv, string PipeCsv, string EquipCsv, 
      bool csvDebug = false, bool FeModelDebug = false)
    {

      if (csvDebug) Console.WriteLine("\n[Loader] Parsing CSV Data...");

      // CSV 파싱 시작 (Structure, Pipe, Equip)
      var csvParser = new CsvRawDataParser(StrucCsv, PipeCsv, EquipCsv, debugPrint: csvDebug);
      var rawCsvDesignData = csvParser.Run();

      // 빈 Fe 모델 컨텍스트 생성
      var context = FeModelContext.CreateEmpty();

      // FE 인스턴스 생성 시작
      var builder = new RawFeModelBuilder(rawCsvDesignData, context, debugPrint: FeModelDebug);
      builder.Build();

      if (FeModelDebug)
      {
        Console.WriteLine("\n[Loader] Generating FE Model Debug Report...");

        var debugger = new FeModelDebugger(context);

        // PrintDebugInfo()의 기본 limit은 20개입니다. 
        // 만약 전체 데이터를 다 보고 싶다면 debugger.PrintDebugInfo(-1); 로 호출
        debugger.PrintDebugInfo();
      }

      return (rawCsvDesignData, context);

    }
  }

}
