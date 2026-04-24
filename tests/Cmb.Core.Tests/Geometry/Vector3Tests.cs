using Cmb.Core.Geometry;
using FluentAssertions;

namespace Cmb.Core.Tests.Geometry;

public class Vector3Tests
{
    [Fact]
    public void Zero_IsAllZero()
    {
        Vector3.Zero.X.Should().Be(0);
        Vector3.Zero.Y.Should().Be(0);
        Vector3.Zero.Z.Should().Be(0);
    }

    [Fact]
    public void Length_CalculatesCorrectly()
    {
        var v = new Vector3(3, 4, 0);
        v.Length.Should().BeApproximately(5.0, 1e-10);
    }

    [Fact]
    public void LengthSquared_CalculatesCorrectly()
    {
        var v = new Vector3(1, 2, 3);
        v.LengthSquared.Should().BeApproximately(14.0, 1e-10);
    }

    [Fact]
    public void Normalize_ReturnsUnitVector()
    {
        var v = new Vector3(3, 4, 0);
        var n = v.Normalize();
        n.Length.Should().BeApproximately(1.0, 1e-10);
        n.X.Should().BeApproximately(0.6, 1e-10);
        n.Y.Should().BeApproximately(0.8, 1e-10);
    }

    [Fact]
    public void Normalize_ZeroVector_Throws()
    {
        var v = Vector3.Zero;
        var act = () => v.Normalize();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dot_PerpendicularVectors_ReturnsZero()
    {
        var a = new Vector3(1, 0, 0);
        var b = new Vector3(0, 1, 0);
        Vector3.Dot(a, b).Should().Be(0);
    }

    [Fact]
    public void Dot_ParallelVectors_ReturnsProduct()
    {
        var a = new Vector3(2, 0, 0);
        var b = new Vector3(3, 0, 0);
        Vector3.Dot(a, b).Should().BeApproximately(6.0, 1e-10);
    }

    [Fact]
    public void Cross_XcrossY_ReturnsZ()
    {
        var x = new Vector3(1, 0, 0);
        var y = new Vector3(0, 1, 0);
        var z = Vector3.Cross(x, y);
        z.X.Should().BeApproximately(0, 1e-10);
        z.Y.Should().BeApproximately(0, 1e-10);
        z.Z.Should().BeApproximately(1, 1e-10);
    }

    [Fact]
    public void Add_TwoVectors()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(4, 5, 6);
        var r = a + b;
        r.Should().Be(new Vector3(5, 7, 9));
    }

    [Fact]
    public void Subtract_TwoVectors()
    {
        var a = new Vector3(5, 7, 9);
        var b = new Vector3(1, 2, 3);
        var r = a - b;
        r.Should().Be(new Vector3(4, 5, 6));
    }

    [Fact]
    public void Multiply_ScalarRight()
    {
        var v = new Vector3(1, 2, 3);
        var r = v * 2.0;
        r.Should().Be(new Vector3(2, 4, 6));
    }

    [Fact]
    public void Multiply_ScalarLeft()
    {
        var v = new Vector3(1, 2, 3);
        var r = 3.0 * v;
        r.Should().Be(new Vector3(3, 6, 9));
    }

    [Fact]
    public void Negate_Vector()
    {
        var v = new Vector3(1, -2, 3);
        var r = -v;
        r.Should().Be(new Vector3(-1, 2, -3));
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(1, 2, 3);
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentValues_NotEqual()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(1, 2, 4);
        (a != b).Should().BeTrue();
    }
}
