using Cmb.Core.Geometry;
using Cmb.Core.Model;

namespace Cmb.Pipeline.Spatial;

/// <summary>
/// 점 단위 공간 해시. 주어진 좌표 근방의 노드 ID를 반경 검색합니다.
/// </summary>
public sealed class NodeSpatialHash
{
    private readonly double _cell;
    private readonly Dictionary<(int, int, int), List<int>> _map = new();

    public NodeSpatialHash(IEnumerable<Node> nodes, double cellSizeMm)
    {
        _cell = Math.Max(cellSizeMm, 1e-9);
        foreach (var n in nodes) Add(n.Id, n.Position);
    }

    /// <summary>
    /// 노드를 해시에 추가합니다. stage 진행 중 새로 생성된 노드를 인덱스에 반영하기 위해 사용.
    /// 호출자는 동일 ID 중복 추가를 책임집니다.
    /// </summary>
    public void Add(int nodeId, Point3 position)
    {
        var key = CellKey(position);
        if (!_map.TryGetValue(key, out var list))
        {
            list = [];
            _map[key] = list;
        }
        list.Add(nodeId);
    }

    /// <summary>
    /// 점 p로부터 radius 이내일 가능성이 있는 후보 노드 ID 집합(셀 단위 광역 후보).
    /// 정확한 거리 검사는 호출자 책임.
    /// </summary>
    public IEnumerable<int> QueryNear(Point3 p, double radius)
    {
        int ix0 = (int)Math.Floor((p.X - radius) / _cell);
        int iy0 = (int)Math.Floor((p.Y - radius) / _cell);
        int iz0 = (int)Math.Floor((p.Z - radius) / _cell);
        int ix1 = (int)Math.Floor((p.X + radius) / _cell);
        int iy1 = (int)Math.Floor((p.Y + radius) / _cell);
        int iz1 = (int)Math.Floor((p.Z + radius) / _cell);

        var seen = new HashSet<int>();
        for (int ix = ix0; ix <= ix1; ix++)
            for (int iy = iy0; iy <= iy1; iy++)
                for (int iz = iz0; iz <= iz1; iz++)
                    if (_map.TryGetValue((ix, iy, iz), out var list))
                        foreach (int id in list)
                            if (seen.Add(id)) yield return id;
    }

    private (int, int, int) CellKey(Point3 p)
        => ((int)Math.Floor(p.X / _cell),
            (int)Math.Floor(p.Y / _cell),
            (int)Math.Floor(p.Z / _cell));
}
