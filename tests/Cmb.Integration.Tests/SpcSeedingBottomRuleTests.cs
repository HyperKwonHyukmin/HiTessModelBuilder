using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// SPC 바닥 선정: 기둥 발·바닥 프레임만 고정하고 배관·중간 높이 가새는 고정하지 않는다.
public class SpcSeedingBottomRuleTests
{
    private static int _eid = 1000;
    private static void Beam(FeModel m, int a, int b, EntityCategory c = EntityCategory.Structure)
        => m.Elements.Add(new BeamElement(_eid++, a, b, 1, c, Vector3.UnitZ));

    // 기둥 2개(발 N1·N3, z=0) + z=300 가새(N5-N6, 기둥 중간 N2·N4 에 연결) + 바닥 근처 배관(N7-N8, z=50)
    private static FeModel Frame()
    {
        var m = new FeModel();
        m.Nodes.AddRange([
            new Node(1, new Point3(0, 0, 0)),    new Node(2, new Point3(0, 0, 300)),    new Node(9, new Point3(0, 0, 2000)),
            new Node(3, new Point3(2000, 0, 0)), new Node(4, new Point3(2000, 0, 300)), new Node(10, new Point3(2000, 0, 2000)),
            new Node(5, new Point3(700, 0, 300)), new Node(6, new Point3(1400, 0, 300)),
            new Node(7, new Point3(0, 500, 50)),  new Node(8, new Point3(2000, 500, 50)),
        ]);
        Beam(m, 1, 2); Beam(m, 2, 9); Beam(m, 3, 4); Beam(m, 4, 10); Beam(m, 9, 10);
        Beam(m, 2, 5); Beam(m, 5, 6); Beam(m, 6, 4);
        Beam(m, 7, 8, EntityCategory.Pipe);
        return m;
    }

    [Fact]
    public void OnlyColumnFeet_AreFixed_NotBracingOrPipes()
    {
        var spc = SpcSeeding.ResolveModelBottomNodes(Frame(), 500.0).ToList();

        spc.Should().BeEquivalentTo([1, 3], "가새(z=300)는 기둥을 따라 더 아래로 이어지고, 배관은 구조가 아니다");
    }

    [Fact]
    public void ElevatedMainStructure_GetsItsOwnBottom_WhenLowestPointIsSmallStand()
    {
        var m = Frame();
        // 6 m 아래층의 작은 배관 받침대(구조 1요소) — 모델 최저점이지만 주 구조와 구조 부재로 이어지지 않는다
        m.Nodes.AddRange([new Node(20, new Point3(5000, 0, -6000)), new Node(21, new Point3(5000, 0, -5500))]);
        Beam(m, 20, 21);

        var spc = SpcSeeding.ResolveModelBottomNodes(m, 500.0).ToList();

        spc.Should().Contain(20, "받침대 발은 그대로 고정");
        spc.Should().Contain([1, 3], "주 구조(구조 요소의 20% 이상)는 자기 바닥에 고정점이 있어야 한다");
    }

    [Fact]
    public void NoStructure_FallsBackToLegacyBand()
    {
        var m = new FeModel();
        m.Nodes.AddRange([new Node(1, new Point3(0, 0, 0)), new Node(2, new Point3(1000, 0, 100))]);
        Beam(m, 1, 2, EntityCategory.Pipe);

        SpcSeeding.ResolveModelBottomNodes(m, 500.0).Should().BeEquivalentTo([1, 2]);
    }
}
