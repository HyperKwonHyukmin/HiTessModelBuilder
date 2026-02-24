using System;
using System.Collections.Generic;
using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;

namespace HiTessModelBuilder.Pipeline.Utils
{
  /// <summary>
  /// 3차원 공간의 노드 검색을 가속화하기 위한 공간 해시(Spatial Hash) 알고리즘입니다.
  /// 특정 바운딩 박스 내부에 존재하는 노드 ID를 빠르게 필터링하여 반환합니다.
  /// </summary>
  public sealed class SpatialHash
  {
    private readonly double _cell;
    private readonly Dictionary<(int, int, int), List<int>> _map = new();

    /// <summary>
    /// 주어진 노드 컬렉션과 셀 크기를 바탕으로 공간 해시 그리드를 구축합니다.
    /// </summary>
    /// <param name="nodes">공간에 배치될 전체 노드 컬렉션</param>
    /// <param name="cellSize">그리드 셀 한 칸의 크기 (최소 1e-9 이상)</param>
    public SpatialHash(Nodes nodes, double cellSize)
    {
      _cell = Math.Max(cellSize, 1e-9);
      foreach (var kv in nodes)
      {
        int nid = kv.Key;
        var p = nodes.GetNodeCoordinates(nid);
        var key = Key(p);

        if (!_map.TryGetValue(key, out var list))
        {
          list = new List<int>();
          _map[key] = list;
        }
        list.Add(nid);
      }
    }

    /// <summary>
    /// 지정된 바운딩 박스 영역과 교차하는 모든 셀의 노드 ID 집합을 반환합니다.
    /// </summary>
    public HashSet<int> Query(BoundingBox bbox)
    {
      var result = new HashSet<int>();
      var (ix0, iy0, iz0) = Key(bbox.Min);
      var (ix1, iy1, iz1) = Key(bbox.Max);

      for (int ix = Math.Min(ix0, ix1); ix <= Math.Max(ix0, ix1); ix++)
        for (int iy = Math.Min(iy0, iy1); iy <= Math.Max(iy0, iy1); iy++)
          for (int iz = Math.Min(iz0, iz1); iz <= Math.Max(iz0, iz1); iz++)
          {
            if (_map.TryGetValue((ix, iy, iz), out var list))
            {
              foreach (var nid in list) result.Add(nid);
            }
          }
      return result;
    }

    private (int, int, int) Key(Point3D p)
    {
      int ix = (int)Math.Floor(p.X / _cell);
      int iy = (int)Math.Floor(p.Y / _cell);
      int iz = (int)Math.Floor(p.Z / _cell);
      return (ix, iy, iz);
    }
  }
}
