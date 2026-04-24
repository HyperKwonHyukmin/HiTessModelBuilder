using Cmb.Core.Model;
using FluentAssertions;

namespace Cmb.Core.Tests.Model;

public class SectionGeometryTests
{
    private static BeamSection S(BeamSectionKind kind, params double[] dims)
        => new(1, kind, dims, 1);

    [Theory]
    [InlineData(100, 6, 200, 10, 200)] // web height 200 > flange width 100
    [InlineData(250, 8, 100, 12, 250)] // flange width 250 > web height 100
    public void H_ReturnsMaxOfDim0AndDim2(double d0, double d1, double d2, double d3, double expected)
        => S(BeamSectionKind.H, d0, d1, d2, d3).MaxCrossSectionDim().Should().Be(expected);

    [Theory]
    [InlineData(150, 90, 10, 10, 150)]
    [InlineData(80, 120, 10, 10, 120)]
    public void L_ReturnsMaxOfDim0AndDim1(double d0, double d1, double d2, double d3, double expected)
        => S(BeamSectionKind.L, d0, d1, d2, d3).MaxCrossSectionDim().Should().Be(expected);

    [Fact]
    public void Tube_ReturnsDim0()
        => S(BeamSectionKind.Tube, 200, 10).MaxCrossSectionDim().Should().Be(200);

    [Fact]
    public void Rod_ReturnsDim0()
        => S(BeamSectionKind.Rod, 50).MaxCrossSectionDim().Should().Be(50);

    [Fact]
    public void Channel_ReturnsMax()
        => S(BeamSectionKind.Channel, 100, 50, 8, 8).MaxCrossSectionDim().Should().Be(100);

    [Fact]
    public void Bar_ReturnsMaxOfDim0AndDim1()
        => S(BeamSectionKind.Bar, 60, 80).MaxCrossSectionDim().Should().Be(80);

    [Fact]
    public void EmptyDims_ReturnsZero()
        => S(BeamSectionKind.H, []).MaxCrossSectionDim().Should().Be(0.0);
}
