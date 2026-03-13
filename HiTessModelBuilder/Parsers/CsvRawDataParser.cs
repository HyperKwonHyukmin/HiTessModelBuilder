using System;
using System.Collections.Generic;
using System.Linq;
using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Services.Debugging;

namespace HiTessModelBuilder.Parsers
{
  public class CsvRawDataParser
  {
    private readonly string _strucCsv;
    private readonly string _pipeCsv; // 변수명 언더바 제거 권장
    private readonly string _equipCsv;
    private readonly bool _debugPrint;

    public CsvRawDataParser(string StrucCsv, string PipeCsv, string EquipCsv, bool debugPrint = false)
    {
      _strucCsv = StrucCsv;
      _pipeCsv = PipeCsv;
      _equipCsv = EquipCsv;
      _debugPrint = debugPrint;
    }

    public RawCsvDesignData? Run()
    {
      
      if (_debugPrint) Console.WriteLine($"[Parser] Reading Structure CSV: {_strucCsv}");

      var csvParser = new CsvParser();

      try
      {       
        var rawCsvDesignData = csvParser.Parse(_strucCsv, _pipeCsv, _equipCsv);
        
        if (_debugPrint)
        {
          RawDataDebugger.Verify(rawCsvDesignData);
        }

        return rawCsvDesignData;
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[Error] Structure Parsing Failed: {ex.Message}");
        return null;
      }    


    }
  }
}