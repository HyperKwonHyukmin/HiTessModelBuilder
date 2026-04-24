namespace Cmb.Pipeline.Spatial;

public sealed class UnionFind
{
    private readonly Dictionary<int, int> _parent = new();
    private readonly Dictionary<int, int> _rank   = new();

    public UnionFind(IEnumerable<int> elements)
    {
        foreach (var e in elements)
        {
            _parent[e] = e;
            _rank[e]   = 0;
        }
    }

    public int Find(int x)
    {
        if (_parent[x] != x)
            _parent[x] = Find(_parent[x]); // path compression
        return _parent[x];
    }

    public void Union(int a, int b)
    {
        int rootA = Find(a);
        int rootB = Find(b);
        if (rootA == rootB) return;

        // union by rank — keep tree shallow
        if (_rank[rootA] < _rank[rootB])
            _parent[rootA] = rootB;
        else if (_rank[rootA] > _rank[rootB])
            _parent[rootB] = rootA;
        else
        {
            _parent[rootB] = rootA;
            _rank[rootA]++;
        }
    }

    public IReadOnlyList<IReadOnlyList<int>> GetGroups()
    {
        var clusters = new Dictionary<int, List<int>>();
        foreach (var x in _parent.Keys)
        {
            int root = Find(x);
            if (!clusters.TryGetValue(root, out var list))
            {
                list = [];
                clusters[root] = list;
            }
            list.Add(x);
        }

        return clusters.Values
            .Select(g => { g.Sort(); return (IReadOnlyList<int>)g; })
            .OrderBy(g => g[0])
            .ToList();
    }
}
