using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using HiTessModelBuilder.Pipeline.Utils;

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementDetectShortInspector
  {
    // [기존 코드 위치] -> [수정 후 코드]
    public static new List<(int eleId, int n1, int n2)> Run(FeModelContext context, double ShortElementDistanceThreshold)
    {
      var shortElements = new List<(int eleId, int n1, int n2)>(); // 반환 타입 명시

      foreach (var kv in context.Elements)
      {
        var ele = kv.Value;
        var eid = kv.Key; // Key값(ElementID) 확보

        if (ele.NodeIDs.Count < 2) continue; // 노드가 2개 미만인 요소는 거리 계산 불가

        // ★ [추가된 방어 코드] 노드가 실제로 존재하는지 확인
        int n1 = ele.NodeIDs[0];
        int n2 = ele.NodeIDs[1];

        if (!context.Nodes.Contains(n1) || !context.Nodes.Contains(n2))
        {
          // 유효하지 않은 요소(Dangling Element)이므로 건너뜀
          continue;
        }

        // [수정 포인트] DistanceUtils 호출 시 context.Nodes를 3번째 인자로 전달해야 합니다.
        double len = DistanceUtils.GetDistanceBetweenNodes(n1, n2, context.Nodes);

        if (len < ShortElementDistanceThreshold)
        {
          shortElements.Add((eid, n1, n2));
        }
      }

      // 결과 출력 옵션
      if (inspectOpt.PrintAllNodeIds && shortElements.Count > 0)
      {
        Console.WriteLine($"\n[Inspector] Found {shortElements.Count} short elements (< {inspectOpt.ShortElementDistanceThreshold}):");
        foreach (var e in shortElements)
          Console.WriteLine($"   -> ELE {e.eleId} : Nodes [{e.n1}, {e.n2}]");
      }

      return shortElements;
    }
  }
}
