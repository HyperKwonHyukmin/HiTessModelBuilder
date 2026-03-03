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
  }
}