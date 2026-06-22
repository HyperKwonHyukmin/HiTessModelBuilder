using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class UboltRbeStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, double snapDist = 50.0, double nodeMergeMm = 1.0)
        => new(model, new RunOptions(new Tolerances(
                UboltSnapMaxDistMm: snapDist,
                NodeMergeMm: nodeMergeMm)),
            NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Up);

    private static BeamElement E(int id, int start, int end, int propertyId, EntityCategory category)
        => new(id, start, end, propertyId, category, Up);

    // Helper: UBOLT marker (no deps, remark="UBOLT")
    private static RigidElement Ubolt(int id, int indepNode, string? sourceName = null)
        => new(id, indepNode, [], "UBOLT", sourceName);

    // ── No UBOLT rigids → early exit ──────────────────────────────────────

    [Fact]
    public void NoUbolts_ReturnsTrue_NoChange()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0));
        model.Nodes.Add(N(2, 100, 0));
        model.Elements.Add(E(10, 1, 2));

        var result = new UboltRbeStage().Execute(MakeCtx(model));

        result.Should().BeTrue();
        model.Rigids.Should().BeEmpty();
    }

    // ── UBOLT within snapDist (phase 1) → snapped ─────────────────────────

    [Fact]
    public void Ubolt_WithinPhase1Radius_SnappedToStructure()
    {
        var model = new FeModel();
        // Structure beam: 1(0,0) → 2(100,0)
        // UBOLT at node 3(50,30): projection=(50,0), dist=30mm < snapDist=50mm
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 30)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        new UboltRbeStage().Execute(MakeCtx(model));

        var updated = model.Rigids.First(r => r.Id == 20);
        updated.DependentNodeIds.Should().HaveCount(1);
    }

    [Fact]
    public void Ubolt_Snapped_DependentNodeIsNearProjection()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 30)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        new UboltRbeStage().Execute(MakeCtx(model));

        var depNodeId = model.Rigids.First(r => r.Id == 20).DependentNodeIds[0];
        var depNode   = model.Nodes.First(n => n.Id == depNodeId);
        depNode.Position.X.Should().BeApproximately(50, 0.01);
        depNode.Position.Y.Should().BeApproximately(0,  0.01);
    }

    [Fact]
    public void Ubolt_Snapped_EmitsInfoDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 30)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        var ctx = MakeCtx(model);
        new UboltRbeStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Info && d.Code == "UBOLT_SNAPPED");
    }

    // ── Phase 2 (widen to 2× radius) ──────────────────────────────────────

    [Fact]
    public void Ubolt_OutsidePhase1_WithinPhase2_SnappedWithWideRadius()
    {
        var model = new FeModel();
        // snapDist=50mm. UBOLT at dist=70mm from structure → missed by phase 1, caught by phase 2 (100mm)
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 70)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        new UboltRbeStage().Execute(MakeCtx(model, snapDist: 50.0));

        var updated = model.Rigids.First(r => r.Id == 20);
        updated.DependentNodeIds.Should().HaveCount(1);
    }

    // ── Fallback: too far → NodeTags.Boundary ─────────────────────────────

    [Fact]
    public void Ubolt_TooFarEvenAfterPhase2_MarkedAsBoundary()
    {
        var model = new FeModel();
        // UBOLT at dist=200mm from structure; 2×snapDist=100mm → no snap
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 200)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        new UboltRbeStage().Execute(MakeCtx(model, snapDist: 50.0));

        var uboltNode = model.Nodes.First(n => n.Id == 3);
        uboltNode.Tags.HasFlag(NodeTags.Boundary).Should().BeTrue();
    }

    [Fact]
    public void Ubolt_TooFar_EmitsWarningDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 200)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        var ctx = MakeCtx(model, snapDist: 50.0);
        new UboltRbeStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Code == "UBOLT_FALLBACK");
    }

    [Fact]
    public void Ubolt_TooFar_RbeRemoved_IndepTaggedBoundary()
    {
        // Fallback U-bolt(snap 실패)는 dep가 비어있어 BDF에서 무효 RBE2 → stage가 제거하고
        // indep 노드만 Boundary 태그로 남겨 SPC1로 export.
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 200)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        new UboltRbeStage().Execute(MakeCtx(model, snapDist: 50.0));

        model.Rigids.Should().NotContain(r => r.Id == 20);
        model.Nodes.First(n => n.Id == 3).Tags.HasFlag(NodeTags.Boundary).Should().BeTrue();
    }

    // ── No structure → warning, all fallback ──────────────────────────────

    [Fact]
    public void NoStructure_AllUboltsFallback_EmitsWarning()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 50, 30));
        model.Rigids.Add(Ubolt(20, 1));

        var ctx = MakeCtx(model);
        new UboltRbeStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Code == "UBOLT_NO_STRUCTURE");
        model.Nodes.First(n => n.Id == 1).Tags.HasFlag(NodeTags.Boundary).Should().BeTrue();
    }

    // ── Projection reuses existing node if within mergeTol ────────────────

    [Fact]
    public void Ubolt_ProjectionCoincidentWithExistingNode_ReusesNode()
    {
        var model = new FeModel();
        // UBOLT at (100, 30): projection onto beam 1→2 is exactly (100,0) = node 2
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 100, 30)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        int nodesBefore = model.Nodes.Count;
        new UboltRbeStage().Execute(MakeCtx(model));

        // Node 2 reused → no new node created
        model.Nodes.Count.Should().Be(nodesBefore);
        model.Rigids.First(r => r.Id == 20).DependentNodeIds[0].Should().Be(2);
    }

    // ── Multiple UBOLTs ────────────────────────────────────────────────────

    [Fact]
    public void MultipleUbolts_EachSnappedIndependently()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 20, 20), N(4, 80, 20)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));
        model.Rigids.Add(Ubolt(21, 4));

        new UboltRbeStage().Execute(MakeCtx(model));

        model.Rigids.Should().AllSatisfy(r => r.DependentNodeIds.Should().HaveCount(1));
    }

    // 두 U-bolt(파이프)가 같은 구조 노드(2)에 클램프되면, 한쪽을 버리지 말고 구조 노드를
    // independent 로, 두 파이프 노드를 모두 dependent 로 하는 1개 RBE2 로 통합한다.
    [Fact]
    public void TwoUbolts_SnapToSameStructureNode_ConsolidatedWithStructureIndependent()
    {
        var model = new FeModel();
        // 구조 빔 1(0,0)-2(100,0). U-bolt 3(100,30)·4(100,-30) 모두 (100,0)=node2 로 투영 → 같은 노드 공유.
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 100, 30), N(4, 100, -30)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));
        model.Rigids.Add(Ubolt(21, 4));

        new UboltRbeStage().Execute(MakeCtx(model));

        model.Rigids.Should().ContainSingle();
        var rbe = model.Rigids[0];
        rbe.Remark.Should().Be("UBOLT");
        rbe.IndependentNodeId.Should().Be(2);                       // 구조 노드 = independent
        rbe.DependentNodeIds.Should().BeEquivalentTo([3, 4]);       // 두 파이프 = dependent (누락 없음)
    }

    [Fact]
    public void Ubolt_PreservesCm_WhenSnapped()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 30)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(new RigidElement(20, 3, [], "UBOLT", cm: "123"));

        new UboltRbeStage().Execute(MakeCtx(model));

        model.Rigids.First(r => r.Id == 20).Cm.Should().Be("123");
    }

    [Fact]
    public void Ubolt_Snapped_DiagonalXzOnly_MovesAnchorOntoVerticalSupportLine()
    {
        var model = new FeModel();
        model.Nodes.AddRange([
            N(1,   0, 0, 100),
            N(2, 100, 0, 100),
            N(3, 200, 0, 100),
            N(4, 120, -50, 0),
            N(5, 120,  50, 0)
        ]);
        model.Elements.AddRange([
            E(10, 1, 2, 2, EntityCategory.Pipe),
            E(11, 2, 3, 2, EntityCategory.Pipe),
            E(12, 4, 5, 1, EntityCategory.Structure)
        ]);
        model.Rigids.Add(new RigidElement(20, 2, [], "UBOLT"));

        var ctx = MakeCtx(model, snapDist: 150.0);
        new UboltRbeStage().Execute(ctx);

        var anchor = model.Nodes.First(n => n.Id == 2);
        anchor.Position.X.Should().BeApproximately(120, 0.001);
        anchor.Position.Y.Should().BeApproximately(0, 0.001);
        anchor.Position.Z.Should().BeApproximately(100, 0.001);
        ctx.Diagnostics.Should().Contain(d => d.Code == "UBOLT_ANCHOR_VERTICALIZED");
    }

    [Fact]
    public void Ubolt_Snapped_DifferentY_DoesNotMoveAnchor()
    {
        var model = new FeModel();
        model.Nodes.AddRange([
            N(1,   0, 0, 100),
            N(2, 100, 0, 100),
            N(3, 200, 0, 100),
            N(4, 120,  20, 0),
            N(5, 120, 120, 0)
        ]);
        model.Elements.AddRange([
            E(10, 1, 2, 2, EntityCategory.Pipe),
            E(11, 2, 3, 2, EntityCategory.Pipe),
            E(12, 4, 5, 1, EntityCategory.Structure)
        ]);
        model.Rigids.Add(new RigidElement(20, 2, [], "UBOLT"));

        var ctx = MakeCtx(model, snapDist: 150.0);
        new UboltRbeStage().Execute(ctx);

        model.Nodes.First(n => n.Id == 2).Position.X.Should().BeApproximately(100, 0.001);
        ctx.Diagnostics.Should().NotContain(d => d.Code == "UBOLT_ANCHOR_VERTICALIZED");
    }

    [Fact]
    public void BoxUbolt_IsLeftForDedicatedBoxStage()
    {
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.H, [100, 10, 100, 10], 1));
        model.Sections.Add(new BeamSection(2, BeamSectionKind.Tube, [50, 45], 1));

        model.Nodes.AddRange([
            N(1, -100,  100,   0), N(2, 100,  100,   0),
            N(3, -100, -100,   0), N(4, 100, -100,   0),
            N(5, -100,    0, 100), N(6, 100,    0, 100),
            N(7, -100,    0,-100), N(8, 100,    0,-100),
            N(9,    0,    0,   0), N(10, 100,   0,   0)
        ]);
        model.Elements.AddRange([
            E(11, 1, 2, 1, EntityCategory.Structure),
            E(12, 3, 4, 1, EntityCategory.Structure),
            E(13, 5, 6, 1, EntityCategory.Structure),
            E(14, 7, 8, 1, EntityCategory.Structure),
            E(15, 9, 10, 2, EntityCategory.Pipe)
        ]);
        model.Rigids.Add(new RigidElement(20, 9, [], "UBOLT", cm: "123456", detailRemark: "BOX"));

        new UboltRbeStage().Execute(MakeCtx(model));

        var rbe = model.Rigids.First(r => r.Id == 20);
        rbe.DependentNodeIds.Should().BeEmpty();
        model.Nodes.First(n => n.Id == 9).HasTag(NodeTags.Boundary).Should().BeFalse();
    }

    [Fact]
    public void BoxUbolt_IsPreserved_WhenRegularUboltCleanupRuns()
    {
        var model = new FeModel();
        model.Nodes.AddRange([
            N(1, 0, 0), N(2, 100, 0),
            N(3, 50, 30),
            N(4, 50, 200)
        ]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));
        model.Rigids.Add(new RigidElement(21, 4, [], "UBOLT", cm: "123456", detailRemark: "BOX"));

        new UboltRbeStage().Execute(MakeCtx(model, snapDist: 50.0));

        model.Rigids.Should().ContainSingle(r =>
            r.Id == 21 &&
            r.Remark == "UBOLT" &&
            r.DetailRemark == "BOX" &&
            r.DependentNodeIds.Count == 0);
    }
}
