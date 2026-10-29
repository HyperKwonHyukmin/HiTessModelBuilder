using Cmb.Core.Geometry;
using Cmb.Core.Model;
using FluentAssertions;

namespace Cmb.Core.Tests.Model;

public class ModelEntitiesTests
{
    [Fact]
    public void Node_Tags_BitwiseOr()
    {
        var node = new Node(1, Point3.Origin);
        node.AddTag(NodeTags.Weld);
        node.AddTag(NodeTags.Intersection);
        node.HasTag(NodeTags.Weld).Should().BeTrue();
        node.HasTag(NodeTags.Intersection).Should().BeTrue();
        node.HasTag(NodeTags.Merged).Should().BeFalse();
    }

    [Fact]
    public void Node_RemoveTag_Works()
    {
        var node = new Node(1, Point3.Origin, NodeTags.Weld | NodeTags.Merged);
        node.RemoveTag(NodeTags.Weld);
        node.HasTag(NodeTags.Weld).Should().BeFalse();
        node.HasTag(NodeTags.Merged).Should().BeTrue();
    }

    [Fact]
    public void Node_InvalidId_Throws()
    {
        var act = () => new Node(0, Point3.Origin);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BeamElement_Properties_Set()
    {
        var e = new BeamElement(1, 10, 20, 5, EntityCategory.Structure, Vector3.UnitZ);
        e.Id.Should().Be(1);
        e.StartNodeId.Should().Be(10);
        e.EndNodeId.Should().Be(20);
        e.PropertyId.Should().Be(5);
        e.Category.Should().Be(EntityCategory.Structure);
        e.Orientation.Should().Be(Vector3.UnitZ);
    }

    [Fact]
    public void RigidElement_DependentNodes_ReadOnly()
    {
        var r = new RigidElement(1, 5, [6, 7, 8], "UBOLT");
        r.DependentNodeIds.Count.Should().Be(3);
        r.DependentNodeIds[0].Should().Be(6);
        r.Remark.Should().Be("UBOLT");
    }

    // ── Cm 정규화: 유효 DOF(1~6) 만 허용 (M-2: 뷰어 IsValidCm 과 일치) ──────────

    [Fact]
    public void RigidElement_NormalizeCm_KeepsOnlyDof1To6()
    {
        // 0/7/8/9 등 범위 밖 숫자는 버려지고 1~6 만 남는다.
        var r = new RigidElement(1, 5, [6], cm: "1230789");
        r.Cm.Should().Be("123");
    }

    [Fact]
    public void RigidElement_NormalizeCm_AllOutOfRange_FallsBackToFullDof()
    {
        // 유효 DOF 가 하나도 없으면 기본값 "123456" 으로 폴백(기존 빈 문자열 처리와 동일).
        var r = new RigidElement(1, 5, [6], cm: "0789");
        r.Cm.Should().Be("123456");
    }

    [Fact]
    public void RigidElement_NormalizeCm_ValidPartialDof_Preserved()
    {
        var r = new RigidElement(1, 5, [6], cm: "123456");
        r.Cm.Should().Be("123456");

        var partial = new RigidElement(2, 5, [6], cm: "36");
        partial.Cm.Should().Be("36");
    }

    [Fact]
    public void PointMass_NegativeMass_Throws()
    {
        var act = () => new PointMass(1, 1, -1.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BeamSection_DimensionsEqual_SameValues()
    {
        var a = new BeamSection(1, BeamSectionKind.H, [400, 200, 10, 16], 1);
        var b = new BeamSection(2, BeamSectionKind.H, [400, 200, 10, 16], 1);
        a.DimensionsEqual(b).Should().BeTrue();
    }

    [Fact]
    public void BeamSection_DimensionsEqual_DifferentKind()
    {
        var a = new BeamSection(1, BeamSectionKind.H, [400, 200, 10, 16], 1);
        var b = new BeamSection(2, BeamSectionKind.L, [400, 200, 10, 16], 1);
        a.DimensionsEqual(b).Should().BeFalse();
    }

    [Fact]
    public void Material_DefaultSteel_Values()
    {
        var m = Material.DefaultSteel;
        m.E.Should().Be(206000.0);
        m.Nu.Should().Be(0.3);
        m.Rho.Should().BeApproximately(7.85e-9, 1e-20);
        m.Name.Should().Be("Steel");
    }

    [Fact]
    public void IdAllocator_Sequential()
    {
        var alloc = new IdAllocator();
        alloc.Next().Should().Be(1);
        alloc.Next().Should().Be(2);
        alloc.Next().Should().Be(3);
        alloc.Peek.Should().Be(4);
    }

    [Fact]
    public void IdAllocator_CustomStart()
    {
        var alloc = new IdAllocator(100);
        alloc.Next().Should().Be(100);
        alloc.Next().Should().Be(101);
    }
}
