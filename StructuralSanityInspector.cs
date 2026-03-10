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
    // ★ 수정: 반환형을 void에서 List<int>로 변경
    public static List<int> Inspect(FeModelContext context, bool useExplicitWeldSpc, bool pipelineDebug, bool verboseDebug)
    {
      {
        // 1. 기하학적 형상 검사 (Geometry)
        double shortElementDistanceThreshold = 1.0;
        InspectGeometry(context, shortElementDistanceThreshold, pipelineDebug, verboseDebug);

        // 2. Equivalence 검사
        double EquivalenceTolerance = 0.1;
        InspectEquivalence(context, EquivalenceTolerance, pipelineDebug, verboseDebug);

        // 3. Duplicate 검사
        InspectDuplicate(context, pipelineDebug, verboseDebug);

        // 4. 데이터 결성 검사
        InspectIntegrity(context, pipelineDebug, verboseDebug);

        // 5. 고립 요소 검사
        InspectIsolation(context, pipelineDebug, verboseDebug);

        // ★ [삭제됨] 매 스테이지마다 강체를 지우면 안 되므로 여기서 호출하던 부분 제거!

        // 6. 위상학적 연결성 검사
        List<int> freeEndNodes = InspectTopology(context, useExplicitWeldSpc, pipelineDebug, verboseDebug);
        InspectRigidDependencies(context, pipelineDebug);

        return freeEndNodes;
      }
    }

    // ★ [수정됨] 외부(Pipeline)에서 파이프라인 전체 종료 후 단 1번 호출할 수 있도록 public으로 변경


    private static List<int> InspectTopology(FeModelContext context, bool useExplicitWeldSpc, bool pipelineDebug, bool verboseDebug)
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

      // 02. RBE 및 PointMass에서 사용 중인 노드 일괄 수집
      var usedInRbeOrMass = new HashSet<int>();
      foreach (var kvp in context.Rigids)
      {
        var rbe = kvp.Value;
        usedInRbeOrMass.Add(rbe.IndependentNodeID);
        foreach (var dep in rbe.DependentNodeIDs) usedInRbeOrMass.Add(dep);
      }
      foreach (var kvp in context.PointMasses)
      {
        var pm = kvp.Value;
        usedInRbeOrMass.Add(pm.NodeID);
      }

      // B. 미사용 노드 (Degree = 0) 탐색 및 삭 (단, RBE나 Mass에서 쓰이는 노드는 보호)
      var isolatedNodes = context.Nodes.Keys
          .Where(id => (!nodeDegree.TryGetValue(id, out var deg) || deg == 0) && !usedInRbeOrMass.Contains(id))
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

      int removedOrphans = RemoveOrphanNodes(context, isolatedNodes);
      if (pipelineDebug && removedOrphans > 0)
      {
        Console.WriteLine($"      [자동 정리] 사용되지 않는 고립 노드 {removedOrphans}개를 즉시 삭제했습니다.");
      }

      // ===================================================================================
      // A. SPC 대상 추출 및 충돌 방지 로직 (하이브리드 모드)
      // ===================================================================================
      var rawSpcTargets = new HashSet<int>();

      // FeModelContext에 WeldNodes 딕셔너리/해시셋이 구성되어 있다고 가정합니다.
      bool effectiveUseExplicitWeldSpc = useExplicitWeldSpc && context.WeldNodes.Count > 0;

      // [Step 1] 타겟 후보군 추출
      if (effectiveUseExplicitWeldSpc)
      {
        // Mode 2: 명시적 용접점(Weld) 수집
        foreach (var nid in context.WeldNodes) rawSpcTargets.Add(nid);
      }
      else
      {
        // Mode 1: 자유단(Free Node) 수집
        var endNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        foreach (var node in endNodes) rawSpcTargets.Add(node);
      }

      // [Step 2] 핵심 힐링 - Dependent Node에 SPC가 들어가는 치명적 류 방지
      var finalSpcList = new HashSet<int>();

      foreach (int targetNode in rawSpcTargets)
      {
        int currentNode = targetNode;
        bool isDependent;

        // ★ [신규 추가] '최상위 대장' 마스터 노드를 찾을 때까지 무한 추적 (while문 도입)
        // (A -> B -> C 구조에서 C에 SPC를 주려 할 때, 최종적으로 A를 찾아냄)
        int loopGuard = 0; // 무한 루프 방지용 안전장치
        do
        {
          isDependent = false;
          foreach (var kvp in context.Rigids)
          {
            var rbe = kvp.Value;
            if (rbe.DependentNodeIDs.Contains(currentNode))
            {
              isDependent = true;
              currentNode = rbe.IndependentNodeID; // 윗선(마스터)으로 타고 올라감
              break;
            }
          }
          loopGuard++;
        } while (isDependent && loopGuard < 100);

        if (loopGuard >= 100)
        {
          LogWarning($"[경고] N{targetNode}에서 무한 꼬리물기(Circular Dependency)가 감지되었습니다! SPC 할당에서 제외합니다.");
          continue;
        }

        // 최상위 대장을 찾았거나, 처음부터 자기가 대장이었다면 그 노드에 SPC를 줍니다.
        finalSpcList.Add(currentNode);
      }

      var resultList = finalSpcList.ToList();

      if (pipelineDebug)
      {
        string modeStr = effectiveUseExplicitWeldSpc ? "명시적 용접점(Weld)" : "자유단(Free Node)";
        if (resultList.Count == 0)
        {
          LogPass($"02_A - SPC 지정 : {modeStr} 기반으로 지정할 노드가 없습니다.");
        }
        else
        {
          LogWarning($"02_A - SPC 지정 : {modeStr} 기반으로 {resultList.Count}개의 노드를 지정했습니다. (종속 충돌 안전처리 완료)");
          Console.WriteLine($"      IDs: {SummarizeIds(resultList, printLimit)}");
        }
      }

      return resultList;
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

          // ★ verboseDebug가 켜져 있을 때만 노드 연결 상태까지 세 출력!
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
      int deletedCount = 0;

      // ★ [추가됨] 중복 요소가 발견되면 첫 번째 요소만 남기고 나머지는 모델에서 영구 삭제
      if (duplicateGroups.Count > 0)
      {
        foreach (var group in duplicateGroups)
        {
          for (int i = 1; i < group.Count; i++) // 인덱스 1부터 삭제 (0번은 보존)
          {
            if (context.Elements.Contains(group[i]))
            {
              context.Elements.Remove(group[i]);
              deletedCount++;
            }
          }
        }
      }

      // ★ 출력은 pipelineDebug가 true일 때만 수행합니다.
      if (pipelineDebug)
      {
        if (duplicateGroups.Count == 0)
        {
          LogPass("05 - 요소 중복 : 완전히 겹치는 중복 요소가 없습니다.");
          return;
        }

        LogCritical($"05 - 요소 중복 : 노드 구성이 동일한 중복 요소 세트가 {duplicateGroups.Count}개 발견되었습니다! (잉여 중복 부재 {deletedCount}개 자동 삭제됨)");

        // verboseDebug에 따라 출력 개수 조절
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

    /// <summary>
    /// 종속 노드(Dependent Node)를 찾지 못해 비어있는(Count == 0) 불량 강체를
    /// 기문으로 따로 모아서 출력하고, 프로그램이 뻗지 않도록 모델에서 삭제합니다.
    /// </summary>
    public static void InspectRigidIntegrity(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      var emptyRbeInfos = new List<(int RbeId, int IndepNodeId)>();

      foreach (var kvp in context.Rigids)
      {
        var rbe = kvp.Value;
        if (rbe.DependentNodeIDs == null || rbe.DependentNodeIDs.Count == 0)
        {
          emptyRbeInfos.Add((kvp.Key, rbe.IndependentNodeID));
        }
      }

      int removedCount = 0;
      foreach (var info in emptyRbeInfos)
      {
        context.Rigids.Remove(info.RbeId);
        removedCount++;
      }

      if (pipelineDebug)
      {
        if (emptyRbeInfos.Count == 0)
        {
          LogPass("06 - 강체(RBE) 무결성 : 모든 강체가 정상적으로 연결 대상을 찾았습니다.");
        }
        else
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"[경고/정리] 06 - 강체(RBE) 무결성 : 타겟을 찾지 못해 DEP가 비어있는 불량 강체 {removedCount}개가 발견되어 안전하게 제외되었습니다.");
          Console.ResetColor();

          int printLimit = verboseDebug ? int.MaxValue : 100;

          // [변경됨] HyperMesh에서 한 번에 복사/붙여넣기 쉽도록 Independent Node ID만 순수하게 추출
          var nodeIdsOnly = emptyRbeInfos
              .Take(printLimit)
              .Select(info => info.IndepNodeId.ToString());

          // 쉼표와 공백으로 연결 (HyperMesh 입력창 규격 대응)
          string displayStr = string.Join(", ", nodeIdsOnly);

          if (emptyRbeInfos.Count > printLimit)
          {
            displayStr += ", ...";
          }

          // 드래그하기 쉽도록 별도의 라인에 노드 번호들만 출력합니다.
          Console.WriteLine($"      [HyperMesh 검색용] 누락된 U-Bolt(또는 밸브)의 Independent Node IDs:");
          Console.WriteLine($"      {displayStr}");
        }
      }
    }

    /// <summary>
    /// NASTRAN FATAL 6202 (MCE1) 에러의 주범인 '다중 종속(Double Dependency)'과 
    /// '순환 종속(Circular Dependency)'을 추적하여 콘솔에 범인(Node, RBE)을 출력합니다.
    /// </summary>
    public static void InspectRigidDependencies(FeModelContext context, bool pipelineDebug)
    {
      if (!pipelineDebug) return;

      bool hasError = false;
      var depNodeToRbes = new Dictionary<int, List<int>>();
      var indepToDeps = new Dictionary<int, HashSet<int>>();

      // 1. RBE 종속성 맵 구축
      foreach (var kvp in context.Rigids)
      {
        int rbeId = kvp.Key;
        var rbe = kvp.Value;

        if (!indepToDeps.ContainsKey(rbe.IndependentNodeID))
          indepToDeps[rbe.IndependentNodeID] = new HashSet<int>();

        foreach (var depNode in rbe.DependentNodeIDs)
        {
          // 다중 종속 추적용
          if (!depNodeToRbes.ContainsKey(depNode))
            depNodeToRbes[depNode] = new List<int>();
          depNodeToRbes[depNode].Add(rbeId);

          // 순환 종속 추적용 (방향성: 마스터 -> 슬레이브)
          indepToDeps[rbe.IndependentNodeID].Add(depNode);
        }
      }

      // 2. 다중 종속 (Double Dependency) 범인 색출
      var doubleDeps = depNodeToRbes.Where(kv => kv.Value.Count > 1).ToList();
      if (doubleDeps.Count > 0)
      {
        LogCritical($"[치명적 오류 탐지] 다중 종속(Double Dependency) 발생! 노드가 여러 마스터에게 지배받고 습니다.");
        foreach (var kvp in doubleDeps.Take(10))
        {
          Console.WriteLine($"      -> Node {kvp.Key}번은 다음 RBE들의 슬레이브로 겹쳐있습니다: {string.Join(", ", kvp.Value)}");
        }
        hasError = true;
      }

      // 3. 순환 종속 (Circular Dependency) 꼬리물기 색출 (DFS 탐색)
      var visited = new HashSet<int>();
      var recursionStack = new HashSet<int>();

      bool DetectCycle(int node, List<int> path)
      {
        if (recursionStack.Contains(node))
        {
          path.Add(node);
          return true; // 순환 꼬리물기 발견!
        }
        if (visited.Contains(node)) return false;

        visited.Add(node);
        recursionStack.Add(node);
        path.Add(node);

        if (indepToDeps.ContainsKey(node))
        {
          foreach (var neighbor in indepToDeps[node])
          {
            if (DetectCycle(neighbor, path)) return true;
          }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(node);
        return false;
      }

      foreach (var node in indepToDeps.Keys)
      {
        var path = new List<int>();
        if (DetectCycle(node, path))
        {
          LogCritical($"[치명적 오류 탐지] 강체 순환 꼬리물기(Circular Dependency) 발생! (FATAL 6202 주범)");
          Console.WriteLine($"      -> 꼬리물기 경로: {string.Join(" -> ", path)}");
          hasError = true;
          break; // 하나만 찾아도 치명적이므로 탈출
        }
      }

      if (!hasError)
      {
        LogPass("08 - 강체 역학망 검사 : 다중 종속 및 순환 종속(꼬리물기)이 전혀 없는 깨끗한 상태입니다.");
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
