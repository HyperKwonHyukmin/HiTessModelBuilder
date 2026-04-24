using Cmb.Core.Geometry;

namespace Cmb.Pipeline.Spatial;

public sealed class SpatialHash<T>
{
    private readonly double _cell;
    private readonly Dictionary<(int, int, int), List<T>> _map = new();

    public SpatialHash(double cellSizeMm)
    {
        _cell = Math.Max(cellSizeMm, 1e-9);
    }

    public void Insert(Point3 p, T value)
    {
        var key = Key(p);
        if (!_map.TryGetValue(key, out var list))
        {
            list = [];
            _map[key] = list;
        }
        list.Add(value);
    }

    // Returns all items whose cell overlaps the AABB of [center ± radius].
    // Callers should apply exact distance filtering on results.
    public IEnumerable<T> QueryNeighbors(Point3 center, double radius)
    {
        int ix0 = (int)Math.Floor((center.X - radius) / _cell);
        int iy0 = (int)Math.Floor((center.Y - radius) / _cell);
        int iz0 = (int)Math.Floor((center.Z - radius) / _cell);
        int ix1 = (int)Math.Floor((center.X + radius) / _cell);
        int iy1 = (int)Math.Floor((center.Y + radius) / _cell);
        int iz1 = (int)Math.Floor((center.Z + radius) / _cell);

        for (int ix = ix0; ix <= ix1; ix++)
            for (int iy = iy0; iy <= iy1; iy++)
                for (int iz = iz0; iz <= iz1; iz++)
                    if (_map.TryGetValue((ix, iy, iz), out var list))
                        foreach (var v in list)
                            yield return v;
    }

    public void Clear() => _map.Clear();

    private (int, int, int) Key(Point3 p) =>
        ((int)Math.Floor(p.X / _cell),
         (int)Math.Floor(p.Y / _cell),
         (int)Math.Floor(p.Z / _cell));
}
