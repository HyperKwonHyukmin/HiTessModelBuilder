using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.Preprocess
{
  public static class StructuralSanityInspector
  {
    public static void Inspect(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      // 1. 위상학적 연결성 검사 (Topology)
      // ★ 밖에서 받아온 플래그를 그대로 넘겨줍니다.
      List<int> freeEndNodes = InspectTopology(context, pipelineDebug, verboseDebug);

      // 2. 기하학적 형상 검사 (Geometry)
      double shortElementDistanceThreshold = 1.0;
      InspectGeometry(context, shortElementDistanceThreshold, pipelineDebug, verboseDebug);
    }

    private static List<int> InspectTopology(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      var connectedGroups = ElementConnectivityInspector.FindConnectedElementGroups(context.Elements);
      if (pipelineDebug)
      {
        if (connectedGroups.Count <= 1)
          LogPass($"01 - 위상 연결성 : 전체 모델이 {connectedGroups.Count}개의 그룹으로 잘 연결되어 있습니다.");
        else
          LogWarning($"01 - 위상 연결성 : 모델이 {connectedGroups.Count}개의 분리된 덩어리로 나뉘어 있습니다.");
      }

      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      var endNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();

      // ★ 상세 출력 제어: verbose가 켜져 있으면 전부 출력, 아니면 5개만 깔끔하게 출력
      int printLimit = verboseDebug ? int.MaxValue : 5;

      if (pipelineDebug)
      {
        PrintNodeStat("02_A - 자유단 노드 (연결 1개)", endNodes, printLimit);
      }

      var isolatedNodes = context.Nodes.Keys
          .Where(id => !nodeDegree.TryGetValue(id, out var deg) || deg == 0)
          .ToList();

      if (pipelineDebug)
      {
        if (isolatedNodes.Count > 0)
          PrintNodeStat("02_B - 고립된 노드 (연결 0개)", isolatedNodes, printLimit);
        else
          Console.WriteLine("02_B - 고립된 노드 (연결 0개) 없음");
      }

      int removedOrphans = RemoveOrphanNodes(context, isolatedNodes);
      if (pipelineDebug && removedOrphans > 0)
      {
        Console.WriteLine($"      [자동 정리] 사용되지 않는 고립 노드 {removedOrphans}개를 즉시 삭제했습니다.");
      }

      return endNodes;
    }

    private static void InspectGeometry(FeModelContext context, double threshold, bool pipelineDebug, bool verboseDebug)
    {
      // 검사는 무조건 수행합니다.
      var shortElements = ElementDetectShortInspector.Run(context, threshold);

      // 출력은 pipelineDebug가 true일 때만 합니다.
      if (pipelineDebug)
      {
        if (shortElements.Count == 0)
        {
          LogPass($"03 - 기하 형상 : 길이가 {threshold} 미만인 짧은 요소가 없습니다.");
        }
        else
        {
          LogWarning($"03 - 기하 형상 : 길이가 {threshold} 미만인 짧은 요소가 {shortElements.Count}개 발견되었습니다.");

          int printLimit = verboseDebug ? int.MaxValue : 5;
          var elementIds = shortElements.Select(t => t.eleId).ToList();

          // 기본 요약 출력
          Console.WriteLine($"      IDs: {SummarizeIds(elementIds, printLimit)}");

          // ★ verboseDebug가 켜져 있을 때만 노드 연결 상태까지 상세 출력!
          if (verboseDebug)
          {
            Console.WriteLine("      [상세 요소 정보]");
            foreach (var e in shortElements)
            {
              Console.WriteLine($"        -> ELE {e.eleId} : Nodes [{e.n1}, {e.n2}]");
            }
          }
        }
      }
    }

    private static int RemoveOrphanNodes(FeModelContext context, List<int> isolatedNodes)
    {
      if (isolatedNodes == null || isolatedNodes.Count == 0) return 0;
      int removed = 0;
      foreach (var nid in isolatedNodes)
      {
        if (context.Nodes.Contains(nid))
        {
          context.Nodes.Remove(nid);
          removed++;
        }
      }
      return removed;
    }

    // --- 헬퍼 메서드 ---
    private static void PrintNodeStat(string title, List<int> nodes, int limit)
    {
      if (nodes.Count == 0) return;
      Console.WriteLine($"{title} : {nodes.Count}개 발견");
      Console.WriteLine($"      IDs: {SummarizeIds(nodes, limit)}");
    }

    private static void LogPass(string msg) => Console.WriteLine($"[통과] {msg}");

    private static void LogWarning(string msg)
    {
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine($"[주의] {msg}");
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
