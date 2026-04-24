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
    public void Ubolt_TooFar_RbeUnchanged_NoDependents()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 200)]);
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(Ubolt(20, 3));

        new UboltRbeStage().Execute(MakeCtx(model, snapDist: 50.0));

        var rbe = model.Rigids.First(r => r.Id == 20);
        rbe.DependentNodeIds.Should().BeEmpty();
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
}
