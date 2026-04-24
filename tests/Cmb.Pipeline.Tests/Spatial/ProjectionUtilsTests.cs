using Cmb.Core.Geometry;
using Cmb.Pipeline.Spatial;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Spatial;

public class ProjectionUtilsTests
{
    // ─── ClosestPointOnSegment ────────────────────────────────────────────────

    [Fact]
    public void ClosestPoint_PerpendicularFromMidpoint_T05()
    {
        // Segment (0,0,0)→(100,0,0), point (50,50,0)
        var (closest, t, dist) = ProjectionUtils.ClosestPointOnSegment(
            new Point3(50, 50, 0), new Point3(0, 0, 0), new Point3(100, 0, 0));

        t.Should().BeApproximately(0.5, 1e-10);
        dist.Should().BeApproximately(50.0, 1e-10);
        closest.X.Should().BeApproximately(50, 1e-10);
        closest.Y.Should().BeApproximately(0, 1e-10);
    }

    [Fact]
    public void ClosestPoint_BeyondEnd_ClampsToEndpoint()
    {
        var (closest, t, dist) = ProjectionUtils.ClosestPointOnSegment(
            new Point3(200, 0, 0), new Point3(0, 0, 0), new Point3(100, 0, 0));

        t.Should().BeApproximately(1.0, 1e-10);
        closest.X.Should().BeApproximately(100, 1e-10);
        dist.Should().BeApproximately(100.0, 1e-10);
    }

    [Fact]
    public void ClosestPoint_BeforeStart_ClampsToStart()
    {
        var (closest, t, dist) = ProjectionUtils.ClosestPointOnSegment(
            new Point3(-50, 0, 0), new Point3(0, 0, 0), new Point3(100, 0, 0));

        t.Should().BeApproximately(0.0, 1e-10);
        closest.X.Should().BeApproximately(0, 1e-10);
        dist.Should().BeApproximately(50.0, 1e-10);
    }

    [Fact]
    public void ClosestPoint_PointOnSegment_ZeroDistance()
    {
        var (_, _, dist) = ProjectionUtils.ClosestPointOnSegment(
            new Point3(30, 0, 0), new Point3(0, 0, 0), new Point3(100, 0, 0));

        dist.Should().BeApproximately(0.0, 1e-10);
    }

    [Fact]
    public void ClosestPoint_ZeroLengthSegment_ReturnsStartPoint()
    {
        var (closest, t, _) = ProjectionUtils.ClosestPointOnSegment(
            new Point3(10, 10, 10), new Point3(5, 5, 5), new Point3(5, 5, 5));

        t.Should().BeApproximately(0.0, 1e-10);
        closest.Should().Be(new Point3(5, 5, 5));
    }

    // ─── SegmentToSegmentClosestPoints ───────────────────────────────────────

    [Fact]
    public void SegSeg_PerpendicularCrossing_NearZeroDistance()
    {
        // AB: X-axis (0,0,0)→(100,0,0)
        // CD: Y-axis through (50,0,0): (50,-100,0)→(50,100,0)
        var (p, q, s, t, dist) = ProjectionUtils.SegmentToSegmentClosestPoints(
            new Point3(0, 0, 0),   new Point3(100, 0, 0),
            new Point3(50, -100, 0), new Point3(50, 100, 0));

        dist.Should().BeApproximately(0.0, 1e-6);
        s.Should().BeApproximately(0.5, 1e-6);
        t.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void SegSeg_TJunction_CorrectClosestPoint()
    {
        // AB: (0,0,0)→(100,0,0)
        // CD: starts at midpoint of AB, goes up: (50,0,0)→(50,100,0)
        var (_, _, s, t, dist) = ProjectionUtils.SegmentToSegmentClosestPoints(
            new Point3(0, 0, 0),  new Point3(100, 0, 0),
            new Point3(50, 0, 0), new Point3(50, 100, 0));

        dist.Should().BeApproximately(0.0, 1e-6);
        s.Should().BeApproximately(0.5, 1e-6);
        t.Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void SegSeg_SkewLines_PositiveDistance()
    {
        // AB: along X at z=0
        // CD: along Y at z=10 — skew, minimum distance = 10
        var (_, _, _, _, dist) = ProjectionUtils.SegmentToSegmentClosestPoints(
            new Point3(0, 0, 0),   new Point3(100, 0, 0),
            new Point3(50, -50, 10), new Point3(50, 50, 10));

        dist.Should().BeApproximately(10.0, 1e-6);
    }

    [Fact]
    public void SegSeg_ParallelSegments_ReturnsFiniteDistance()
    {
        // Two parallel segments — algorithm should not throw
        var (_, _, _, _, dist) = ProjectionUtils.SegmentToSegmentClosestPoints(
            new Point3(0, 0, 0), new Point3(100, 0, 0),
            new Point3(0, 5, 0), new Point3(100, 5, 0));

        dist.Should().BeApproximately(5.0, 1e-6);
    }

    [Fact]
    public void IsNearlyParallel_ParallelSegments_ReturnsTrue()
    {
        bool result = ProjectionUtils.IsNearlyParallel(
            new Point3(0, 0, 0), new Point3(100, 0, 0),
            new Point3(0, 5, 0), new Point3(100, 5, 0));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsNearlyParallel_PerpendicularSegments_ReturnsFalse()
    {
        bool result = ProjectionUtils.IsNearlyParallel(
            new Point3(0, 0, 0), new Point3(100, 0, 0),
            new Point3(0, 0, 0), new Point3(0, 100, 0));

        result.Should().BeFalse();
    }
}
