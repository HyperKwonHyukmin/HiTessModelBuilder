using HiTessModelBuilder.Model.Entities;
using System;
using System.Linq;
using System.Text;

namespace HiTessModelBuilder.Services.Debugging
{
  public class FeModelDebugger
  {
    private readonly FeModelContext _context;

    public FeModelDebugger(FeModelContext context)
    {
      _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// 모델의 전체 요약과 각 엔티티별 상세 내용을 콘솔에 출력합니다.
    /// </summary>
    /// <param name="limit">각 항목별 출력할 최대 개수 (기본값: 20개, -1이면 전체 출력)</param>
    public void PrintDebugInfo(int limit = 20)
    {
      PrintHeader("FE MODEL CONTEXT DEBUG REPORT");

      // 1. 요약 정보 (Summary)
      Console.WriteLine($" * Total Nodes      : {_context.Nodes.Count()}");
      Console.WriteLine($" * Total Elements   : {_context.Elements.Count()}");
      Console.WriteLine($" * Total Properties : {_context.Properties.Count()}");
      Console.WriteLine($" * Total Materials  : {_context.Materials.Count()}");
      Console.WriteLine();

      // 2. 상세 출력
      PrintMaterials();
      PrintProperties(limit);
      PrintNodes(limit);
      PrintElements(limit); // 가장 중요: RawData 매핑 확인

      ResetColor();
      Console.WriteLine("End of Debug Report.");
      Console.WriteLine("-------------------------------------------------------------------------------");
    }

    private void PrintMaterials()
    {
      PrintSectionHeader("1. Materials (All)");
      Console.WriteLine($"| {"ID",3} | {"Name",-10} | {"E (MPa)",10} | {"Density",10} |");
      Console.WriteLine(new string('-', 45));

      foreach (var kvp in _context.Materials)
      {
        var m = kvp.Value;
        Console.WriteLine($"| {kvp.Key,3} | {m.Name,-10} | {m.E,10:F0} | {m.Rho,10:E2} |");
      }
      Console.WriteLine();
    }

    private void PrintProperties(int limit)
    {
      PrintSectionHeader($"2. Properties (Top {limit})");
      Console.WriteLine($"| {"ID",3} | {"Type",-4} | {"MatID",5} | {"Dimensions",-30} |");
      Console.WriteLine(new string('-', 55));

      int count = 0;
      foreach (var kvp in _context.Properties)
      {
        if (limit != -1 && count++ >= limit) break;
        var p = kvp.Value;
        string dims = string.Join(", ", p.Dim.Select(d => d.ToString("0.0")));
        Console.WriteLine($"| {kvp.Key,3} | {p.Type,-4} | {p.MaterialID,5} | {dims,-30} |");
      }
      if (limit != -1 && _context.Properties.Count() > limit) Console.WriteLine($"... ({_context.Properties.Count() - limit} more properties)");
      Console.WriteLine();
    }

    private void PrintNodes(int limit)
    {
      PrintSectionHeader($"3. Nodes (Top {limit})");
      Console.WriteLine($"| {"ID",5} | {"X",10} | {"Y",10} | {"Z",10} |");
      Console.WriteLine(new string('-', 45));

      int count = 0;
      foreach (var kvp in _context.Nodes)
      {
        if (limit != -1 && count++ >= limit) break;
        var n = kvp.Value;
        Console.WriteLine($"| {kvp.Key,5} | {n.X,10:F2} | {n.Y,10:F2} | {n.Z,10:F2} |");
      }
      if (limit != -1 && _context.Nodes.Count() > limit) Console.WriteLine($"... ({_context.Nodes.Count() - limit} more nodes)");
      Console.WriteLine();
    }

    private void PrintElements(int limit)
    {
      PrintSectionHeader($"4. Elements (Top {limit}) - Check Raw Mapping");
      // 헤더: FE 정보 | RawData 매핑 정보
      Console.WriteLine($"| {"ID",5} | {"Prop",4} | {"Nodes",-10} || {"RawType",-8} | {"RawID",5} | {"FeType",-6} |");
      Console.WriteLine(new string('-', 75));

      int count = 0;
      foreach (var kvp in _context.Elements)
      {
        if (limit != -1 && count++ >= limit) break;

        var e = kvp.Value;
        string nodes = $"{e.NodeIDs.FirstOrDefault()},{e.NodeIDs.LastOrDefault()}";

        // ExtraData 안전하게 가져오기
        string rawType = GetExtra(e, "RawType");
        string rawID = GetExtra(e, "ID");
        string feType = GetExtra(e, "FeType");

        Console.WriteLine($"| {kvp.Key,5} | {e.PropertyID,4} | {nodes,-10} || {rawType,-8} | {rawID,5} | {feType,-6} |");
      }
      if (limit != -1 && _context.Elements.Count() > limit) Console.WriteLine($"... ({_context.Elements.Count() - limit} more elements)");
      Console.WriteLine();
    }

    // 헬퍼 메서드들
    private string GetExtra(Element e, string key)
        => (e.ExtraData != null && e.ExtraData.ContainsKey(key)) ? e.ExtraData[key] : "-";

    private void PrintHeader(string title)
    {
      Console.ForegroundColor = ConsoleColor.Cyan;
      Console.WriteLine("===============================================================================");
      Console.WriteLine($"   {title}");
      Console.WriteLine("===============================================================================");
      ResetColor();
    }

    private void PrintSectionHeader(string subTitle)
    {
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine($"[{subTitle}]");
      ResetColor();
    }

    private void ResetColor() => Console.ResetColor();
  }
}
