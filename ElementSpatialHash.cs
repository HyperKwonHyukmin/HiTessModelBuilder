using System;
using System.Collections.Generic;
using System.Linq;
using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;

namespace HiTessModelBuilder.Pipeline.Utils
{
  /// <summary>
  /// 요소(Element) 선분들의 공간 검색을 가속화하기 위한 해시 그리드입니다.
  /// 특정 요소를 감싸는 바운딩 박스를 기준으로 주변의 교차 후보 요소 ID를 빠르게 반환합니다.
  /// </summary>
  public sealed class ElementSpatialHash
  {
    private readonly double _cell;
    private readonly double _inflate;
    private readonly Dictionary<(int, int, int), List<int>> _map = new();
    private readonly Dictionary<int, BoundingBox> _bbox = new();

    public ElementSpatialHash(Elements elements, Nodes nodes, double cellSize, double inflate)
    {
      _cell = Math.Max(cellSize, 1e-9);
      _inflate = Math.Max(inflate, 0);

      var ids = elements.Keys.ToList();
      foreach (var eid in ids)
      {
        if (!elements.Contains(eid)) continue;

        if (!TryGetSegment(nodes, elements, eid, out var a, out var b))
          continue;

        var bb = BoundingBox.FromSegment(a, b, _inflate);
        _bbox[eid] = bb;

        foreach (var key in CoveredCells(bb))
        {
          if (!_map.TryGetValue(key, out var list))
          {
            list = new List<int>();
            _map[key] = list;
          }
          list.Add(eid);
        }
      }
    }

    public IEnumerable<int> QueryCandidates(int eid)
    {
      if (!_bbox.TryGetValue(eid, out var bb))
        return Enumerable.Empty<int>();

      var set = new HashSet<int>();
      foreach (var key in CoveredCells(bb))
      {
        if (_map.TryGetValue(key, out var list))
        {
          for (int i = 0; i < list.Count; i++)
            set.Add(list[i]);
        }
      }
      return set;
    }

    private IEnumerable<(int, int, int)> CoveredCells(BoundingBox bb)
    {
      var (ix0, iy0, iz0) = Key(bb.Min);
      var (ix1, iy1, iz1) = Key(bb.Max);

      int x0 = Math.Min(ix0, ix1), x1 = Math.Max(ix0, ix1);
      int y0 = Math.Min(iy0, iy1), y1 = Math.Max(iy0, iy1);
      int z0 = Math.Min(iz0, iz1), z1 = Math.Max(iz0, iz1);

      for (int ix = x0; ix <= x1; ix++)
        for (int iy = y0; iy <= y1; iy++)
          for (int iz = z0; iz <= z1; iz++)
            yield return (ix, iy, iz);
    }

    private (int, int, int) Key(Point3D p)
    {
      return ((int)Math.Floor(p.X / _cell), (int)Math.Floor(p.Y / _cell), (int)Math.Floor(p.Z / _cell));
    }

    private bool TryGetSegment(Nodes nodes, Elements elements, int eid, out Point3D a, out Point3D b)
    {
      a = default!; b = default!;
      if (!elements.Contains(eid)) return false;
      var e = elements[eid];
      if (e.NodeIDs == null || e.NodeIDs.Count < 2) return false;
      int n0 = e.NodeIDs.First(); int n1 = e.NodeIDs.Last();
      if (!nodes.Contains(n0) || !nodes.Contains(n1)) return false;
      a = nodes[n0]; b = nodes[n1];
      return true;
    }
  }
}
