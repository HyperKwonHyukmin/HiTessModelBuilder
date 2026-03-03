using HiTessModelBuilder.Model.Entities;
using System.Collections.Generic;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class NodeDegreeInspector
  {
    public static Dictionary<int, int> BuildNodeDegree(FeModelContext context)
    {
      // 1. 빈 딕셔너리 생성 (필요한 노드만 기록하여 메모리 절약)
      var degree = new Dictionary<int, int>();

      foreach (var ele in context.Elements)
      {
        foreach (int nodeId in ele.Value.NodeIDs)
        {
          // 2. ContainsKey + Indexer 대신 TryGetValue로 단일 탐색(O(1)) 최적화
          degree.TryGetValue(nodeId, out int currentCount);
          degree[nodeId] = currentCount + 1;
        }
      }

      return degree;
    }
  }
}