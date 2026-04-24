using Cmb.Core.Geometry;
using FluentAssertions;

namespace Cmb.Core.Tests.Geometry;

public class Point3Tests
{
    [Fact]
    public void Constructor_NaN_Throws()
    {
        var act = () => new Point3(double.NaN, 0, 0);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_Infinity_Throws()
    {
        var act = () => new Point3(double.PositiveInfinity, 0, 0);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DistanceTo_SamePoint_IsZero()
    {
        var p = new Point3(1, 2, 3);
        p.DistanceTo(p).Should().Be(0);
    }

    [Fact]
    public void DistanceTo_KnownDistance()
    {
        var a = new Point3(0, 0, 0);
        var b = new Point3(3, 4, 0);
        a.DistanceTo(b).Should().BeApproximately(5.0, 1e-10);
    }

    [Fact]
    public void DistanceTo_3D_KnownDistance()
    {
        var a = new Point3(1, 1, 1);
        var b = new Point3(4, 5, 1);
        a.DistanceTo(b).Should().BeApproximately(5.0, 1e-10);
    }

    [Fact]
    public void Add_PointAndVector()
    {
        var p = new Point3(1, 2, 3);
        var v = new Vector3(10, 20, 30);
        var r = p + v;
        r.Should().Be(new Point3(11, 22, 33));
    }

    [Fact]
    public void Subtract_PointAndVector()
    {
        var p = new Point3(10, 20, 30);
        var v = new Vector3(1, 2, 3);
        var r = p - v;
        r.Should().Be(new Point3(9, 18, 27));
    }

    [Fact]
    public void Subtract_TwoPoints_ReturnsVector()
    {
        var a = new Point3(4, 6, 8);
        var b = new Point3(1, 2, 3);
        var v = a - b;
        v.Should().Be(new Vector3(3, 4, 5));
    }

    [Fact]
    public void Equality_SameCoordinates_AreEqual()
    {
        var a = new Point3(1, 2, 3);
        var b = new Point3(1, 2, 3);
        (a == b).Should().BeTrue();
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentCoordinates_NotEqual()
    {
        var a = new Point3(1, 2, 3);
        var b = new Point3(1, 2, 4);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Origin_IsZero()
    {
        Point3.Origin.X.Should().Be(0);
        Point3.Origin.Y.Should().Be(0);
        Point3.Origin.Z.Should().Be(0);
    }
}
