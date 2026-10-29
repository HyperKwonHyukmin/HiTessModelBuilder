using Cmb.Core.Geometry;
using FluentAssertions;

namespace Cmb.Core.Tests.Geometry;

// 주축(X/Y/Z) 회전 수학 검증. 오른손 좌표계, 각도는 도(degree) 단위.
public class AxisRotationTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void RotateVector_90AboutZ_MapsXtoY()
    {
        var r = AxisRotation.RotateVector(new Vector3(10, 0, 0), 'Z', 90);

        r.X.Should().BeApproximately(0, Tol);
        r.Y.Should().BeApproximately(10, Tol);
        r.Z.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void RotateVector_90AboutX_MapsZtoNegY()
    {
        var r = AxisRotation.RotateVector(new Vector3(0, 0, 1), 'X', 90);

        r.X.Should().BeApproximately(0, Tol);
        r.Y.Should().BeApproximately(-1, Tol);
        r.Z.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void RotateVector_90AboutY_MapsXtoNegZ()
    {
        var r = AxisRotation.RotateVector(new Vector3(1, 0, 0), 'Y', 90);

        r.X.Should().BeApproximately(0, Tol);
        r.Y.Should().BeApproximately(0, Tol);
        r.Z.Should().BeApproximately(-1, Tol);
    }

    [Fact]
    public void RotatePoint_AboutPivot_KeepsPivotFixed()
    {
        var pivot = new Point3(10, 20, 30);

        var r = AxisRotation.RotatePoint(pivot, 'Z', 37, pivot);

        r.X.Should().BeApproximately(10, Tol);
        r.Y.Should().BeApproximately(20, Tol);
        r.Z.Should().BeApproximately(30, Tol);
    }

    [Fact]
    public void RotatePoint_90AboutZ_TranslatesAroundPivot()
    {
        var pivot = new Point3(10, 0, 0);

        var r = AxisRotation.RotatePoint(new Point3(20, 0, 0), 'Z', 90, pivot);

        r.X.Should().BeApproximately(10, Tol);
        r.Y.Should().BeApproximately(10, Tol);
        r.Z.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void RotateVector_UnknownAxis_Throws()
    {
        var act = () => AxisRotation.RotateVector(Vector3.UnitX, 'W', 90);

        act.Should().Throw<ArgumentException>();
    }
}
