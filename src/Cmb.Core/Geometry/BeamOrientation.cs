namespace Cmb.Core.Geometry;

public static class BeamOrientation
{
    private const double ParallelDotLimit = 0.90;
    private const double MinLength = 1e-9;

    public static Vector3 CalculateBarOrientation(Point3 start, Point3 end)
        => CalculateBarOrientation(end - start);

    /// <summary>
    /// CSV <c>ori</c>(단면의 폭 방향 = 요소 z축)를 CBEAM 방향 벡터 v(요소 y축 기준)로 바꾼다.
    /// v = ori × x̂. H 는 플랜지 폭, FBAR 는 판 폭, L 은 DIM1 다리가 ori 쪽에 놓인다.
    /// 샘플 CSV 전수 확인: 수평 H 보의 ori 는 전부 수평(⊥축)이라 이 해석이면 웹이 수직이 되고,
    /// 기존 기본 규칙(v=Z)과 같은 결과가 된다. ori 를 곧바로 v 로 쓰면 수평 H 보가 전부 눕는다.
    /// ori 가 없거나 축과 거의 평행하면 null — 호출자는 기존 기본값을 쓴다.
    /// </summary>
    public static Vector3? FromSectionWidthDirection(Vector3 axis, Vector3 ori)
    {
        if (axis.Length < MinLength || ori.Length < MinLength) return null;
        var xAxis = axis.Normalize();
        var z = ori.Normalize();
        if (Math.Abs(Vector3.Dot(xAxis, z)) >= ParallelDotLimit) return null;
        var v = Vector3.Cross(z, xAxis);
        return v.Length < MinLength ? null : v.Normalize();
    }

    /// <summary>
    /// 요소에 저장된 방향 벡터를 현재 축 기준으로 정리한다. 축과 거의 평행하거나(Nastran FATAL) 0 이면
    /// 기존 기본 규칙(<see cref="CalculateBarOrientation(Vector3)"/>)으로 대체한다.
    /// 저장값이 기본값(UnitZ)이면 결과가 기본 규칙과 비트 단위로 같다 — 배관·구 JSON 출력이 바뀌지 않는다.
    /// </summary>
    public static Vector3 Resolve(Point3 start, Point3 end, Vector3 stored)
    {
        var axis = end - start;
        if (stored.Length < MinLength) return CalculateBarOrientation(axis);
        var xAxis = axis.Normalize();
        var reference = stored.Normalize();
        if (Math.Abs(Vector3.Dot(xAxis, reference)) >= ParallelDotLimit)
            return CalculateBarOrientation(axis);
        var projected = reference - xAxis * Vector3.Dot(reference, xAxis);
        return projected.Length < MinLength ? CalculateBarOrientation(axis) : projected.Normalize();
    }

    public static Vector3 CalculateBarOrientation(Vector3 axis)
    {
        if (axis.Length < MinLength)
            throw new InvalidOperationException("Cannot calculate beam orientation for a zero-length axis.");

        var xAxis = axis.Normalize();
        var reference = Math.Abs(Vector3.Dot(xAxis, Vector3.UnitZ)) < ParallelDotLimit
            ? Vector3.UnitZ
            : Vector3.UnitY;

        var projected = reference - xAxis * Vector3.Dot(reference, xAxis);
        if (projected.Length < MinLength)
        {
            reference = Vector3.UnitX;
            projected = reference - xAxis * Vector3.Dot(reference, xAxis);
        }

        return projected.Normalize();
    }
}
