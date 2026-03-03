using HiTessModelBuilder.Model.Entities;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementDuplicateInspector
  {
    public static List<List<int>> FindDuplicateGroups(FeModelContext context)
    {
      var topologyGroups = new Dictionary<string, List<int>>();

      foreach (var kvp in context.Elements)
      {
        int elementID = kvp.Key;
        var nodes = kvp.Value.NodeIDs;

        if (nodes == null || nodes.Count < 2) continue;

        // ★ [성능 최적화] 1D 요소(노드 2개)인 경우 LINQ 정렬을 회피하여 메모리 할당 제거
        string topologyKey;
        if (nodes.Count == 2)
        {
          int n1 = nodes[0];
          int n2 = nodes[1];
          topologyKey = n1 < n2 ? $"{n1}-{n2}" : $"{n2}-{n1}";
        }
        else
        {
          // 노드가 3개 이상인 다각형 요소용 Fallback 로직
          var sortedNodeIDs = nodes.OrderBy(n => n).ToList();
          topologyKey = string.Join("-", sortedNodeIDs);
        }

        // ★ [성능 최적화] TryGetValue를 통한 단일 탐색
        if (!topologyGroups.TryGetValue(topologyKey, out var groupList))
        {
          groupList = new List<int>();
          topologyGroups[topologyKey] = groupList;
        }
        groupList.Add(elementID);
      }

      return topologyGroups.Values
                           .Where(group => group.Count > 1)
                           .ToList();
    }
  }
}