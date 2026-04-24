namespace Cmb.Core.Geometry;

public readonly struct Point3 : IEquatable<Point3>
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static readonly Point3 Origin = new(0, 0, 0);

    public Point3(double x, double y, double z)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z))
            throw new ArgumentException("Point3 coordinates must not be NaN.");
        if (double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z))
            throw new ArgumentException("Point3 coordinates must not be Infinity.");
        X = x; Y = y; Z = z;
    }

    public double DistanceTo(Point3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static Point3 operator +(Point3 p, Vector3 v) => new(p.X + v.X, p.Y + v.Y, p.Z + v.Z);
    public static Point3 operator -(Point3 p, Vector3 v) => new(p.X - v.X, p.Y - v.Y, p.Z - v.Z);
    public static Vector3 operator -(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public bool Equals(Point3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is Point3 p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Point3 a, Point3 b) => a.Equals(b);
    public static bool operator !=(Point3 a, Point3 b) => !a.Equals(b);
    public override string ToString() => $"Point3({X}, {Y}, {Z})";
}
