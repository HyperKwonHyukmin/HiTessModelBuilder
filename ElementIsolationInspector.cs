using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;


namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  /// <summary>
  /// 전체 구조에서 고립된 Element 검사
  /// </summary>
  public static class ElementIsolationInspector
  {
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

