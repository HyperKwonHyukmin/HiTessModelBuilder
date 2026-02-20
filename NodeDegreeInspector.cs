using HiTessModelBuilder.Model.Entities;
using System;

public static class NodeDegreeInspector
{
  public static Dictionary<int, int> BuildNodeDegree(
    FeModelContext context)
  {
    var degree = context.Nodes
      .ToDictionary(kv => kv.Key, _ => 0);

    foreach (var ele in context.Elements)
    {
      foreach (int nodeId in ele.Value.NodeIDs)
      {
        if (degree.ContainsKey(nodeId))
          degree[nodeId]++;
      }
    }

    return degree;
  }
}

