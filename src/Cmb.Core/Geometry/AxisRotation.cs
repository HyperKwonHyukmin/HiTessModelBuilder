namespace Cmb.Core.Geometry;

/// <summary>
/// 주축(X/Y/Z) 기준 회전. 오른손 좌표계, 각도는 도(degree) 단위.
/// 모델 강체 회전(노드 좌표 + 빔 방향 벡터)에 사용한다.
/// </summary>
public static class AxisRotation
{
    /// <summary>방향 벡터를 주축 기준으로 회전한다(평행이동 없음).</summary>
    public static Vector3 RotateVector(Vector3 v, char axis, double angleDeg)
    {
        double r = angleDeg * Math.PI / 180.0;
        double c = Math.Cos(r);
        double s = Math.Sin(r);

        return char.ToUpperInvariant(axis) switch
        {
            'X' => new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c),
            'Y' => new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c),
            'Z' => new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z),
            _ => throw new ArgumentException($"Unsupported rotation axis: {axis}. Use X, Y, or Z.", nameof(axis)),
        };
    }

    /// <summary>점을 pivot 기준으로 주축 회전한다.</summary>
    public static Point3 RotatePoint(Point3 p, char axis, double angleDeg, Point3 pivot)
    {
        var d = p - pivot;                       // Vector3
        var r = RotateVector(d, axis, angleDeg);
        return pivot + r;                        // Point3
    }
}
