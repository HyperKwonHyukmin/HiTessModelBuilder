using Cmb.Core.Geometry;
using Cmb.Core.Model;

namespace Cmb.Pipeline.Spatial;

public sealed class ElementSpatialHash
{
    private readonly double _cell;
    private readonly double _inflate;
    private readonly Dictionary<int, (Point3 A, Point3 B)> _endpoints = new();
    private readonly Dictionary<(int, int, int), List<int>> _map       = new();

    public ElementSpatialHash(
        IEnumerable<BeamElement> elements,
        IReadOnlyDictionary<int, Node> nodeById,
        double cellSizeMm,
        double inflate = 0.0)
    {
        _cell    = Math.Max(cellSizeMm, 1e-9);
        _inflate = Math.Max(inflate, 0.0);

        foreach (var elem in elements)
        {
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

            _endpoints[elem.Id] = (nA.Position, nB.Position);

            foreach (var key in CoveredCells(nA.Position, nB.Position))
            {
                if (!_map.TryGetValue(key, out var list))
                {
                    list = [];
                    _map[key] = list;
                }
                list.Add(elem.Id);
            }
        }
    }

    /// <summary>
    /// Returns candidate element IDs whose bounding box overlaps the given element's bbox.
    /// </summary>
    public IEnumerable<int> QueryCandidates(int elemId)
    {
        if (!_endpoints.TryGetValue(elemId, out var ep))
            return [];

        var result = new HashSet<int>();
        foreach (var key in CoveredCells(ep.A, ep.B))
            if (_map.TryGetValue(key, out var list))
                foreach (var id in list)
                    if (id != elemId) result.Add(id);

        return result;
    }

    /// <summary>
    /// Returns all element IDs whose bounding box overlaps the given AABB.
    /// </summary>
    public IEnumerable<int> QueryBox(Point3 min, Point3 max)
    {
        var result = new HashSet<int>();
        int ix0 = (int)Math.Floor((min.X - _inflate) / _cell);
        int iy0 = (int)Math.Floor((min.Y - _inflate) / _cell);
        int iz0 = (int)Math.Floor((min.Z - _inflate) / _cell);
        int ix1 = (int)Math.Floor((max.X + _inflate) / _cell);
        int iy1 = (int)Math.Floor((max.Y + _inflate) / _cell);
        int iz1 = (int)Math.Floor((max.Z + _inflate) / _cell);

        for (int ix = ix0; ix <= ix1; ix++)
            for (int iy = iy0; iy <= iy1; iy++)
                for (int iz = iz0; iz <= iz1; iz++)
                    if (_map.TryGetValue((ix, iy, iz), out var list))
                        foreach (var id in list)
                            result.Add(id);

        return result;
    }

    private IEnumerable<(int, int, int)> CoveredCells(Point3 a, Point3 b)
    {
        int ix0 = (int)Math.Floor((Math.Min(a.X, b.X) - _inflate) / _cell);
        int iy0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - _inflate) / _cell);
        int iz0 = (int)Math.Floor((Math.Min(a.Z, b.Z) - _inflate) / _cell);
        int ix1 = (int)Math.Floor((Math.Max(a.X, b.X) + _inflate) / _cell);
        int iy1 = (int)Math.Floor((Math.Max(a.Y, b.Y) + _inflate) / _cell);
        int iz1 = (int)Math.Floor((Math.Max(a.Z, b.Z) + _inflate) / _cell);

        for (int ix = ix0; ix <= ix1; ix++)
            for (int iy = iy0; iy <= iy1; iy++)
                for (int iz = iz0; iz <= iz1; iz++)
                    yield return (ix, iy, iz);
    }
}
