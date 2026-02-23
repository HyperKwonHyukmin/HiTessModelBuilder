using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.Preprocess
{
  public static class StructuralSanityInspector
  {

    // ★ 최적화 1: void 대신 List<int>를 반환하여 계산된 자유단 노드를 밖으로 전달
    public static void Inspect(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      // 1. 위상학적 연결성 검사 (Topology)
      List<int> freeEndNodes = new List<int>();
      freeEndNodes =  InspectTopology(context, pipelineDebug:false, verboseDebug:false);

      // 2. 기하학적 형상 검사 (Geometry)
      double ShortElementDistanceThreshold = 0.1;
      InspectGeometry(context, ShortElementDistanceThreshold:1.0, pipelineDebug:false, verboseDebug:false);
    }

    private static List<int> InspectTopology(FeModelContext context, bool pipelineDebug, bool verboseDebug)
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

      int printLimit = verboseDebug ? int.MaxValue : 30;

      if (pipelineDebug)
      {
        PrintNodeStat("02_A - 자유단 노드 (연결 1개)", endNodes, printLimit);
      }

      // B. 미사용 노드 (Degree = 0)
      // ★ 최적화 2: GetAllNodes() 대신 가벼운 Keys 컬렉션을 사용하여 메모리 절약
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

      // C. 고립 노드 삭제
      int removedOrphans = RemoveOrphanNodes(context, isolatedNodes);
      if (pipelineDebug && removedOrphans > 0)
      {
        Console.WriteLine($"      [자동 정리] 사용되지 않는 고립 노드 {removedOrphans}개를 즉시 삭제했습니다.");
      }

      return endNodes;
    }

    private static void InspectGeometry(FeModelContext context, double ShortElementDistanceThreshold,
      bool pipelineDebug, bool verboseDebug)
    {
      var shortElements = ElementDetectShortInspector.Run(context, ShortElementDistanceThreshold);

      if (shortElements.Count == 0)
      {
        LogPass("03 - 기하 형상 : 너무 짧은 요소가 없습니다.");
      }
      else
      {
        LogWarning($"03 - 기하 형상 : 길이가 {ShortElementDistanceThreshold} 미만인 짧은 요소가 {shortElements.Count}개 발견되었습니다.");
        if (opt.DebugMode)
        {
          var elementIds = shortElements.Select(t => t.eleId).ToList();
          Console.WriteLine($"      IDs: {SummarizeIds(elementIds, opt)}");
        }
      }
    }

    // ★ 최적화 3: 불필요한 Element 순회(Any, Contains)를 완전히 제거하여 O(1) 삭제 달성
    private static int RemoveOrphanNodes(FeModelContext context, List<int> isolatedNodes)
    {
      if (isolatedNodes == null || isolatedNodes.Count == 0) return 0;

      int removed = 0;
      foreach (var nid in isolatedNodes)
      {
        // 이미 Degree 분석에서 연결이 없다는 것이 증명되었으므로 묻지도 따지지도 않고 바로 삭제합니다.
        if (context.Nodes.Contains(nid))
        {
          context.Nodes.Remove(nid);
          removed++;
        }
      }
      return removed;
    }

    // --- 아래 헬퍼 메서드들은 기존과 동일 ---
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
