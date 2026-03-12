using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  /// <summary>
  /// 물리적으로 연결된 하나의 독립된 구조물(Component) 단위를 표현합니다.
  /// </summary>
  public record ConnectedComponent(
      List<int> ElementIDs,
      List<int> RigidIDs,
      List<int> PointMassIDs
  );

  public static class ElementConnectivityInspector
  {
    /// <summary>
    /// Element, Rigid, PointMass의 노드 연결성을 모두 고려하여
    /// 물리적으로 이어진 전체 엔티티 그룹(Component)들을 반환합니다.
    /// </summary>
    public static List<ConnectedComponent> FindConnectedComponents(FeModelContext context)
    {
      var allNodeIDs = new HashSet<int>();

      // 1. 모든 사용 중인 Node ID 수집 (Values 대신 KeyValuePair 순회 후 .Value 접근)
      foreach (var kvp in context.Elements)
        foreach (var n in kvp.Value.NodeIDs) allNodeIDs.Add(n);

      foreach (var kvp in context.Rigids)
      {
        allNodeIDs.Add(kvp.Value.IndependentNodeID);
        foreach (var n in kvp.Value.DependentNodeIDs) allNodeIDs.Add(n);
      }

      foreach (var kvp in context.PointMasses)
        allNodeIDs.Add(kvp.Value.NodeID);

      if (allNodeIDs.Count == 0) return new List<ConnectedComponent>();

      // 2. Union-Find 초기화
      var uf = new UnionFind(allNodeIDs);

      // 3. Element 연결성 병합
      foreach (var kvp in context.Elements)
      {
        var nodes = kvp.Value.NodeIDs;
        for (int i = 1; i < nodes.Count; i++) uf.Union(nodes[0], nodes[i]);
      }

      // 4. Rigid(RBE) 연결성 병합 (Indep - Dep)
      foreach (var kvp in context.Rigids)
      {
        int master = kvp.Value.IndependentNodeID;
        foreach (int slave in kvp.Value.DependentNodeIDs) uf.Union(master, slave);
      }

      // 5. 그룹핑 (Root Node 기반 분류)
      var elementGroups = new Dictionary<int, List<int>>();
      var rigidGroups = new Dictionary<int, List<int>>();
      var massGroups = new Dictionary<int, List<int>>();

      foreach (var kvp in context.Elements)
      {
        int root = uf.Find(kvp.Value.NodeIDs[0]);
        if (!elementGroups.ContainsKey(root)) elementGroups[root] = new List<int>();
        elementGroups[root].Add(kvp.Key);
      }

      foreach (var kvp in context.Rigids)
      {
        int root = uf.Find(kvp.Value.IndependentNodeID);
        if (!rigidGroups.ContainsKey(root)) rigidGroups[root] = new List<int>();
        rigidGroups[root].Add(kvp.Key);
      }

      foreach (var kvp in context.PointMasses)
      {
        int root = uf.Find(kvp.Value.NodeID);
        if (!massGroups.ContainsKey(root)) massGroups[root] = new List<int>();
        massGroups[root].Add(kvp.Key);
      }

      // 6. 결과 조합
      var allRoots = elementGroups.Keys.Union(rigidGroups.Keys).Union(massGroups.Keys).Distinct();
      var result = new List<ConnectedComponent>();

      foreach (var root in allRoots)
      {
        result.Add(new ConnectedComponent(
            elementGroups.GetValueOrDefault(root, new List<int>()),
            rigidGroups.GetValueOrDefault(root, new List<int>()),
            massGroups.GetValueOrDefault(root, new List<int>())
        ));
      }

      return result;
    }

    // 기존 메서드 하위 호환성 유지 (ElementGroupTranslationModifier 등에서 에러 방지)
    public static List<List<int>> FindConnectedElementGroups(Elements elements)
    {
      if (elements == null || !elements.Any())
        return new List<List<int>>();

      var allNodeIDs = elements
          .SelectMany(e => e.Value.NodeIDs)
          .Distinct()
          .ToList();

      if (allNodeIDs.Count == 0)
        return new List<List<int>>();

      var uf = new UnionFind(allNodeIDs);

      foreach (var kvp in elements)
      {
        var nodeIDs = kvp.Value.NodeIDs;
        if (nodeIDs.Count < 2) continue;

        int baseNode = nodeIDs[0];
        for (int i = 1; i < nodeIDs.Count; i++)
        {
          uf.Union(baseNode, nodeIDs[i]);
        }
      }

      var groupMap = new Dictionary<int, List<int>>();

      foreach (var kvp in elements)
      {
        int elementID = kvp.Key;
        var nodeIDs = kvp.Value.NodeIDs;

        if (nodeIDs.Count == 0) continue;

        int root = uf.Find(nodeIDs[0]);

        if (!groupMap.ContainsKey(root))
        {
          groupMap[root] = new List<int>();
        }
        groupMap[root].Add(elementID);
      }

      return groupMap.Values.ToList();
    }
  }
}
