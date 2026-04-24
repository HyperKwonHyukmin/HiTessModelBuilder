using Cmb.Core.Geometry;

namespace Cmb.Pipeline.Spatial;

public static class RaySegmentIntersection
{
    private const double ParallelEps = 1e-12;

    /// <summary>
    /// Ray(origin + s*dir, s≥0)와 Segment(segA + t*(segB-segA), t∈[0,1])의 최근접점을 계산합니다.
    /// <para>반환:</para>
    /// <list type="bullet">
    ///   <item>hit  — s≥0 이고 t∈[0,1] 이고 lateral≤coplanarTol 이면 true</item>
    ///   <item>s    — ray 파라미터 (origin으로부터 거리)</item>
    ///   <item>t    — segment 파라미터</item>
    ///   <item>lateral — 두 최근접점 사이의 수직 거리</item>
    ///   <item>pRay — origin + s*dir  (ElementA 방향 보존)</item>
    ///   <item>pSeg — segA + t*(segB-segA)  (ElementB segment 위의 점)</item>
    /// </list>
    /// </summary>
    public static (bool hit, double s, double t, double lateral, Point3 pRay, Point3 pSeg)
        TryClosest(
            Point3  rayOrigin, Vector3 rayDir,
            Point3  segA,      Point3  segB,
            double  coplanarTol)
    {
        // ray:     P(s) = rayOrigin + s * d1   (d1 = rayDir, unit vector expected but works for any)
        // segment: Q(t) = segA      + t * d2   (d2 = segB - segA)
        var d1 = new Vector3(rayDir.X, rayDir.Y, rayDir.Z);
        var d2 = segB - segA;
        var r  = rayOrigin - segA;

        double a = Vector3.Dot(d1, d1); // |d1|²
        double e = Vector3.Dot(d2, d2); // |d2|²
        double f = Vector3.Dot(d2, r);

        if (a < ParallelEps || e < ParallelEps)
            return (false, 0, 0, 0, rayOrigin, segA);

        double b = Vector3.Dot(d1, d2);
        double c = Vector3.Dot(d1, r);
        double denom = a * e - b * b;

        double sVal, tVal;

        if (Math.Abs(denom) < ParallelEps)
        {
            // 평행 또는 일치 — 단일 교차점 없음, hit=false
            return (false, 0, 0, 0, rayOrigin, segA);
        }
        else
        {
            sVal = (b * f - c * e) / denom;
            tVal = (a * f - b * c) / denom;
            tVal = Math.Clamp(tVal, 0.0, 1.0);

            // re-solve s for clamped t
            if (tVal != (a * f - b * c) / denom)
                sVal = (b * tVal - c) / a;
        }

        // ray goes forward only
        if (sVal < 0.0)
        {
            sVal = 0.0;
            tVal = Math.Clamp(f / e, 0.0, 1.0);
        }

        var pRay = rayOrigin + d1 * sVal;
        var pSeg = segA + d2 * tVal;
        double lat = pRay.DistanceTo(pSeg);

        bool hit = sVal >= 0.0 && lat <= coplanarTol;
        return (hit, sVal, tVal, lat, pRay, pSeg);
    }
}
