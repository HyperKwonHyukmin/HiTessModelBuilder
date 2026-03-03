using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementConnectivityInspector
  {
    public static List<List<int>> FindConnectedElementGroups(Elements elements)
    {
      if (elements == null || elements.Count == 0) // .Any() 대신 .Count 속성 사용이 훨씬 빠름
        return new List<List<int>>();

      // ★ 최적화 1: 무거운 LINQ(SelectMany + Distinct + ToList) 대신 순수 HashSet 사용
      var uniqueNodes = new HashSet<int>();
      foreach (var kvp in elements)
      {
        foreach (int nid in kvp.Value.NodeIDs)
        {
          uniqueNodes.Add(nid);
        }
      }

      if (uniqueNodes.Count == 0)
        return new List<List<int>>();

      // 2. Union-Find 초기화 (HashSet.ToList()로 변환하여 전달)
      var uf = new UnionFind(uniqueNodes.ToList());

      // 3. 요소 내부 노드 통합 (Union)
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

      // 4. 그룹핑
      var groupMap = new Dictionary<int, List<int>>();
      foreach (var kvp in elements)
      {
        int elementID = kvp.Key;
        var nodeIDs = kvp.Value.NodeIDs;

        if (nodeIDs.Count == 0) continue;

        int root = uf.Find(nodeIDs[0]);

        // ★ 최적화 2: ContainsKey 대신 TryGetValue로 단일 탐색 달성
        if (!groupMap.TryGetValue(root, out var groupList))
        {
          groupList = new List<int>();
          groupMap[root] = groupList;
        }

        groupList.Add(elementID);
      }

      // 5. 결과 반환
      return groupMap.Values.ToList();
    }
  }
}