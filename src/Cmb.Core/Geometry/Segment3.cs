namespace Cmb.Core.Geometry;

public readonly struct Segment3 : IEquatable<Segment3>
{
    public Point3 Start { get; }
    public Point3 End { get; }

    public Segment3(Point3 start, Point3 end)
    {
        Start = start;
        End = end;
    }

    public double Length => Start.DistanceTo(End);

    public Point3 Midpoint
    {
        get
        {
            return new Point3(
                (Start.X + End.X) * 0.5,
                (Start.Y + End.Y) * 0.5,
                (Start.Z + End.Z) * 0.5);
        }
    }

    public Point3 ClosestPointTo(Point3 p)
    {
        var dir = End - Start;
        var lenSq = dir.LengthSquared;
        if (lenSq < double.Epsilon)
            return Start;

        var t = Vector3.Dot(p - Start, dir) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        return Start + dir * t;
    }

    public double DistanceTo(Point3 p) => p.DistanceTo(ClosestPointTo(p));

    public bool Equals(Segment3 other) => Start == other.Start && End == other.End;
    public override bool Equals(object? obj) => obj is Segment3 s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Start, End);
    public static bool operator ==(Segment3 a, Segment3 b) => a.Equals(b);
    public static bool operator !=(Segment3 a, Segment3 b) => !a.Equals(b);
    public override string ToString() => $"Segment3({Start} → {End})";
}
