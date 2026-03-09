using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

public static class NodeDegreeInspector
{
  public static Dictionary<int, int> BuildNodeDegree(FeModelContext context)
  {
    var degree = context.Nodes.ToDictionary(kv => kv.Key, _ => 0);

    // 1. Elements (CBEAM 등 일반 부재) 카운트
    foreach (var kvp in context.Elements)
    {
      foreach (int nodeId in kvp.Value.NodeIDs)
      {
        if (degree.ContainsKey(nodeId))
          degree[nodeId]++;
      }
    }

    // 2. ★ Rigids (RBE) 카운트 추가
    // 강체에 묶인 노드들도 위상학적으로 연결된 것으로 간주하여 차수(Degree)를 올립니다.
    foreach (var kvp in context.Rigids)
    {
      var rbe = kvp.Value;
      
      if (degree.ContainsKey(rbe.IndependentNodeID))
        degree[rbe.IndependentNodeID]++;

      foreach (int nodeId in rbe.DependentNodeIDs)
      {
        if (degree.ContainsKey(nodeId))
          degree[nodeId]++;
      }
    }

    return degree;
  }
}
