using Cmb.Core.Geometry;
using FluentAssertions;

namespace Cmb.Core.Tests.Geometry;

public class Segment3Tests
{
    [Fact]
    public void Length_HorizontalSegment()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(5, 0, 0));
        s.Length.Should().BeApproximately(5.0, 1e-10);
    }

    [Fact]
    public void Length_3DSegment()
    {
        var s = new Segment3(new Point3(1, 1, 1), new Point3(4, 5, 1));
        s.Length.Should().BeApproximately(5.0, 1e-10);
    }

    [Fact]
    public void Midpoint_IsCenter()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(4, 6, 8));
        s.Midpoint.Should().Be(new Point3(2, 3, 4));
    }

    [Fact]
    public void ClosestPointTo_InsideSegment()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(10, 0, 0));
        var p = new Point3(5, 3, 0);
        var closest = s.ClosestPointTo(p);
        closest.X.Should().BeApproximately(5.0, 1e-10);
        closest.Y.Should().BeApproximately(0.0, 1e-10);
        closest.Z.Should().BeApproximately(0.0, 1e-10);
    }

    [Fact]
    public void ClosestPointTo_BeforeStart_ClampsToStart()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(10, 0, 0));
        var p = new Point3(-5, 0, 0);
        var closest = s.ClosestPointTo(p);
        closest.Should().Be(s.Start);
    }

    [Fact]
    public void ClosestPointTo_AfterEnd_ClampsToEnd()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(10, 0, 0));
        var p = new Point3(15, 0, 0);
        var closest = s.ClosestPointTo(p);
        closest.Should().Be(s.End);
    }

    [Fact]
    public void ClosestPointTo_ZeroLengthSegment_ReturnsStart()
    {
        var p0 = new Point3(1, 1, 1);
        var s = new Segment3(p0, p0);
        var p = new Point3(5, 5, 5);
        var closest = s.ClosestPointTo(p);
        closest.Should().Be(p0);
    }

    [Fact]
    public void DistanceTo_PointAboveSegment()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(10, 0, 0));
        var p = new Point3(5, 4, 0);
        s.DistanceTo(p).Should().BeApproximately(4.0, 1e-10);
    }

    [Fact]
    public void DistanceTo_PointBeyondEnd()
    {
        var s = new Segment3(new Point3(0, 0, 0), new Point3(10, 0, 0));
        var p = new Point3(13, 4, 0);
        s.DistanceTo(p).Should().BeApproximately(5.0, 1e-10);
    }

    [Fact]
    public void Equality_SameSegments_AreEqual()
    {
        var a = new Segment3(new Point3(0, 0, 0), new Point3(1, 2, 3));
        var b = new Segment3(new Point3(0, 0, 0), new Point3(1, 2, 3));
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentSegments_NotEqual()
    {
        var a = new Segment3(new Point3(0, 0, 0), new Point3(1, 2, 3));
        var b = new Segment3(new Point3(0, 0, 0), new Point3(1, 2, 4));
        (a != b).Should().BeTrue();
    }
}
