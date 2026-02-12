using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Model.Geometry
{
  // [1] Vector3D 정의
  public struct Vector3D
  {
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }

    public double Magnitude() => Math.Sqrt(X * X + Y * Y + Z * Z);

    // 정규화 (Normalize)
    public Vector3D Normalize()
    {
      double m = Magnitude();
      return m < 1e-12 ? new Vector3D(0, 0, 0) : new Vector3D(X / m, Y / m, Z / m);
    }

    public double Dot(Vector3D other) => X * other.X + Y * other.Y + Z * other.Z;

    // 벡터 연산
    public static Vector3D operator +(Vector3D a, Vector3D b) => new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator *(Vector3D v, double s) => new Vector3D(v.X * s, v.Y * s, v.Z * s);
    public static Vector3D operator *(double s, Vector3D v) => new Vector3D(v.X * s, v.Y * s, v.Z * s);
    public static Vector3D operator /(Vector3D v, double s) => new Vector3D(v.X / s, v.Y / s, v.Z / s);

    // 호환성: Point3D로 암시적 변환
    public static implicit operator Point3D(Vector3D v) => new Point3D(v.X, v.Y, v.Z);
  }

  // [2] Point3D 정의
  public struct Point3D
  {
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public Point3D(double x, double y, double z) { X = x; Y = y; Z = z; }

    // ★ [핵심 수정] 점 - 점 = 벡터 (Normalize 가능!)
    public static Vector3D operator -(Point3D a, Point3D b)
        => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    // ★ [핵심 수정] 점 + 벡터 = 점 (이동)
    public static Point3D operator +(Point3D p, Vector3D v)
        => new Point3D(p.X + v.X, p.Y + v.Y, p.Z + v.Z);

    // 점 - 벡터 = 점
    public static Point3D operator -(Point3D p, Vector3D v)
        => new Point3D(p.X - v.X, p.Y - v.Y, p.Z - v.Z);

    // 편의용: 점+점, 점*스칼라 (중간점 계산 등)
    public static Point3D operator +(Point3D a, Point3D b) => new Point3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3D operator *(Point3D a, double s) => new Point3D(a.X * s, a.Y * s, a.Z * s);
    public static Point3D operator *(double s, Point3D a) => new Point3D(a.X * s, a.Y * s, a.Z * s);
    public static Point3D operator /(Point3D a, double s) => new Point3D(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Point3D other) => X * other.X + Y * other.Y + Z * other.Z;
  }

  // [3] BoundingBox 정의 (호환성 유지)
  public struct BoundingBox
  {
    public Point3D Min { get; private set; }
    public Point3D Max { get; private set; }
    public bool IsValid { get; private set; }

    public BoundingBox(Point3D min, Point3D max)
    {
      Min = min; Max = max; IsValid = true;
    }

    public BoundingBox(IEnumerable<Point3D> points)
    {
      if (points == null || !points.Any())
      {
        Min = new Point3D(double.MaxValue, double.MaxValue, double.MaxValue);
        Max = new Point3D(double.MinValue, double.MinValue, double.MinValue);
        IsValid = false;
      }
      else
      {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in points)
        {
          if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
          if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
          if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        Min = new Point3D(minX, minY, minZ);
        Max = new Point3D(maxX, maxY, maxZ);
        IsValid = true;
      }
    }

    public static BoundingBox FromSegment(Point3D a, Point3D b, double inflate)
    {
      double minX = Math.Min(a.X, b.X) - inflate, maxX = Math.Max(a.X, b.X) + inflate;
      double minY = Math.Min(a.Y, b.Y) - inflate, maxY = Math.Max(a.Y, b.Y) + inflate;
      double minZ = Math.Min(a.Z, b.Z) - inflate, maxZ = Math.Max(a.Z, b.Z) + inflate;
      return new BoundingBox(new Point3D(minX, minY, minZ), new Point3D(maxX, maxY, maxZ));
    }

    public bool Contains(Point3D p, double tol = 0)
    {
      if (!IsValid) return false;
      return p.X >= Min.X - tol && p.X <= Max.X + tol &&
             p.Y >= Min.Y - tol && p.Y <= Max.Y + tol &&
             p.Z >= Min.Z - tol && p.Z <= Max.Z + tol;
    }
  }
}
