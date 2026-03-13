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
    public static List<int> Inspect(FeModelContext context, bool useExplicitWeldSpc, bool pipelineDebug, bool verboseDebug, bool isFinalStage = false)
    {
      double shortElementDistanceThreshold = 1.0;
      InspectGeometry(context, shortElementDistanceThreshold, pipelineDebug, verboseDebug);

      double EquivalenceTolerance = 0.1;
      InspectEquivalence(context, EquivalenceTolerance, pipelineDebug, verboseDebug);

      InspectDuplicate(context, pipelineDebug, verboseDebug);
      InspectIntegrity(context, pipelineDebug, verboseDebug);
      InspectIsolation(context, pipelineDebug, verboseDebug);

      // 위상 연결성 및 경계조건(SPC) 산출
      List<int> freeEndNodes = InspectTopology(context, useExplicitWeldSpc, pipelineDebug, verboseDebug, isFinalStage);

      InspectRigidDependencies(context, pipelineDebug);

      // ===================================================================================
      // ★ [신규 추가] 실패한 UBOLT 구제 및 Nastran 해석 에러 방지 로직
      // 연결 타겟을 찾지 못해 비어있는 UBOLT RBE는 BDF 출력 시 누락되어 Singularity를 유발합니다.
      // 마지막 Stage에서 이를 찾아내어 해당 노드를 강제로 SPC(경계조건) 리스트에 추가하고 보고합니다.
      // ===================================================================================
      if (isFinalStage)
      {
        var failedUboltNodes = new List<int>();
        foreach (var kvp in context.Rigids)
        {
          var rbe = kvp.Value;
          // 종속 노드가 없는 깡통 강체인지 확인
          if (rbe.DependentNodeIDs == null || rbe.DependentNodeIDs.Count == 0)
          {
            // 그 중에서도 생성 의도가 UBOLT였던 것만 추출
            if (rbe.ExtraData != null && rbe.ExtraData.TryGetValue("Type", out string typeStr) && typeStr == "UBOLT")
            {
              failedUboltNodes.Add(rbe.IndependentNodeID);
            }
          }
        }

        if (failedUboltNodes.Count > 0)
        {
          if (pipelineDebug)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[긴급 조치 / 해석 에러 방지]");
            Console.WriteLine($" -> 타겟 구조물을 찾지 못해 연결에 실패한 UBOLT {failedUboltNodes.Count}개가 발견되었습니다.");
            Console.WriteLine($" -> Nastran Fatal Error(Singularity) 방지를 위해 아래 배관 노드들에 SPC(고정 경계조건)를 강제 할당합니다.");

            // 노드 리스트를 보기 좋게 출력 (Verbose가 아니면 20개까지만 요약)
            int limit = verboseDebug ? int.MaxValue : 20;
            var displayNodes = failedUboltNodes.Take(limit).Select(n => $"N{n}");
            string nodeStr = string.Join(", ", displayNodes);
            if (failedUboltNodes.Count > limit) nodeStr += " ...";
            Console.WriteLine($" -> 실패한 UBOLT 대상 노드: {nodeStr}\n");

            Console.ResetColor();
          }

          // 기존에 산출된 경계조건(SPC) 리스트에 실패한 UBOLT 노드들을 병합
          var combinedSpc = new HashSet<int>(freeEndNodes);
          foreach (var fn in failedUboltNodes)
          {
            combinedSpc.Add(fn);
          }
          freeEndNodes = combinedSpc.ToList();
        }
        else
        {
          // ★ [추가됨] 실패한 UBOLT가 없을 경우(완벽 성공) 출력되는 로그
          if (pipelineDebug)
          {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[최종 점검] UBOLT 무결성 검사 : 실패한 항 없이 모든 UBOLT가 구조물에 성공적으로 연결되었습니다.\n");
            Console.ResetColor();
          }
        }
      }

      return freeEndNodes;
    }

    private static List<int> InspectTopology(FeModelContext context, bool useExplicitWeldSpc, bool pipelineDebug, bool verboseDebug, bool isFinalStage)
    {
      // Element뿐만 아니라 Rigid, PointMass까지 통합된 ConnectedComponent 검색 로직 적용
      var connectedComponents = ElementConnectivityInspector.FindConnectedComponents(context);

      if (pipelineDebug)
      {
        if (connectedComponents.Count <= 1)
          LogPass($"01 - 위상 연결성 : 전체 모델이 {connectedComponents.Count}개의 그룹으로 잘 연결되어 있습니다.");
        else
          LogWarning($"01 - 위상 연결성 : 모델이 {connectedComponents.Count}개의 분리된 덩어리로 나뉘어 있습니다.");
      }

      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      int printLimit = verboseDebug ? int.MaxValue : 5;

      var usedInRbeOrMass = new HashSet<int>();
      foreach (var kvp in context.Rigids)
      {
        var rbe = kvp.Value;
        usedInRbeOrMass.Add(rbe.IndependentNodeID);
        foreach (var dep in rbe.DependentNodeIDs) usedInRbeOrMass.Add(dep);
      }
      foreach (var kvp in context.PointMasses)
      {
        usedInRbeOrMass.Add(kvp.Value.NodeID);
      }

      var isolatedNodes = context.Nodes.Keys
          .Where(id => (!nodeDegree.TryGetValue(id, out var deg) || deg == 0) && !usedInRbeOrMass.Contains(id))
          .ToList();

      if (pipelineDebug)
      {
        if (isolatedNodes.Count == 0)
        {
          LogPass("02_B - 고립된 노드 (연결 0개) : 없습니다.");
        }
        else
        {
          LogWarning($"02_B - 고립된 노드 (연결 0개) : {isolatedNodes.Count}개 발견 (자동 삭제됨)");
          if (verboseDebug) Console.WriteLine($"      IDs: {SummarizeIds(isolatedNodes, printLimit)}");
        }
      }

      RemoveOrphanNodes(context, isolatedNodes);

      // ===================================================================================
      // A. SPC 대상 추출 및 강체(Rigid) 충돌 완벽 방지 로직
      // ===================================================================================
      var rawSpcTargets = new HashSet<int>();
      bool effectiveUseExplicitWeldSpc = useExplicitWeldSpc && context.WeldNodes.Count > 0;

      // [Step 1] 타겟 후보군 추출
      if (effectiveUseExplicitWeldSpc)
      {
        foreach (var nid in context.WeldNodes) rawSpcTargets.Add(nid);
      }
      else
      {
        var endNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        foreach (var node in endNodes) rawSpcTargets.Add(node);
      }

      // [Step 2] 모델 내의 모든 Rigid 관련 노드(Independent + Dependent) 수집
      var allRigidNodes = new HashSet<int>();
      foreach (var kvp in context.Rigids)
      {
        var rigid = kvp.Value;
        allRigidNodes.Add(rigid.IndependentNodeID);
        foreach (var dep in rigid.DependentNodeIDs) allRigidNodes.Add(dep);
      }

      // [Step 3] RBE 관련 노드에 배정된 SPC는 무조건 파기(제거)
      var finalSpcList = new HashSet<int>();
      foreach (int targetNode in rawSpcTargets)
      {
        if (!allRigidNodes.Contains(targetNode))
        {
          finalSpcList.Add(targetNode);
        }
        else if (verboseDebug)
        {
          Console.WriteLine($"      -> [SPC 제거] 노드 N{targetNode}는 강체(RBE)에 포함되어 있어 경계조건이 삭제되었습니다.");
        }
      }

      // [Step 4] 그룹별 경계조건 누락 방지 (최하단 Z축 자동 할당)
      if (isFinalStage)
      {
        double zTolerance = 10.0;

        foreach (var comp in connectedComponents)
        {
          var groupNodes = new HashSet<int>();

          // 해당 컴포넌트 내부의 든 노드 수집
          foreach (int eid in comp.ElementIDs)
            if (context.Elements.Contains(eid))
              foreach (int nid in context.Elements[eid].NodeIDs) groupNodes.Add(nid);

          foreach (int rid in comp.RigidIDs)
            if (context.Rigids.Contains(rid))
            {
              groupNodes.Add(context.Rigids[rid].IndependentNodeID);
              foreach (int nid in context.Rigids[rid].DependentNodeIDs) groupNodes.Add(nid);
            }

          foreach (int mid in comp.PointMassIDs)
            if (context.PointMasses.Contains(mid))
              groupNodes.Add(context.PointMasses[mid].NodeID);

          // 해당 덩어리 내에 경계조건(SPC)이 단 하나라도 들어갔는지 검사
          bool hasSpc = groupNodes.Any(nid => finalSpcList.Contains(nid));

          if (!hasSpc && groupNodes.Count > 0)
          {
            var validNodes = groupNodes.Where(n => context.Nodes.Contains(n)).ToList();
            if (validNodes.Count == 0) continue;

            // 바닥면 판별 및 타겟 선정
            double minZ = validNodes.Min(n => context.Nodes[n].Z);
            var bottomNodes = validNodes.Where(n => context.Nodes[n].Z <= minZ + zTolerance).ToList();

            int addedSpcCount = 0;
            // 강체(RBE) 노드를 제외한 바닥면 노드에 SPC 할당
            foreach (int targetNode in bottomNodes)
            {
              if (!allRigidNodes.Contains(targetNode))
              {
                finalSpcList.Add(targetNode);
                addedSpcCount++;
              }
            }

            if (pipelineDebug && addedSpcCount > 0)
            {
              Console.ForegroundColor = ConsoleColor.Magenta;
              Console.WriteLine($"      -> [안전망 작동] 경계조건이 누락된 독립 그룹 발견! 해당 그룹 최하단(Z={minZ:F1}) 유효 노드 {addedSpcCount}개에 SPC 강제 할당 완료.");
              Console.ResetColor();
            }
          }
        }
      }

      var resultList = finalSpcList.ToList();

      if (pipelineDebug)
      {
        string modeStr = effectiveUseExplicitWeldSpc ? "명시적 용접점(Weld)" : "자유단(Free Node)";
        if (resultList.Count == 0)
          LogPass($"02_A - SPC 지정 : {modeStr} 기반으로 지정할 노드가 없습니다.");
        else
          LogPass($"02_A - SPC 지정 : {modeStr} 기반으로 {resultList.Count}개의 노드를 지정했습니다.");
      }

      return resultList;
    }

    private static void InspectGeometry(FeModelContext context, double threshold, bool pipelineDebug, bool verboseDebug)
    {
      var shortElements = ElementDetectShortInspector.Run(context, threshold);
      if (pipelineDebug)
      {
        if (shortElements.Count == 0) LogPass($"03 - 기하 형상 : 길이가 {threshold} 미만인 짧은 요소가 없습니다.");
        else LogWarning($"03 - 기하 형상 : 짧은 요소 {shortElements.Count}개 발견.");
      }
    }

    private static void InspectEquivalence(FeModelContext context, double EquivalenceTolerance, bool pipelineDebug, bool verboseDebug)
    {
      var coincidentGroups = NodeEquivalenceInspector.InspectEquivalenceNodes(context, EquivalenceTolerance);
      if (pipelineDebug)
      {
        if (coincidentGroups.Count == 0) LogPass($"04 - 노드 중복 : 허용오차({EquivalenceTolerance}) 내에 겹치는 노드가 없습니다.");
        else LogWarning($"04 - 노드 중복 : 위치가 겹치는 노드 그룹 {coincidentGroups.Count}개 발견.");
      }
    }

    private static void InspectDuplicate(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      var duplicateGroups = ElementDuplicateInspector.FindDuplicateGroups(context);
      if (duplicateGroups.Count > 0)
      {
        foreach (var group in duplicateGroups)
        {
          for (int i = 1; i < group.Count; i++)
          {
            if (context.Elements.Contains(group[i]))
            {
              context.Elements.Remove(group[i]);
              if (verboseDebug) Console.WriteLine($"      -> [중복 삭제] 위치가 동일한 중복 부재(E{group[i]}) 삭제됨.");
            }
          }
        }
      }
      if (pipelineDebug)
      {
        if (duplicateGroups.Count == 0) LogPass("05 - 요소 중복 : 완전히 겹치는 요소가 없습니다.");
        else LogWarning($"05 - 요소 중복 : 중복 요소 세트 {duplicateGroups.Count}개 삭제 완료.");
      }
    }

    private static void InspectIntegrity(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      var invalidElements = ElementIntegrityInspector.FindElementsWithInvalidReference(context);
      if (invalidElements.Count > 0)
      {
        foreach (var eid in invalidElements)
        {
          if (context.Elements.Contains(eid))
          {
            context.Elements.Remove(eid);
            if (verboseDebug) Console.WriteLine($"      -> [불량 삭제] 유효지 않은 속성 참조 부재(E{eid}) 삭제됨.");
          }
        }
      }
      if (pipelineDebug)
      {
        if (invalidElements.Count == 0) LogPass("06 - 데이터 무결성 : 모든 요소가 유효합니다.");
        else LogWarning($"06 - 데이터 무결성 : 불량 요소 {invalidElements.Count}개 삭제 완료.");
      }
    }

    private static void InspectIsolation(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      var isolation = ElementIsolationInspector.FindIsolatedElements(context);
      if (pipelineDebug)
      {
        if (isolation.Count == 0) LogPass("07 - 요소 고립 : 고립된 요소가 없습니다.");
        else LogWarning($"07 - 요소 고립 : 메인 구조물과 끊어진 요소 {isolation.Count}개 발견.");
      }
    }

    public static void InspectRigidIntegrity(FeModelContext context, bool pipelineDebug, bool verboseDebug)
    {
      var emptyRbeInfos = new List<(int RbeId, int IndepNodeId)>();
      foreach (var kvp in context.Rigids)
      {
        if (kvp.Value.DependentNodeIDs == null || kvp.Value.DependentNodeIDs.Count == 0)
          emptyRbeInfos.Add((kvp.Key, kvp.Value.IndependentNodeID));
      }

      foreach (var info in emptyRbeInfos)
      {
        if (context.Rigids.Contains(info.RbeId)) context.Rigids.Remove(info.RbeId);
        if (verboseDebug) Console.WriteLine($"      -> [연결 실패] 타겟이 없는 불량 강체 삭제됨.");
      }

      if (pipelineDebug)
      {
        if (emptyRbeInfos.Count == 0) LogPass("08 - 강체 무결성 : 모든 강체가 정상입니다.");
        else LogWarning($"08 - 강체 무결성 : 불량 강체 {emptyRbeInfos.Count}개 삭제 완료.");
      }
    }

    public static void InspectRigidDependencies(FeModelContext context, bool pipelineDebug)
    {
      if (!pipelineDebug) return;

      bool hasError = false;
      var depNodeToRbes = new Dictionary<int, List<int>>();
      var indepToDeps = new Dictionary<int, HashSet<int>>();

      foreach (var kvp in context.Rigids)
      {
        int rbeId = kvp.Key;
        var rbe = kvp.Value;

        if (!indepToDeps.ContainsKey(rbe.IndependentNodeID))
          indepToDeps[rbe.IndependentNodeID] = new HashSet<int>();

        foreach (var depNode in rbe.DependentNodeIDs)
        {
          if (!depNodeToRbes.ContainsKey(depNode)) depNodeToRbes[depNode] = new List<int>();
          depNodeToRbes[depNode].Add(rbeId);
          indepToDeps[rbe.IndependentNodeID].Add(depNode);
        }
      }

      var doubleDeps = depNodeToRbes.Where(kv => kv.Value.Count > 1).ToList();
      if (doubleDeps.Count > 0)
      {
        LogCritical($"[치명적 오류 탐지] 다중 종속(Double Dependency) 발생!");
        hasError = true;
      }

      var visited = new HashSet<int>();
      var recursionStack = new HashSet<int>();

      bool DetectCycle(int node, List<int> path)
      {
        if (recursionStack.Contains(node)) return true;
        if (visited.Contains(node)) return false;

        visited.Add(node); recursionStack.Add(node); path.Add(node);

        if (indepToDeps.ContainsKey(node))
          foreach (var neighbor in indepToDeps[node])
            if (DetectCycle(neighbor, path)) return true;

        path.RemoveAt(path.Count - 1); recursionStack.Remove(node);
        return false;
      }

      foreach (var node in indepToDeps.Keys)
      {
        var path = new List<int>();
        if (DetectCycle(node, path))
        {
          LogCritical($"[치명적 오류 탐지] 강체 순환 꼬리물기(Circular Dependency) 발생!");
          hasError = true;
          break;
        }
      }

      if (!hasError) LogPass("09 - 강체 역학망 검사 : 다중/순환 종속이 없는 깨끗한 상태입니다.");
    }

    private static int RemoveOrphanNodes(FeModelContext context, List<int> isolatedNodes)
    {
      if (isolatedNodes == null || isolatedNodes.Count == 0) return 0;
      int removed = 0;
      foreach (var nid in isolatedNodes)
      {
        if (context.Nodes.Contains(nid)) { context.Nodes.Remove(nid); removed++; }
      }
      return removed;
    }

    private static void LogPass(string msg) => Console.WriteLine($"[통과] {msg}");
    private static void LogWarning(string msg) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"[주의] {msg}"); Console.ResetColor(); }
    private static void LogCritical(string msg) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"[실패] {msg}"); Console.ResetColor(); }

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
