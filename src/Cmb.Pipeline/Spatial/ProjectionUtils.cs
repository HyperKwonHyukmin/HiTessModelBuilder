using Cmb.Core.Geometry;

namespace Cmb.Pipeline.Spatial;

public static class ProjectionUtils
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// Closest point on segment AB to point P. t is clamped to [0,1].
    /// </summary>
    public static (Point3 Closest, double T, double Dist) ClosestPointOnSegment(
        Point3 p, Point3 a, Point3 b)
    {
        var ab = b - a;
        double abLenSq = ab.LengthSquared;

        if (abLenSq < Epsilon)
            return (a, 0.0, p.DistanceTo(a));

        double t = Vector3.Dot(p - a, ab) / abLenSq;
        t = Math.Clamp(t, 0.0, 1.0);

        var closest = a + ab * t;
        return (closest, t, p.DistanceTo(closest));
    }

    /// <summary>
    /// Closest points between segments AB and CD (Dan Sunday algorithm).
    /// Returns points P on AB (parameter s) and Q on CD (parameter t) with minimum distance.
    /// </summary>
    public static (Point3 P, Point3 Q, double S, double T, double Dist)
        SegmentToSegmentClosestPoints(Point3 a, Point3 b, Point3 c, Point3 d)
    {
        var u = b - a; // direction of AB
        var v = d - c; // direction of CD
        var w = a - c; // vector from C to A

        double aa = u.LengthSquared;          // |u|²
        double bb = Vector3.Dot(u, v);        // u·v
        double cc = v.LengthSquared;          // |v|²
        double dd = Vector3.Dot(u, w);        // u·w
        double ee = Vector3.Dot(v, w);        // v·w

        double D  = aa * cc - bb * bb;        // determinant
        double sN, sD = D;
        double tN, tD = D;

        const double EPS = 1e-18;

        if (D < EPS)
        {
            // Nearly parallel — fix s=0, solve for t
            sN = 0.0; sD = 1.0;
            tN = ee;  tD = cc;
        }
        else
        {
            sN = bb * ee - cc * dd;
            tN = aa * ee - bb * dd;

            if (sN < 0.0)      { sN = 0.0; tN = ee;      tD = cc; }
            else if (sN > sD)  { sN = sD;  tN = ee + bb;  tD = cc; }
        }

        if (tN < 0.0)
        {
            tN = 0.0;
            if (-dd < 0.0)       sN = 0.0;
            else if (-dd > aa)   sN = sD;
            else                 { sN = -dd; sD = aa; }
        }
        else if (tN > tD)
        {
            tN = tD;
            if (-dd + bb < 0.0)      sN = 0.0;
            else if (-dd + bb > aa)  sN = sD;
            else                     { sN = -dd + bb; sD = aa; }
        }

        double sc = Math.Abs(sN) < EPS ? 0.0 : sN / sD;
        double tc = Math.Abs(tN) < EPS ? 0.0 : tN / tD;

        var p = a + u * sc;
        var q = c + v * tc;
        return (p, q, sc, tc, p.DistanceTo(q));
    }

    /// <summary>
    /// True when segments AB and CD are nearly parallel (sin of angle < 1e-4).
    /// </summary>
    public static bool IsNearlyParallel(Point3 a, Point3 b, Point3 c, Point3 d)
    {
        var u = b - a;
        var v = d - c;
        double na = u.Length;
        double nb = v.Length;
        if (na < 1e-18 || nb < 1e-18) return false;

        double sinTheta = Vector3.Cross(u, v).Length / (na * nb);
        return sinTheta < 1e-4;
    }
}
