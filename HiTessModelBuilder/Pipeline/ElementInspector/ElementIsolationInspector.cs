using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementIsolationInspector
  {
    /// <summary>
    /// 전체 FE 모델에서 메인 구조물과 노드를 공유하지 않고 독립적으로 고립된 Element들의 ID 목록을 반환합니다.
    /// Union-Find 알고리즘을 통해 노드 클러스터를 형성하고, 가장 큰 클러스터에 속하지 않는 요소들을 추출합니다.
    /// </summary>
    /// <param name="context">검사할 전체 노드와 요소가 포함된 FeModelContext</param>
    /// <returns>고립된 Element ID 리스트</returns>
    public static List<int> FindIsolatedElements(FeModelContext context)
    {
      var result = new List<int>();

      // 1. 모든 Node ID 수집
      var nodeIDs = context.Elements
        .SelectMany(e => e.Value.NodeIDs)
        .Distinct()
        .ToList();

      if (nodeIDs.Count == 0)
        return result;

      // 2. Union-Find 초기화
      var uf = new UnionFind(nodeIDs);

      // 3. Element별로 Node 연결
      foreach (var element in context.Elements)
      {
        var ids = element.Value.NodeIDs;
        if (ids.Count < 2)
          continue;

        int baseNode = ids[0];
        for (int i = 1; i < ids.Count; i++)
          uf.Union(baseNode, ids[i]);
      }

      // 4. Node cluster 생성
      var clusters = uf.GetClusters();

      // 5. 가장 큰 cluster 선택 (main structure)
      var mainCluster = clusters
        .OrderByDescending(c => c.Value.Count)
        .First()
        .Value
        .ToHashSet();

      // 6. Element가 main cluster에 속하는지 검사
      foreach (var kv in context.Elements)
      {
        int elementID = kv.Key;
        var element = kv.Value;

        bool connectedToMain = element.NodeIDs
          .Any(id => mainCluster.Contains(id));

        if (!connectedToMain)
          result.Add(elementID);
      }

      return result;
    }
  }
}