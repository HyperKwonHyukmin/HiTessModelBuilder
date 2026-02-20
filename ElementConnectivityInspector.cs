using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;


namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementConnectivityInspector
  {
    /// <summary>
    /// 연결된 요소들의 그룹 리스트를 반환합니다.
    /// (예: 100개의 요소가 서로 다 붙어있으면 1개의 그룹, 떨어져 있으면 N개의 그룹)
    /// </summary>
    public static List<List<int>> FindConnectedElementGroups(Elements elements)
    {
      if (elements == null || !elements.Any())
        return new List<List<int>>();

      // 1. 전체 노드 ID 수집 (중복 제거)
      var allNodeIDs = elements
          .SelectMany(e => e.Value.NodeIDs)
          .Distinct()
          .ToList();

      if (allNodeIDs.Count == 0)
        return new List<List<int>>();

      // 2. Union-Find 초기화
      var uf = new UnionFind(allNodeIDs);

      // 3. 요소 내부의 노드들을 하나로 통합 (Union)
      // 논리: 한 요소(Element)를 구성하는 노드들은 물리적으로 연결되어 있다.
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

      // [수정] 불필요한 uf.GetClusters() 호출 제거 (성능 최적화)

      // 4. 그룹핑 (Root Node -> Element IDs)
      var groupMap = new Dictionary<int, List<int>>();

      foreach (var kvp in elements)
      {
        int elementID = kvp.Key;
        var nodeIDs = kvp.Value.NodeIDs;

        if (nodeIDs.Count == 0) continue;

        // 요소의 첫 번째 노드가 속한 집합의 대표(Root)를 찾음
        int root = uf.Find(nodeIDs[0]);

        if (!groupMap.ContainsKey(root))
        {
          groupMap[root] = new List<int>();
        }
        groupMap[root].Add(elementID);
      }

      // 5. 결과 반환
      return groupMap.Values.ToList();
    }
  }
}
