using Cmb.Core.Geometry;
using Cmb.Pipeline.Spatial;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Spatial;

public class SpatialHashTests
{
    [Fact]
    public void Insert_ThenQuery_ReturnsValue()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(0, 0, 0), 42);

        hash.QueryNeighbors(new Point3(0, 0, 0), 50.0)
            .Should().Contain(42);
    }

    [Fact]
    public void Query_FarPoint_NotReturned()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(0, 0, 0), 99);

        // radius=50 but point is 200 away — no cell overlap
        hash.QueryNeighbors(new Point3(200, 0, 0), 50.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void Insert_MultiplePoints_AllReturnedInLargeRadius()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(0, 0, 0), 1);
        hash.Insert(new Point3(50, 0, 0), 2);
        hash.Insert(new Point3(0, 50, 0), 3);

        var results = hash.QueryNeighbors(new Point3(25, 25, 0), 200.0).ToList();
        results.Should().Contain(1).And.Contain(2).And.Contain(3);
    }

    [Fact]
    public void Query_EmptyHash_ReturnsEmpty()
    {
        var hash = new SpatialHash<string>(100.0);
        hash.QueryNeighbors(new Point3(0, 0, 0), 500.0).Should().BeEmpty();
    }

    [Fact]
    public void Insert_SamePosition_BothReturned()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(10, 10, 10), 1);
        hash.Insert(new Point3(10, 10, 10), 2);

        var results = hash.QueryNeighbors(new Point3(10, 10, 10), 1.0).ToList();
        results.Should().Contain(1).And.Contain(2);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(0, 0, 0), 1);
        hash.Clear();

        hash.QueryNeighbors(new Point3(0, 0, 0), 500.0).Should().BeEmpty();
    }

    [Fact]
    public void Query_PointInAdjacentCell_Returned()
    {
        var hash = new SpatialHash<int>(100.0);
        // Insert at (99, 0, 0) — cell (0,0,0)
        hash.Insert(new Point3(99, 0, 0), 7);
        // Query from (101, 0, 0) with radius 50 — covers cells (-1..1, -1..0, -1..0) at minimum
        var results = hash.QueryNeighbors(new Point3(101, 0, 0), 50.0).ToList();
        results.Should().Contain(7);
    }

    [Fact]
    public void Query_NegativeCoordinates_Correct()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(-50, -50, -50), 11);

        hash.QueryNeighbors(new Point3(-50, -50, -50), 10.0)
            .Should().Contain(11);
    }

    [Fact]
    public void Query_ZeroRadius_FindsOnlyExactCell()
    {
        var hash = new SpatialHash<int>(100.0);
        hash.Insert(new Point3(0, 0, 0), 1);
        hash.Insert(new Point3(150, 0, 0), 2);

        // radius=0: only cell (0,0,0) covered → id=1 only
        var results = hash.QueryNeighbors(new Point3(0, 0, 0), 0.0).ToList();
        results.Should().Contain(1).And.NotContain(2);
    }

    [Fact]
    public void Insert_StringValues_WorkCorrectly()
    {
        var hash = new SpatialHash<string>(50.0);
        hash.Insert(new Point3(10, 10, 10), "hello");

        hash.QueryNeighbors(new Point3(10, 10, 10), 5.0)
            .Should().Contain("hello");
    }

    [Fact]
    public void LargeRadius_SpansManyCells_AllValuesFound()
    {
        var hash = new SpatialHash<int>(100.0);
        for (int i = 0; i < 5; i++)
            hash.Insert(new Point3(i * 200.0, 0, 0), i);

        // radius 500 from origin should cover all 5 points (at 0, 200, 400, 600, 800)
        var results = hash.QueryNeighbors(new Point3(400, 0, 0), 500.0).ToList();
        results.Should().HaveCountGreaterOrEqualTo(5);
    }
}
