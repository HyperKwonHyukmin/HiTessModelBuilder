using System;
using System.Collections.Generic;
using System.Linq;
using HiTessModelBuilder.Model.Entities;

namespace HiTessModelBuilder.Services.Debugging
{
  public static class RawDataDebugger
  {
    public static void Verify(RawCsvDesignData data)
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
      Console.WriteLine($" - Fbar  (Bar) : {data.FbarDesignList?.Count ?? 0}");
      Console.WriteLine($" - RBar  (라운드)   : {data.RbarDesignList?.Count ?? 0}");
      Console.WriteLine($" - Tube  (튜브)   : {data.TubeDesignList?.Count ?? 0}");
      Console.WriteLine($" - Unknown        : {data.UnknownDesignList?.Count ?? 0}");
      Console.WriteLine($" - Pipe  (배관 데이터) : {data.PipeList?.Count ?? 0}"); // [추가]
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

      PrintList("FBAR", data.FbarDesignList, e =>
          $"W={e.Width}, t={e.Thickness}");

      PrintList("RBAR", data.RbarDesignList, e =>
          $"Dia={e.Diameter}");

      PrintList("TUBE", data.TubeDesignList, e =>
           $"Dia={e.OuterDiameter}, Thickness={e.Thickness}");

      // 3. Unknown 데이터 확인 (파싱 실패 원인 분석용)
      PrintUnknownList(data.UnknownDesignList);
      // 3. [추가] Pipe 상세 데이터 출력 (상위 10개)
      PrintPipeList(data.PipeList);

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

    /// <summary>
    /// 배관(Pipe) 데이터를 디버깅 콘솔에 표 형태로 상세하게 출력합니다.
    /// 단일 PipeEntity 구조에 맞춰 Type 문자열을 기준으로 치수(Dimension) 포맷을 다르게 출력합니다.
    /// </summary>
    private static void PrintPipeList(List<PipeEntity> list)
    {
      if (list == null || list.Count == 0) return;

      int printLimit = Math.Min(list.Count, 10);
      Console.WriteLine($"\n>> Checking PIPE Data (First {printLimit} items):");

      // 테이블 헤더 (총 넓이를 넉넉히 주어 좌표 데이터가 깨지지 않도록 방어)
      string header = $"| {"Name",-12} | {"Type",-5} | {"Branch",-8} | {"Parsed Dims (OD x t)",-32} | {"APos (Start)",-26} | {"LPos (End)",-26} | {"Normal",-20} | {"Mass",-6} | {"Rest",-6} | {"P3/Inter Pos",-26} |";
      Console.WriteLine(header);
      Console.WriteLine(new string('-', header.Length)); // 헤더 길이에 맞 구분선 출력

      foreach (var item in list.Take(printLimit))
      {
        // 1. 공통 좌표 및 벡터 포맷팅 헬퍼 함수 (소수점 1자리 통일)
        string FormatVec(double[]? v) => v != null && v.Length >= 3
            ? $"{v[0]:F1}, {v[1]:F1}, {v[2]:F1}" : "N/A";

        string aPosStr = FormatVec(item.APos);
        string lPosStr = FormatVec(item.LPos);
        string normStr = FormatVec(item.Normal);

        // 2. 경계조건(Rest) 포맷팅
        string restStr = item.Rest != null && item.Rest.Length > 0
            ? string.Join("", item.Rest) : "-";

        // 3. 문자열(Type) 기반 치수(Dims) 포맷팅
        string dimInfo = "";
        if (item.Type == "TEE")
        {
          dimInfo = $"M({item.OutDia}x{item.Thick}) B({item.OutDia2}x{item.Thick2})";
        }
        else if (item.Type == "VALV" || item.Type == "UBOLT" || item.Type == "TRAP")
        {
          dimInfo = "N/A (Special)";
        }
        else
        {
          // TUBI, OLET, FLAN, REDU 등 일반 배관
          dimInfo = $"OD={item.OutDia}, t={item.Thick}";
        }

        // 4. 선택적 좌표(P3Pos, InterPos) 포맷팅
        string extraStr = "-";
        if (item.P3Pos != null && item.P3Pos.Length >= 3)
          extraStr = $"P3: {FormatVec(item.P3Pos)}";
        else if (item.InterPos != null && item.InterPos.Length >= 3)
          extraStr = $"In: {FormatVec(item.InterPos)}";

        // 행(Row) 출력
        Console.WriteLine($"| {item.Name,-12} | {item.Type,-5} | {item.Branch,-8} | {dimInfo,-32} | {aPosStr,-26} | {lPosStr,-26} | {normStr,-20} | {item.Mass,-6:F1} | {restStr,-6} | {extraStr,-26} |");
      }
    }
  }
}
