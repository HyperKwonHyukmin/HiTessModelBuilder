using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.NodeInspector;
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

      // 3. Equivalence 검사 (노드 중복)
      double EquivalenceTolerance = 0.1;
      InspectEquivalence(context, EquivalenceTolerance, pipelineDebug, verboseDebug);

      // 4. Duplicate 검사 (요소 중복)
      InspectDuplicate(context, pipelineDebug, verboseDebug);

      // 5. 데이터 무결성 검사 
      InspectIntegrity(context, pipelineDebug, verboseDebug);

      // 6. 고립 요소 검사 (Isolation)
      InspectIsolation(context, pipelineDebug, verboseDebug);



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
          LogWarning($"01 - 위상 연결성 : 모델이 {connectedGroups.Count}개의 분리된 덩어리로 나뉘어 있습니다.");
      }

      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      int printLimit = verboseDebug ? int.MaxValue : 5;

      // A. 자유단 노드 (Degree = 1) -> SPC 생성 대상
      var endNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
      if (pipelineDebug)
      {
        if (endNodes.Count == 0)
        {
          LogPass("02_A - 자유단 노드 (연결 1개) : 발견되지 않았습니다.");
        }
        else
        {
          // 자유단 노드는 SPC 대상이므로 상황을 인지할 수 있게 [주의] 혹은 [안내] 격으로 노출합니다.
          LogWarning($"02_A - 자유단 노드 (연결 1개) : {endNodes.Count}개 발견 (SPC 자동 생성 대상)");
          Console.WriteLine($"      IDs: {SummarizeIds(endNodes, printLimit)}");
        }
      }

      // B. 미사용 노드 (Degree = 0)
      var isolatedNodes = context.Nodes.Keys
          .Where(id => !nodeDegree.TryGetValue(id, out var deg) || deg == 0)
          .ToList();

      if (pipelineDebug)
      {
        if (isolatedNodes.Count == 0)
        {
          LogPass("02_B - 고립된 노드 (연결 0개) : 없습니다. (모든 노드가 정상 연결됨)");
        }
        else
        {
          LogWarning($"02_B - 고립된 노드 (연결 0개) : {isolatedNodes.Count}개 발견 (자동 삭제 예정)");
          Console.WriteLine($"      IDs: {SummarizeIds(isolatedNodes, printLimit)}");
        }
      }

      // C. 고립 노드 삭제
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

    private static void InspectEquivalence(FeModelContext context, double EquivalenceTolerance,
          bool pipelineDebug, bool verboseDebug)
    {
      // 검사는 무조건 백그라운드에서 수행합니다 (향후 자동 병합 기능 등을 위해)
      var coincidentGroups = NodeEquivalenceInspector.InspectEquivalenceNodes(context, EquivalenceTolerance);

      // ★ 출력은 pipelineDebug가 켜져 있을 때만 수행합니다.
      if (pipelineDebug)
      {
        if (coincidentGroups.Count == 0)
        {
          LogPass($"04 - 노드 중복 : 허용오차({EquivalenceTolerance}) 내에 겹치는 노드가 없습니다.");
          return;
        }

        LogWarning($"04 - 노드 중복 : 위치가 겹치는 노드 그룹이 {coincidentGroups.Count}개 발견되었습니다.");

        // ★ verboseDebug에 따라 출력 개수 조절 (상세=전부, 아니면 5개)
        int printLimit = verboseDebug ? int.MaxValue : 5;
        int shown = 0;

        foreach (var group in coincidentGroups.Take(printLimit))
        {
          shown++;
          int repID = group.FirstOrDefault();
          string ids = string.Join(", ", group);

          if (context.Nodes.Contains(repID))
          {
            var node = context.Nodes[repID];
            Console.WriteLine($"      그룹 {shown}: IDs [{ids}] 위치 ({node.X:F1}, {node.Y:F1}, {node.Z:F1})");
          }
        }

        if (coincidentGroups.Count > printLimit)
        {
          Console.WriteLine($"      ... (총 {coincidentGroups.Count}개 그룹 중 {printLimit}개만 출력됨. 상세 출력은 verboseDebug 켜기)");
        }
      }
    }

    private static void InspectDuplicate(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      // 검사는 무조건 수행합니다.
      var duplicateGroups = ElementDuplicateInspector.FindDuplicateGroups(context);

      // ★ 출력은 pipelineDebug가 true일 때만 수행합니다.
      if (pipelineDebug)
      {
        if (duplicateGroups.Count == 0)
        {
          LogPass("05 - 요소 중복 : 완전히 겹치는 중복 요소가 없습니다.");
          return;
        }

        LogCritical($"05 - 요소 중복 : 노드 구성이 동일한 중복 요소 세트가 {duplicateGroups.Count}개 발견되었습니다!");

        // ★ verboseDebug에 따라 출력 개수 조절
        int printLimit = verboseDebug ? int.MaxValue : 5;
        int count = 0;

        foreach (var group in duplicateGroups.Take(printLimit))
        {
          count++;
          Console.WriteLine($"      세트 #{count}: [Element IDs: {string.Join(", ", group)}]");
        }

        if (duplicateGroups.Count > printLimit)
        {
          Console.WriteLine($"      ... (총 {duplicateGroups.Count}개 세트 중 {printLimit}개만 출력됨. 상세 출력은 verboseDebug 켜기)");
        }
      }
    }

    private static void InspectIntegrity(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      // 1. 불량 요소 탐색 (검사는 무조건 수행)
      var invalidElements = ElementIntegrityInspector.FindElementsWithInvalidReference(context);

      // 2. 요소 자동 삭제 (복구도 무조건 수행)
      int deletedCount = 0;
      if (invalidElements.Count > 0)
      {
        foreach (var eid in invalidElements)
        {
          if (context.Elements.Contains(eid))
          {
            context.Elements.Remove(eid);
            deletedCount++;
          }
        }
      }

      // ★ 3. 출력은 pipelineDebug가 true일 때만 수행합니다.
      if (pipelineDebug)
      {
        if (invalidElements.Count == 0)
        {
          LogPass("06 - 데이터 무결성 : 모든 요소가 유효한 노드와 속성을 참조하고 있습니다.");
        }
        else
        {
          Console.ForegroundColor = ConsoleColor.Magenta;
          Console.WriteLine($"[복구] 06 - 데이터 무결성 : 존재하지 않는 노드/속성을 참조하는 불량 요소 {deletedCount}개를 모델에서 자동 삭제했습니다.");
          Console.ResetColor();

          // ★ verboseDebug에 따라 출력 개수 조절
          int printLimit = verboseDebug ? int.MaxValue : 5;
          Console.WriteLine($"      삭제된 IDs: {SummarizeIds(invalidElements, printLimit)}");
        }
      }
    }

    private static void InspectIsolation(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      // 검사는 백그라운드에서 무조건 수행합니다.
      var isolation = ElementIsolationInspector.FindIsolatedElements(context);

      // 출력은 pipelineDebug가 활성화되었을 때만 수행합니다.
      if (pipelineDebug)
      {
        if (isolation.Count == 0)
        {
          LogPass("07 - 요소 고립 : 고립된(연결되지 않은) 요소가 없습니다.");
          return;
        }

        LogWarning($"07 - 요소 고립 : 메인 구조물과 연결되지 않은 고립 요소가 {isolation.Count}개 발견되었습니다.");

        // verboseDebug에 따라 출력할 Element ID 개수 조절 (상세=전부, 아니면 기본 5개)
        int printLimit = verboseDebug ? int.MaxValue : 5;
        Console.WriteLine($"      고립된 Element IDs: {SummarizeIds(isolation, printLimit)}");
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
    private static void LogPass(string msg) => Console.WriteLine($"[통과] {msg}");

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
