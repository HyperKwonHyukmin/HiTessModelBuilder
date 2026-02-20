using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder.Pipeline.Preprocess
{
  public static class StructuralSanityInspector
  {
    public static void Inspect(FeModelContext context, bool pipelineDebug)
    {
      List<int> freeEndNodes = new List<int>();

      freeEndNodes = InspectTopology(context, pipelineDebug);
    }


    private static List<int> InspectTopology(
        FeModelContext context, bool pipelineDebug)
    {
      // 01. Element 그룹 연결성 확인
      var connectedGroups = ElementConnectivityInspector.FindConnectedElementGroups(context.Elements);
      if (pipelineDebug)
      {
        if (connectedGroups.Count <= 1)
          LogPass($"01 - 위상 연결성 : 전체 모델이 {connectedGroups.Count}개의 그룹으로 잘 연결되어 있습니다.");
        else
          LogWarning($"01 - 위상 연결성 : 모델이 {connectedGroups.Count}개의 분리된 덩어리로 나뉘어 있습니다. (의도치 않은 분리 주의)");
      }

      // 02. 노드 사용 빈도(Degree) 분석
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);

      // A. 자유단 노드 (Degree = 1) -> SPC 생성 대상
      var endNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();

      if (pipelineDebug)
      {
        PrintNodeStat("02_A - 자유단 노드 (연결 1개)", endNodes, int.MaxValue);
      }

      // B. 미사용 노드 (Degree = 0)
      var isolatedNodes = context.Nodes.GetAllNodes()
          .Select(kv => kv.Key)
          .Where(id => !nodeDegree.TryGetValue(id, out var deg) || deg == 0)
          .ToList();

      if (pipelineDebug)
      {
        if (isolatedNodes.Count > 0)
        {
          PrintNodeStat("02_B - 고립된 노드 (연결 0개)", isolatedNodes, int.MaxValue);
        }
        else
        {
          Console.WriteLine("02_B - 고립된 노드 없음");
        }
      }
      int removedOrphans = RemoveOrphanNodesByElementConnection(context, isolatedNodes);
      if (pipelineDebug)
      {
        if (removedOrphans > 0)
          Console.WriteLine($"      [자동 정리] 사용되지 않는 고립 노드 {removedOrphans}개를 삭제했습니다.");
      }
      return endNodes;
    }

    private static int RemoveOrphanNodesByElementConnection(FeModelContext context, List<int> isolatedNodes)
    {
      if (isolatedNodes == null || isolatedNodes.Count == 0) return 0;
      int removed = 0;
      foreach (var nid in isolatedNodes)
      { 
        if (!context.Nodes.Contains(nid)) continue;

        bool referenced = context.Elements.Any(kv => kv.Value.NodeIDs.Contains(nid));
        if (!referenced)
        {
          context.Nodes.Remove(nid);
          removed++;
        }
      }
      return removed;
    }

    private static void PrintNodeStat(string title, List<int> nodes, int limit)
    {
      if (nodes.Count == 0) return;

      string msg = $"{title} : {nodes.Count}개 발견";
      Console.WriteLine(msg);

      if (nodes.Count > 0)
      {
        Console.WriteLine($"      IDs: {SummarizeIds(nodes, limit)}");
      }
    }

    private static void LogPass(string msg)
    {
      Console.WriteLine($"[통과] {msg}");
    }

    private static void LogWarning(string msg)
    {
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine($"[주의] {msg}");
      Console.ResetColor();
    }

    private static void LogCritical(string msg)
    {
      Console.ForegroundColor = ConsoleColor.Red;
      Console.WriteLine($"[실패] {msg}");
      Console.ResetColor();
    }
    private static string SummarizeIds(List<int> ids, int limit)
    {
      if (ids == null || ids.Count == 0) return "";
      var subset = ids.Take(limit);
      string str = string.Join(", ", subset);
      if (ids.Count > limit) str += ", ...";
      return str;
    }
  }
}
