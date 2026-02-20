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

    public RawStructureDesignData? Run()
    {
      // 1. Structure 파싱
      if (_debugPrint) Console.WriteLine($"[Parser] Reading Structure CSV: {_strucCsv}");

      // 주의: StructureCsvParser 클래스가 실제로 구현되어 있어야 합니다.
      var structureParser = new StructureCsvParser();

      try
      {
        // StructureCsvParser 내부에 Parse 메서드와 ParsedEntities 속성이 있다고 가정
        var rawStructureDesignData = structureParser.Parse(_strucCsv);
        

        // 2. 디버그 모드일 경우 검증 출력
        if (_debugPrint)
        {
          // ParsedEntities가 List<StructureEntity>를 반환한다고 가정
          RawDataDebugger.Verify(rawStructureDesignData);
        }

        return rawStructureDesignData;
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[Error] Structure Parsing Failed: {ex.Message}");
        return null;
      }    


    }
  }
}
