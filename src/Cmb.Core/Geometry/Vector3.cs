namespace Cmb.Core.Geometry;

public readonly struct Vector3 : IEquatable<Vector3>
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static readonly Vector3 Zero = new(0, 0, 0);
    public static readonly Vector3 UnitX = new(1, 0, 0);
    public static readonly Vector3 UnitY = new(0, 1, 0);
    public static readonly Vector3 UnitZ = new(0, 0, 1);

    public Vector3(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
    }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vector3 Normalize()
    {
        var len = Length;
        if (len < double.Epsilon)
            throw new InvalidOperationException("Cannot normalize a zero vector.");
        return new Vector3(X / len, Y / len, Z / len);
    }

    public static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3 operator *(double s, Vector3 v) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3 operator -(Vector3 v) => new(-v.X, -v.Y, -v.Z);

    public bool Equals(Vector3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is Vector3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);
    public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);
    public override string ToString() => $"Vector3({X}, {Y}, {Z})";
}
