namespace Cmb.Core.Geometry;

public static class BeamOrientation
{
    private const double ParallelDotLimit = 0.90;
    private const double MinLength = 1e-9;

    public static Vector3 CalculateBarOrientation(Point3 start, Point3 end)
        => CalculateBarOrientation(end - start);

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
