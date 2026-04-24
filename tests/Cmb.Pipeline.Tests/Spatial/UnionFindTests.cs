using Cmb.Pipeline.Spatial;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Spatial;

public class UnionFindTests
{
    [Fact]
    public void Initially_EachElement_IsOwnRoot()
    {
        var uf = new UnionFind([1, 2, 3]);
        uf.Find(1).Should().Be(1);
        uf.Find(2).Should().Be(2);
        uf.Find(3).Should().Be(3);
    }

    [Fact]
    public void Union_TwoElements_SameRoot()
    {
        var uf = new UnionFind([1, 2]);
        uf.Union(1, 2);
        uf.Find(1).Should().Be(uf.Find(2));
    }

    [Fact]
    public void Union_Idempotent_NoError()
    {
        var uf = new UnionFind([1, 2]);
        uf.Union(1, 2);
        uf.Union(1, 2); // second call should be safe
        uf.Find(1).Should().Be(uf.Find(2));
    }

    [Fact]
    public void Union_Chain_AllSameGroup()
    {
        var uf = new UnionFind([1, 2, 3, 4]);
        uf.Union(1, 2);
        uf.Union(2, 3);
        uf.Union(3, 4);

        uf.Find(1).Should().Be(uf.Find(2));
        uf.Find(2).Should().Be(uf.Find(3));
        uf.Find(3).Should().Be(uf.Find(4));
    }

    [Fact]
    public void PathCompression_ConsistentAfterMultipleCalls()
    {
        var uf = new UnionFind([1, 2, 3, 4, 5]);
        uf.Union(1, 2);
        uf.Union(2, 3);
        uf.Union(3, 4);
        uf.Union(4, 5);

        int root = uf.Find(5);
        uf.Find(1).Should().Be(root);
        uf.Find(3).Should().Be(root);
        uf.Find(5).Should().Be(root);
    }

    [Fact]
    public void GetGroups_IsolatedNodes_EachOwnGroup()
    {
        var uf = new UnionFind([10, 20, 30]);
        var groups = uf.GetGroups();
        groups.Should().HaveCount(3);
        groups.SelectMany(g => g).Should().BeEquivalentTo([10, 20, 30]);
    }

    [Fact]
    public void GetGroups_AfterUnion_CorrectGrouping()
    {
        var uf = new UnionFind([1, 2, 3, 4, 5]);
        uf.Union(1, 2);
        uf.Union(3, 4);

        var groups = uf.GetGroups();
        groups.Should().HaveCount(3); // {1,2}, {3,4}, {5}

        var group12 = groups.First(g => g.Contains(1));
        group12.Should().BeEquivalentTo([1, 2]);

        var group34 = groups.First(g => g.Contains(3));
        group34.Should().BeEquivalentTo([3, 4]);
    }

    [Fact]
    public void GetGroups_EachGroupSortedAscending()
    {
        var uf = new UnionFind([5, 3, 1, 4, 2]);
        uf.Union(5, 1);
        uf.Union(1, 3);

        var groups = uf.GetGroups();
        var bigGroup = groups.First(g => g.Count == 3);
        bigGroup.Should().BeInAscendingOrder();
    }

    [Fact]
    public void GetGroups_MinValueIsFirstInGroup()
    {
        var uf = new UnionFind([10, 5, 8]);
        uf.Union(10, 5);
        uf.Union(10, 8);

        var groups = uf.GetGroups();
        var g = groups.Single();
        g[0].Should().Be(5); // minimum is first
    }

    [Fact]
    public void GetGroups_OrderedByGroupMin()
    {
        var uf = new UnionFind([1, 2, 10, 11]);
        uf.Union(1, 2);
        uf.Union(10, 11);

        var groups = uf.GetGroups();
        groups[0][0].Should().Be(1);
        groups[1][0].Should().Be(10);
    }

    [Fact]
    public void SingleElement_GetGroups_ReturnsOneSingletonGroup()
    {
        var uf = new UnionFind([42]);
        var groups = uf.GetGroups();
        groups.Should().HaveCount(1);
        groups[0].Should().BeEquivalentTo([42]);
    }
}
