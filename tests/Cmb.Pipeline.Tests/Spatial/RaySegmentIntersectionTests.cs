using Cmb.Core.Geometry;
using Cmb.Pipeline.Spatial;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Spatial;

public class RaySegmentIntersectionTests
{
    private const double Tol = 10.0;

    [Fact]
    public void OrthogonalCross_HitsAtExactPoint()
    {
        // Ray: origin=(0,0,0) dir=+X  Segment: (500,-100,0)→(500,100,0)
        var (hit, s, t, lat, pRay, pSeg) =
            RaySegmentIntersection.TryClosest(
                new Point3(0, 0, 0), Vector3.UnitX,
                new Point3(500, -100, 0), new Point3(500, 100, 0),
                Tol);

        hit.Should().BeTrue();
        s.Should().BeApproximately(500, 1e-6);
        t.Should().BeApproximately(0.5, 1e-6);
        lat.Should().BeApproximately(0, 1e-6);
        pRay.X.Should().BeApproximately(500, 1e-6);
        pSeg.X.Should().BeApproximately(500, 1e-6);
        pSeg.Y.Should().BeApproximately(0,   1e-6);
    }

    [Fact]
    public void Parallel_ReturnsFalse()
    {
        // Both along X axis
        var (hit, _, _, _, _, _) =
            RaySegmentIntersection.TryClosest(
                new Point3(0, 0, 0), Vector3.UnitX,
                new Point3(0, 1, 0), new Point3(500, 1, 0),
                Tol);

        hit.Should().BeFalse();
    }

    [Fact]
    public void Skew_LateralWithinTolerance()
    {
        // Ray: (0,0,0) dir=+X  Segment at X=500, Y-offset=5
        var (hit, s, _, lat, pRay, pSeg) =
            RaySegmentIntersection.TryClosest(
                new Point3(0, 0, 0), Vector3.UnitX,
                new Point3(500, 5, -100), new Point3(500, 5, 100),
                Tol);

        hit.Should().BeTrue();
        s.Should().BeApproximately(500, 1e-6);
        lat.Should().BeApproximately(5, 1e-3);
        pRay.Y.Should().BeApproximately(0, 1e-6); // pRay stays on X axis
        pSeg.Y.Should().BeApproximately(5, 1e-6); // pSeg is on the segment
    }

    [Fact]
    public void LateralExceedsTolerance_ReturnsFalse()
    {
        // Ray: +X  Segment at X=500, Y=20 (lateral=20 > Tol=10)
        var (hit, _, _, _, _, _) =
            RaySegmentIntersection.TryClosest(
                new Point3(0, 0, 0), Vector3.UnitX,
                new Point3(500, 20, -100), new Point3(500, 20, 100),
                Tol);

        hit.Should().BeFalse();
    }

    [Fact]
    public void RayBehindOrigin_HitFalseWhenSNegative()
    {
        // Segment is behind the ray origin (X = -500)
        var (hit, s, _, _, _, _) =
            RaySegmentIntersection.TryClosest(
                new Point3(0, 0, 0), Vector3.UnitX,
                new Point3(-500, -100, 0), new Point3(-500, 100, 0),
                Tol);

        // s would be negative, hit should be false
        hit.Should().BeFalse();
    }

    [Fact]
    public void SegmentOutsideT_ReturnsFalse()
    {
        // Segment: (500,50,0)→(500,200,0) — ray at Y=0 misses this segment (t would be negative)
        var (hit, _, _, lat, _, _) =
            RaySegmentIntersection.TryClosest(
                new Point3(0, 0, 0), Vector3.UnitX,
                new Point3(500, 50, 0), new Point3(500, 200, 0),
                Tol);

        // Closest point on segment is t=0 (500,50,0), lateral=50 > Tol=10
        hit.Should().BeFalse();
        lat.Should().BeApproximately(50, 1e-3);
    }
}
