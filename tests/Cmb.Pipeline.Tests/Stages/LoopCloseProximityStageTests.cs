using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class LoopCloseProximityStageTests
{
    private static StageContext Ctx(FeModel model, Tolerances? tol = null)
        => new(model, new RunOptions(tol ?? new Tolerances()), NullLogger.Instance);

    private static Node N(int id, double x, double y, double z)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int a, int b, int property = 1)
        => new(id, a, b, property, EntityCategory.Structure, Vector3.UnitZ);

    private static FeModel BaseModel()
    {
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.L, [75, 75, 6, 6], 1));
        model.Sections.Add(new BeamSection(2, BeamSectionKind.L, [125, 75, 7, 7], 1));
        return model;
    }

    // z=-500 으로 멀리 우회하는 체인 — 두 부재를 같은 그룹으로 묶되 위상상 멀리 떨어뜨린다.
    private static void AddFarDetour(FeModel model, int upperEndNode, int lowerEndNode)
    {
        model.Nodes.AddRange([N(90, -300, 0, -500), N(91, 0, -300, -500)]);
        model.Elements.AddRange([
            E(390, upperEndNode, 90),
            E(391, 90, 91),
            E(392, 91, lowerEndNode),
        ]);
    }

    // 상부 부재(X방향, z=12)와 하부 부재(Y방향, z=0)가 (0,0)에서 12mm 적층 교차.
    // 같은 그룹이지만 위상상 멀다 → 가장 가까운 노드쌍(N2↔N11, 12mm) 1개만 단일-dependent RBE2 로 연결.
    [Fact]
    public void MutualNearestStackedNodes_ConnectedOnce_NoProjectionNoSplit()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, -200, 0, 12), N(2, 0, 0, 12), N(3, 200, 0, 12),   // 상부(X방향)
            N(10, 0, -200, 0), N(11, 0, 0, 0), N(12, 0, 200, 0),   // 하부(Y방향)
        ]);
        model.Elements.AddRange([
            E(100, 1, 2), E(101, 2, 3),
            E(200, 10, 11), E(201, 11, 12),
        ]);
        AddFarDetour(model, 1, 10);   // 1-90-91-10 으로만 이어짐 → N2↔N11 은 ≥3 hop
        int nodesBefore = model.Nodes.Count;

        var ctx = Ctx(model);
        new LoopCloseProximityStage().Execute(ctx);

        model.Rigids.Should().ContainSingle();
        var rbe = model.Rigids[0];
        rbe.Remark.Should().Be(LoopCloseProximityStage.LoopCloseRemark);
        rbe.DependentNodeIds.Should().HaveCount(1);                          // 단일 dependent
        (new[] { rbe.IndependentNodeId }.Concat(rbe.DependentNodeIds))
            .Should().BeEquivalentTo([2, 11]);                              // 최근접 노드쌍
        model.Nodes.Count.Should().Be(nodesBefore);                         // 투영 노드 생성 없음
        ctx.Diagnostics.Should().Contain(d => d.Code == "LOOP_CLOSE_RBE_CREATED");

        // 기존 노드(N11)로 연결 → 타겟 부재 분할 불필요(그대로 보존)
        new SplitByExistingNodesStage().Execute(ctx);
        model.Elements.Should().Contain(e => e.Id == 200);
        model.Elements.Should().Contain(e => e.Id == 201);
    }

    // N2 아래에 N11(12mm)·N12(51mm) 두 후보가 있어도, 상호 최근접인 N2↔N11 1개만 연결.
    // N12 는 자기 최근접이 N2(51mm)지만 N2 의 최근접은 N11 이라 비상호 → 연결 안 됨.
    // (E2996 의 먼 버전·E3005 중복·E2981 묶음이 제거되는 핵심 케이스)
    [Fact]
    public void NonMutualFartherCandidate_NotConnected()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, -200, 0, 12), N(2, 0, 0, 12), N(3, 200, 0, 12),   // 상부(X방향)
            N(10, 0, -50, 0), N(11, 0, 0, 0), N(12, 0, 50, 0),     // 하부(Y방향): N11=12mm, N12=51mm
        ]);
        model.Elements.AddRange([
            E(100, 1, 2), E(101, 2, 3),
            E(200, 10, 11), E(201, 11, 12),
        ]);
        AddFarDetour(model, 1, 10);

        var ctx = Ctx(model);
        new LoopCloseProximityStage().Execute(ctx);

        model.Rigids.Should().ContainSingle();
        var rbe = model.Rigids[0];
        (new[] { rbe.IndependentNodeId }.Concat(rbe.DependentNodeIds))
            .Should().BeEquivalentTo([2, 11]);
        // N12 는 어떤 RBE2 에도 참여하지 않는다
        model.Rigids.Should().NotContain(r =>
            r.IndependentNodeId == 12 || r.DependentNodeIds.Contains(12));
    }

    // 거의 평행(같은 방향) 적층 부재는 연결하지 않는다(교차/T-접합만 대상).
    [Fact]
    public void ParallelStackedMembers_NotConnected()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, 0, 0, 12), N(2, 200, 0, 12),    // 상부(X방향)
            N(10, 0, 0, 0), N(11, 200, 0, 0),    // 하부(X방향, 12mm 아래 — 평행)
        ]);
        model.Elements.AddRange([
            E(100, 1, 2),
            E(200, 10, 11),
        ]);
        AddFarDetour(model, 1, 10);

        new LoopCloseProximityStage().Execute(Ctx(model));

        model.Rigids.Should().BeEmpty();
    }

    // 위상상 가까우면(직결 등 minHops 미만) 공간상 근접해도 연결하지 않는다.
    [Fact]
    public void TopologicallyClose_NotConnected()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, -200, 0, 12), N(2, 0, 0, 12),    // 상부
            N(10, 0, -200, 0), N(11, 0, 0, 0),    // 하부
        ]);
        model.Elements.AddRange([
            E(100, 1, 2),
            E(200, 10, 11),
            E(300, 2, 10),     // N2↔N10 직결 → N2-N10-N11 = 2 hop (minHops=3 미만)
        ]);

        new LoopCloseProximityStage().Execute(Ctx(model));

        model.Rigids.Should().BeEmpty();
    }

    [Fact]
    public void NoElements_ReturnsTrue()
    {
        var model = new FeModel();
        new LoopCloseProximityStage().Execute(Ctx(model)).Should().BeTrue();
    }
}
