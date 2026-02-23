using HiTessModelBuilder.Model.Entities;
using System.Collections.Generic;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementDetectShortInspector
  {
    // 'new' 키워드 제거, 순수하게 데이터만 찾아서 반환합니다.
    public static List<(int eleId, int n1, int n2)> Run(FeModelContext context, double threshold)
    {
      var shortElements = new List<(int eleId, int n1, int n2)>();

      foreach (var kv in context.Elements)
      {
        var ele = kv.Value;
        var eid = kv.Key;

        if (ele.NodeIDs.Count < 2) continue;

        int n1 = ele.NodeIDs[0];
        int n2 = ele.NodeIDs[1];

        if (!context.Nodes.Contains(n1) || !context.Nodes.Contains(n2)) continue;

        // ★ [최적화] 외부 Utils 대신 우리가 만든 Point3D 연산자 오버로딩 활용
        var p1 = context.Nodes[n1];
        var p2 = context.Nodes[n2];
        double len = (p1 - p2).Magnitude();

        if (len < threshold)
        {
          shortElements.Add((eid, n1, n2));
        }
      }

      return shortElements;
    }
  }
}
