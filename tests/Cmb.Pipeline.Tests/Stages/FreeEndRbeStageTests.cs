using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class FreeEndRbeStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    // thin Rod: maxDim=25mm
    private static readonly BeamSection ThinSection = new(1, BeamSectionKind.Rod, [25.0], 1);
    private static readonly BeamSection LargeSection = new(2, BeamSectionKind.H, [200.0, 20.0, 200.0, 10.0], 1);

    private static StageContext MakeCtx(FeModel model,
        double extraMarginMm = 25.0, double endpointMarginMm = 5.0, double nodeMergeMm = 1.0)
        => new(model, new RunOptions(new Tolerances(
                FreeEndRbeExtraMarginMm:    extraMarginMm,
                FreeEndRbeEndpointMarginMm: endpointMarginMm,
                NodeMergeMm:               nodeMergeMm)),
            NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end, int propId = 1)
        => new(id, start, end, propId, EntityCategory.Structure, Up);

    private static FeModel BaseModel()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(ThinSection);
        m.Sections.Add(LargeSection);
        return m;
    }

    // ── 빈 모델 / 자유단 없음 ────────────────────────────────────────────────

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var result = new FreeEndRbeStage().Execute(MakeCtx(BaseModel()));
        result.Should().BeTrue();
    }

    [Fact]
    public void RigidOnlyNode_IsNotTreatedAsBeamFreeEnd()
    {
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 50)]);
        m.Elements.Add(E(10, 1, 2));
        m.Rigids.Add(new RigidElement(20, 3, [1], "TEST"));

        var ctx = MakeCtx(m);
        new FreeEndRbeStage().Execute(ctx);

        ctx.Diagnostics.Should().NotContain(d =>
            d.Code == "FREE_END_UNRESOLVED" && d.NodeId == 3);
    }

    [Fact]
    public void NoFreeEnds_NoOp()
    {
        // N1-N2-N3 체인: 모두 degree≥2 (N2) 또는 SPC 가능
        // But N1,N3 are degree=1 — make a closed loop to have no free ends
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 50, 0), N(3, 100, 0)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 2, 3), E(12, 1, 3)]); // closed triangle

        int rigidsBefore = m.Rigids.Count;
        new FreeEndRbeStage().Execute(MakeCtx(m));

        m.Rigids.Count.Should().Be(rigidsBefore);
    }

    // ── 단일 자유단 — interior projection ────────────────────────────────────

    [Fact]
    public void SingleFreeEnd_InteriorCapture_RbeAdded()
    {
        // Target: N1(0,0) → N2(200,0). Free chain: N3(100,30) → N4(100,100).
        // N3 is free-end. tRaw≈0.5 (interior). dist=30mm. allowed=25+25=50mm → accepted.
        // proj = (100,0).
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 200, 0), N(3, 100, 30), N(4, 100, 100)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new FreeEndRbeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle(r => r.Remark == "FREE_END_RBE");
        var rbe = m.Rigids.First(r => r.Remark == "FREE_END_RBE");
        rbe.DependentNodeIds.Should().Contain(3);              // free node as dependent
        var projNode = m.Nodes.First(n => n.Id == rbe.IndependentNodeId);
        projNode.Position.X.Should().BeApproximately(100, 0.1);
        projNode.Position.Y.Should().BeApproximately(0,   0.1);
    }

    // ── endpoint capture (tRaw > 1.0001) ────────────────────────────────────

    [Fact]
    public void SingleFreeEnd_EndpointCapture_tRaw1_01_Accepted()
    {
        // Target: N1(0,0) → N2(100,0) [horizontal] + N2 → N3(100,100) [vertical].
        // Free: N4(108,8) → N5(108,80) [vertical].
        // N4 onto E10: tRaw=1.08 → isNearEndpoint (dist≈11.3mm ≤ 5*4=20mm) ✓.
        // N4 onto E11: collinear with free chain direction (both vertical) → filtered.
        // Only N4 captures → exactly 1 RBE.
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 100, 100), N(4, 108, 8), N(5, 108, 80)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 2, 3), E(12, 4, 5)]);

        new FreeEndRbeStage().Execute(MakeCtx(m, endpointMarginMm: 5.0));

        m.Rigids.Should().ContainSingle(r => r.Remark == "FREE_END_RBE");
        m.Rigids.First(r => r.Remark == "FREE_END_RBE").DependentNodeIds.Should().Contain(4);
    }

    // ── 공선 필터 ────────────────────────────────────────────────────────────

    [Fact]
    public void SingleFreeEnd_CollinearTarget_Skipped()
    {
        // Target: N1(0,0) → N2(200,0). Free: N3(300,0) → N4(400,0).
        // Free direction: (1,0,0). Target direction: (1,0,0). cos=1>0.985 → collinear → skip.
        // dist=100mm is also > threshold, but collinear filter fires first.
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 200, 0), N(3, 300, 0), N(4, 400, 0)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(m, extraMarginMm: 25.0);
        new FreeEndRbeStage().Execute(ctx);

        m.Rigids.Should().BeEmpty();
        ctx.Diagnostics.Should().Contain(d => d.Code == "FREE_END_UNRESOLVED");
    }

    [Fact]
    public void SingleFreeEnd_CollinearEndpointGap_UsesNearestEndpoint()
    {
        // Regression for short endpoint gaps on the same axis.
        // The old collinear filter rejected the nearest endpoint candidate,
        // allowing a farther non-collinear endpoint to win and create a long RBE.
        var m = BaseModel();
        m.Nodes.AddRange([
            N(1, 0, 0), N(2, 100, 0),
            N(3, 130, 0), N(4, 230, 0),
            N(5, 100, 250)
        ]);
        m.Elements.AddRange([
            E(10, 1, 2),
            E(11, 3, 4),
            E(12, 2, 5, propId: 2)
        ]);

        new FreeEndRbeStage().Execute(MakeCtx(m, endpointMarginMm: 10.0));

        m.Rigids.Should().ContainSingle(r =>
            r.Remark == "FREE_END_RBE" &&
            r.IndependentNodeId == 2 &&
            r.DependentNodeIds.Contains(3));
        m.Rigids.Should().NotContain(r =>
            r.Remark == "FREE_END_RBE" &&
            r.IndependentNodeId == 5 &&
            r.DependentNodeIds.Contains(3));
    }

    // ── 거리 초과 ────────────────────────────────────────────────────────────

    [Fact]
    public void SingleFreeEnd_TooFar_NoRbe_Unresolved()
    {
        // Target: N1(0,0) → N2(100,0). Free: N3(50,300) → N4(50,400).
        // dist=300mm. allowed=25+25=50mm. 300>50 → no candidate.
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 300), N(4, 50, 400)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(m);
        new FreeEndRbeStage().Execute(ctx);

        m.Rigids.Should().BeEmpty();
        ctx.Diagnostics.Should().Contain(d =>
            d.Code == "FREE_END_UNRESOLVED" && d.Severity == DiagnosticSeverity.Warning);
    }

    // ── 자기 소속 요소 제외 ──────────────────────────────────────────────────

    [Fact]
    public void SingleFreeEnd_SelfExcluded_OtherTargetUsed()
    {
        // Chain: N1(0,0) → N2(100,0) → N3(200,0).  N1 is free-end.
        // N1 soaks to E10 (N1-N2) — self, excluded.
        // Perpendicular target: N4(50,30) → N5(50,80) cross-beam.
        // dist(N1 → E_N4N5 line x=50): sqrt((50)^2+(30)^2)=~58. Too far with margin 50.
        // So N1 has no valid non-self target → unresolved.
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 200, 0)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);

        var ctx = MakeCtx(m);
        new FreeEndRbeStage().Execute(ctx);

        // N1 free-end: only candidate is its own element E10 → excluded → unresolved
        // N3 free-end: only candidate is E11 (self) → unresolved
        m.Rigids.Should().BeEmpty();
        ctx.Diagnostics.Should().Contain(d => d.Code == "FREE_END_UNRESOLVED");
    }

    // ── 기존 노드 재사용 ─────────────────────────────────────────────────────

    [Fact]
    public void ProjectionCoincidesWithExistingNode_NodeReused()
    {
        // Target: N1(0,0) → N2(200,0). N_mid(100,0) already exists.
        // Free: N3(100,20) → N4(100,80). Projection = (100,0) ≈ N_mid within 1mm.
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 200, 0), N(10, 100, 0), N(3, 100, 20), N(4, 100, 80)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);
        int nodeBefore = m.Nodes.Count;

        new FreeEndRbeStage().Execute(MakeCtx(m));

        m.Nodes.Count.Should().Be(nodeBefore); // no new node
        var rbe = m.Rigids.First(r => r.Remark == "FREE_END_RBE");
        rbe.IndependentNodeId.Should().Be(10);
    }

    [Fact]
    public void FreeNodeAlreadyOnTargetInterior_DeferredToSplit_NoRbe()
    {
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 200, 0), N(3, 100, 0), N(4, 100, 80)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(m);
        new FreeEndRbeStage().Execute(ctx);

        m.Rigids.Should().NotContain(r =>
            r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(3));
        ctx.Diagnostics.Should().Contain(d => d.Code == "FREE_END_ON_TARGET_DEFERRED");
    }

    [Fact]
    public void ProjectionNearTargetEndpointAlreadyConnectedByElement_NoRbe()
    {
        var m = BaseModel();
        m.Nodes.AddRange([
            N(1, 0, 0, 0),
            N(2, 150, 0, 0),
            N(3, 150, 0, 500)
        ]);
        m.Elements.AddRange([
            E(10, 2, 1, propId: 1),
            E(11, 2, 3, propId: 2)
        ]);

        var ctx = MakeCtx(m, endpointMarginMm: 10.0);
        new FreeEndRbeStage().Execute(ctx);

        m.Rigids.Should().NotContain(r =>
            r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(1));
    }

    [Fact]
    public void FreeNodeCoincidentWithTargetEndpoint_UsesEndpointNode_NotSelfLoop()
    {
        var m = BaseModel();
        m.Nodes.AddRange([
            N(1, 0, 0), N(2, 200, 0), N(3, 0, 0), N(4, 0, 80), N(5, 0, -80)
        ]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4), E(12, 1, 5)]);

        new FreeEndRbeStage().Execute(MakeCtx(m));

        m.Rigids.Should().ContainSingle(r =>
            r.Remark == "FREE_END_RBE" &&
            r.IndependentNodeId == 1 &&
            r.DependentNodeIds.Contains(3));
    }

    // ── 복수 자유단 각각 독립 RBE ────────────────────────────────────────────

    [Fact]
    public void MultipleFreeEnds_EachGetsOwnRbe()
    {
        // Target: N1(0,0) → N2(400,0).
        // Free A: N3(100,25) → N4(100,100).  N3 free-end.
        // Free B: N5(300,25) → N6(300,100).  N5 free-end.
        // Both project onto target interior: dist=25mm ≤ 50mm → each gets own RBE.
        var m = BaseModel();
        m.Nodes.AddRange([N(1, 0, 0), N(2, 400, 0),
                          N(3, 100, 25), N(4, 100, 100),
                          N(5, 300, 25), N(6, 300, 100)]);
        m.Elements.AddRange([E(10, 1, 2), E(11, 3, 4), E(12, 5, 6)]);

        new FreeEndRbeStage().Execute(MakeCtx(m));

        m.Rigids.Count(r => r.Remark == "FREE_END_RBE").Should().Be(2);
        m.Rigids.Should().Contain(r => r.DependentNodeIds.Contains(3));
        m.Rigids.Should().Contain(r => r.DependentNodeIds.Contains(5));
    }

    // ── 축 방향 후보 우선 ────────────────────────────────────────────────────

    private static readonly BeamSection PostAngle  = new(3, BeamSectionKind.L, [90.0, 90.0, 10.0, 10.0], 1);
    private static readonly BeamSection SideAngle  = new(4, BeamSectionKind.L, [75.0, 75.0, 6.0, 6.0], 1);
    private static readonly BeamSection FloorHBeam = new(5, BeamSectionKind.H, [130.0, 20.0, 150.0, 7.0], 1);

    /// <summary>
    /// 3496-35210-A508372 N291 재현: 수직 앵글 기둥의 하단 자유단(N11) 옆 76mm 에 수평 앵글,
    /// 아래 75mm(+Y 45mm) 에 H빔(상부 플랜지 윗면 = 자유단 높이)이 있다.
    /// 거리만 보면 옆 앵글(76)이 H빔(87)보다 가깝지만, 기둥 끝은 축 방향(아래) H빔에 얹혀야 한다.
    /// </summary>
    private static FeModel VerticalPostOverHBeamModel()
    {
        var m = BaseModel();
        m.Sections.AddRange([PostAngle, SideAngle, FloorHBeam]);
        m.Nodes.AddRange([
            N(10, 0, 0, 138.5), N(11, 0, 0, 0),              // 수직 기둥 (N11 = 하단 자유단)
            N(20, -76, -1000, 0), N(21, -76, 1000, 0),       // 옆 수평 앵글 (같은 높이, X -76)
            N(30, -1000, -45, -75), N(31, 1000, -45, -75),   // 아래 H빔 (Z -75, Y -45)
        ]);
        m.Elements.AddRange([E(40, 10, 11, propId: 3), E(41, 20, 21, propId: 4), E(42, 30, 31, propId: 5)]);
        return m;
    }

    [Fact]
    public void VerticalPostFreeEnd_PrefersAxialHBeamBelow_OverCloserLateralAngle()
    {
        var m = VerticalPostOverHBeamModel();

        new FreeEndRbeStage().Execute(MakeCtx(m));

        var rbe = m.Rigids.Single(r => r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(11));
        var indep = m.Nodes.First(n => n.Id == rbe.IndependentNodeId).Position;
        indep.X.Should().BeApproximately(0,   0.1);
        indep.Y.Should().BeApproximately(-45, 0.1);
        indep.Z.Should().BeApproximately(-75, 0.1);
        rbe.DetailRemark.Should().Be(FreeEndRbeStage.AxialDetailRemark);
    }

    [Fact]
    public void AxialPreferenceDisabled_KeepsNearestLateral()
    {
        // 선호 여유 0 → 순수 최근접(옆 앵글 76mm) 선택. 기존 동작과 같다.
        var m = VerticalPostOverHBeamModel();
        var ctx = new StageContext(m, new RunOptions(new Tolerances(FreeEndRbeAxialPreferenceMm: 0.0)),
            NullLogger.Instance);

        new FreeEndRbeStage().Execute(ctx);

        var rbe = m.Rigids.Single(r => r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(11));
        var indep = m.Nodes.First(n => n.Id == rbe.IndependentNodeId).Position;
        indep.X.Should().BeApproximately(-76, 0.1);
        indep.Z.Should().BeApproximately(0,   0.1);
    }

    [Fact]
    public void AxialCandidateMuchFarther_KeepsNearestLateral()
    {
        // 옆 앵글이 20mm 로 붙어 있고 H빔은 축 방향 160mm 아래 → 160 > 20 + 100 이라 옆 앵글 유지.
        var m = BaseModel();
        m.Sections.AddRange([PostAngle, SideAngle, FloorHBeam]);
        m.Nodes.AddRange([
            N(10, 0, 0, 138.5), N(11, 0, 0, 0),
            N(20, -20, -1000, 0), N(21, -20, 1000, 0),
            N(30, -1000, 0, -160), N(31, 1000, 0, -160),
        ]);
        m.Elements.AddRange([E(40, 10, 11, propId: 3), E(41, 20, 21, propId: 4), E(42, 30, 31, propId: 5)]);

        new FreeEndRbeStage().Execute(MakeCtx(m));

        var rbe = m.Rigids.Single(r => r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(11));
        m.Nodes.First(n => n.Id == rbe.IndependentNodeId).Position.X.Should().BeApproximately(-20, 0.1);
    }

    [Fact]
    public void LateralMemberAlreadyTouching_KeptEvenIfAxialMemberBelow()
    {
        // 옆 앵글이 30mm(≤ 75/2) 로 자유단에 이미 닿아 있다 → 아래 H빔(축 방향, 얹힘)이 있어도 옆 앵글 유지.
        // (09 N319: 옆 L100 6mm vs 아래 H200 103mm 같은 경우)
        var m = BaseModel();
        m.Sections.AddRange([PostAngle, SideAngle, FloorHBeam]);
        m.Nodes.AddRange([
            N(10, 0, 0, 138.5), N(11, 0, 0, 0),
            N(20, -30, -1000, 0), N(21, -30, 1000, 0),
            N(30, -1000, -45, -75), N(31, 1000, -45, -75),
        ]);
        m.Elements.AddRange([E(40, 10, 11, propId: 3), E(41, 20, 21, propId: 4), E(42, 30, 31, propId: 5)]);

        new FreeEndRbeStage().Execute(MakeCtx(m));

        var rbe = m.Rigids.Single(r => r.Remark == "FREE_END_RBE" && r.DependentNodeIds.Contains(11));
        m.Nodes.First(n => n.Id == rbe.IndependentNodeId).Position.X.Should().BeApproximately(-30, 0.1);
    }
}
