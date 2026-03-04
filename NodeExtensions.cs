using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;

namespace HiTessModelBuilder.Model.Entities
{
  public static class NodeExtensions
  {
    /// <summary>
    /// 기준점(P0)과 방향벡터(vRef), 매개변수(t)를 이용하여 좌표를 계산하고,
    /// 해당 위치에 노드를 생성하거나 기존 노드를 반환합니다.
    /// </summary>
    public static int GetOrCreateNodeAtT(this Nodes nodes, Point3D P0, Vector3D vRef, double t)
    {
      // Point3D = Point3D + (Vector3D * double) 연산 수행 (정확한 기하학적 연산)
      Point3D p = P0 + (vRef * t);
      return nodes.AddOrGet(p.X, p.Y, p.Z);
    }

    /// <summary>
    /// 현재 모델에서 하나 이상의 부재(Element)에 사용되고 있는 모든 유효 노드 ID를 HashSet으로 반환합니다.
    /// (성능 최적화를 위해 장비 질량 매핑 전, 루프 바깥에서 한 번만 호출할 것을 권장합니다.)
    /// </summary>
    public static HashSet<int> GetNodesUsedInElements(this FeModelContext context)
    {
      var usedNodes = new HashSet<int>();
      foreach (var kvp in context.Elements)
      {
        foreach (int nid in kvp.Value.NodeIDs)
        {
          usedNodes.Add(nid);
        }
      }
      return usedNodes;
    }

    /// <summary>
    /// 지정된 타겟 좌표(targetPos)와 가장 가까운 노드 ID를 찾습니다. 
    /// 단, 검사 대상은 validNodeIds(부재에 속한 노드)로 제한되며, 허용 거리(tolerance) 이내여야 합니다.
    /// </summary>
    /// <param name="nodes">Nodes 컬렉션 인스턴스</param>
    /// <param name="targetPos">탐색 기준 좌표 (장비 COG 등)</param>
    /// <param name="validNodeIds">검색 대상이 될 유효 노드 ID 집합</param>
    /// <param name="tolerance">탐색 허용 반경 (이보다 멀면 -1 반환)</param>
    /// <returns>가장 가까운 유효 노드 ID (없으면 -1 반환)</returns>
    public static int FindClosestValidNode(
        this Nodes nodes,
        Point3D targetPos,
        HashSet<int> validNodeIds,
        double tolerance = double.MaxValue)
    {
      int closestNodeID = -1;
      double minDistance = tolerance;

      foreach (int nid in validNodeIds)
      {
        if (!nodes.Contains(nid)) continue;

        var nodePos = nodes[nid];

        // Point3D의 오버로딩된 연산자(-)와 Magnitude()를 사용하여 거리 계산
        double dist = (nodePos - targetPos).Magnitude();

        if (dist < minDistance)
        {
          minDistance = dist;
          closestNodeID = nid;
        }
      }

      return closestNodeID;
    }

    /// <summary>
    /// 모델 내에서 '배관(Pipe)' 요소로 분류된 부재들에 사용된 노드 ID만 추출하여 HashSet으로 반환합니다.
    /// (Point Mass가 구조물 노드에 잘못 들러붙는 것을 방지합니다.)
    /// </summary>
    public static HashSet<int> GetNodesUsedInPipeElements(this FeModelContext context)
    {
      var pipeNodes = new HashSet<int>();
      foreach (var kvp in context.Elements)
      {
        var ele = kvp.Value;

        // 요소의 ExtraData에 "Category"가 "Pipe"로 마킹된 경우만 취급
        if (ele.ExtraData != null &&
            ele.ExtraData.TryGetValue("Category", out string? category) &&
            category == "Pipe")
        {
          foreach (int nid in ele.NodeIDs)
          {
            pipeNodes.Add(nid);
          }
        }
      }
      return pipeNodes;
    }
  }
}
