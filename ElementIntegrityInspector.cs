using HiTessModelBuilder.Model.Entities;
using System.Collections.Generic;
using System.Linq; 

namespace HiTessModelBuilder.Pipeline.ElementInspector
{
  public static class ElementIntegrityInspector
  {
    public static List<int> FindElementsWithInvalidReference(FeModelContext context)
    {
      var invalidElements = new List<int>();

      foreach (var kv in context.Elements)
      {
        int elementId = kv.Key;
        var element = kv.Value;

        // 1. Node Reference 확인
        // 요소에 포함된 노드 중 하나라도 Nodes 컬렉션에 없다면 결함
        if (element.NodeIDs.Any(nodeID => !context.Nodes.Contains(nodeID)))
        {
          invalidElements.Add(elementId);
          continue; // 다음 요소로 넘어감 (goto 대체)
        }

        // 2. Property Reference 확인
        if (!context.Properties.Contains(element.PropertyID))
        {
          invalidElements.Add(elementId);
          continue;
        }

        // 3. Material Reference 확인
        // 위에서 Property 존재 여부를 확인했으므로 안전하게 가져옴
        var prop = context.Properties[element.PropertyID];

        if (!context.Materials.Contains(prop.MaterialID))
        {
          invalidElements.Add(elementId);
          continue;
        }
      }

      return invalidElements;
    }
  }
}
