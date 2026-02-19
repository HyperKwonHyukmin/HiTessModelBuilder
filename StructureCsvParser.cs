using System;
using System.Collections.Generic;
using System.Linq;
using HiTessModelBuilder.Model.Entities; // StructureEntity 접근을 위해 추가

namespace HiTessModelBuilder.Parsers
{
  public class CsvRawDataParser
  {
    private readonly string _strucCsv;
    private readonly string _pipeCsv_;
    private readonly string _equipCsv;
    private readonly bool _debugPrint; // 읽기 전용으로 변경 권장

    public CsvRawDataParser(string StrucCsv, string PipeCsv, string EquipCsv, bool debugPrint = false)
    {
      _strucCsv = StrucCsv;
      _pipeCsv_ = PipeCsv;
      _equipCsv = EquipCsv;
      _debugPrint = debugPrint;
    }

    public void Run()
    {
      // 1. Structure 파싱
      if (_debugPrint) Console.WriteLine($"[Parser] Reading Structure CSV: {_strucCsv}");

      var structureParser = new StructureCsvParser();
      try
      {
        structureParser.Parse(_strucCsv);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[Error] Structure Parsing Failed: {ex.Message}");
        // 필요 시 throw;
      }

      // 추후 Pipe, Equip 파서 실행 로직 추가...
    }

    /// <summary>
    /// 파싱된 구조물 데이터를 테이블 형태로 콘솔에 출력합니다.
    /// </summary>
    private void PrintStructureVerification(List<StructureEntity> entities)
    {
      Console.WriteLine($"\n>> Structure Parsing Complete. Total Items: {entities.Count}");

      if (entities.Count == 0)
      {
        Console.WriteLine("Warning: No entities parsed.");
        return;
      }

      // 헤더 출력
      Console.WriteLine(new string('-', 130));
      Console.WriteLine(
          $"| {"Name",-15} " +
          $"| {"Size (Raw)",-15} " +
          $"| {"Pos (X, Y, Z)",-25} " +
          $"| {"Ori (Vector)",-20} " +
          $"| {"Poss (Start)",-20} " +
          $"| {"Pose (End)",-20} |");
      Console.WriteLine(new string('-', 130));

      // 데이터 출력 (최대 20개만 샘플링)
      int count = 0;
      foreach (var e in entities)
      {
        string posFmt = $"{e.Pos.X:0}, {e.Pos.Y:0}, {e.Pos.Z:0}";
        string possFmt = $"{e.Poss.X:0}, {e.Poss.Y:0}, {e.Poss.Z:0}";
        string poseFmt = $"{e.Pose.X:0}, {e.Pose.Y:0}, {e.Pose.Z:0}";
        string oriFmt = $"{e.Ori.X:0.###}, {e.Ori.Y:0.###}, {e.Ori.Z:0.###}";

        Console.WriteLine(
            $"| {e.Name,-15} " +
            $"| {e.SizeDims,-15} " +
            $"| {posFmt,-25} " +
            $"| {oriFmt,-20} " +
            $"| {possFmt,-20} " +
            $"| {poseFmt,-20} |");
        count++;
      }

      Console.WriteLine(new string('-', 130));
    }
  }
}
