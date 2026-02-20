using System;
using System.Collections.Generic;
using System.Linq;
using HiTessModelBuilder.Model.Entities;

namespace HiTessModelBuilder.Services.Debugging
{
  public static class RawDataDebugger
  {
    public static void Verify(RawStructureDesignData data)
    {
      if (data == null)
      {
        Console.WriteLine(">> [Debug] RawDesignData is NULL.");
        return;
      }

      Console.WriteLine("\n========================================================");
      Console.WriteLine("               RawDesignData Verification               ");
      Console.WriteLine("========================================================");

      // 1. 전체 요약 (Count 확인)
      Console.WriteLine($"[Summary]");
      Console.WriteLine($" - Angle (ㄱ형강) : {data.AngDesignList?.Count ?? 0}");
      Console.WriteLine($" - Beam  (H형강)  : {data.BeamDesignList?.Count ?? 0}");
      Console.WriteLine($" - BSC  (Channel)   : {data.BscDesignList?.Count ?? 0}");
      Console.WriteLine($" - Bulb  (Bar) : {data.BulbDesignList?.Count ?? 0}");
      Console.WriteLine($" - RBar  (라운드)   : {data.RbarDesignList?.Count ?? 0}");
      Console.WriteLine($" - Tube  (튜브)   : {data.TubeDesignList?.Count ?? 0}");
      Console.WriteLine($" - Unknown        : {data.UnknownDesignList?.Count ?? 0}");
      Console.WriteLine("--------------------------------------------------------");

      // 2. 각 타입별 상세 데이터 검증 (상위 5개만 출력)
      PrintList("ANGLE", data.AngDesignList, e =>
          $"W={e.Width}, H={e.Height}, t={e.Thickness}");

      PrintList("BEAM", data.BeamDesignList, e =>
          $"W={e.Width}, H={e.Height}, ti={e.InnerThickness}, to={e.OuterThickness}");

      PrintList("Channel (BSC)", data.BscDesignList, e =>
          $"W={e.Width}, H={e.Height}, ti={e.InnerThickness}, to={e.OuterThickness}");

      PrintList("BULB", data.BulbDesignList, e =>
          $"W={e.Width}, t={e.Thickness}");

      PrintList("RBAR", data.RbarDesignList, e =>
          $"Dia={e.Diameter}");

      PrintList("TUBE", data.TubeDesignList, e =>
           $"Dia={e.OuterDiameter}, Thickness={e.Thickness}");

      // 3. Unknown 데이터 확인 (파싱 실패 원인 분석용)
      PrintUnknownList(data.UnknownDesignList);

      Console.WriteLine("========================================================\n");
    }

    // 제네릭 출력 메서드
    private static void PrintList<T>(string label, List<T> list, Func<T, string> dimInfo) where T : StructureEntity
    {
      if (list == null || list.Count == 0) return;

      Console.WriteLine($"\n>> Checking {label} (First {Math.Min(list.Count, 5)} items):");
      Console.WriteLine($"| {"Name",-15} | {"Size(Raw)",-18} | {"Parsed Dims (Property)",-40} | {"Pos(Start)",-20} |");
      Console.WriteLine(new string('-', 100));

      foreach (var item in list.Take(5))
      {
        string posStr = item.Poss != null && item.Poss.Length >= 3
            ? $"{item.Poss[0]:0},{item.Poss[1]:0},{item.Poss[2]:0}"
            : "No Pos";

        Console.WriteLine($"| {item.Name,-15} | {item.SizeText,-18} | {dimInfo(item),-40} | {posStr,-20} |");
      }
    }

    private static void PrintUnknownList(List<UnknownDesignData> list)
    {
      if (list == null || list.Count == 0) return;

      Console.WriteLine($"\n>> Checking UNKNOWN Items (Check why these failed):");
      foreach (var item in list.Take(5))
      {
        Console.WriteLine($" - Name: {item.Name}, SizeRaw: {item.SizeText}");
        // RawLine이 있다면 출력
        if (!string.IsNullOrEmpty(item.RawLine))
          Console.WriteLine($"   RawLine: {item.RawLine}");
      }
    }
  }
}
