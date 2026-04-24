using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class NodeEquivalenceStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, double nodeMergeMm = 1.0)
        => new(model, new RunOptions(new Tolerances(NodeMergeMm: nodeMergeMm)), NullLogger.Instance);

    private static Node N(int id, double x, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Up);

    // ── Basic merge ───────────────────────────────────────────────────────────

    [Fact]
    public void TwoNodes_WithinTolerance_Merged()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0.5)); // 0.5 mm apart — within default 1 mm
        model.Elements.Add(E(10, 1, 2));

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        // One node removed
        model.Nodes.Should().HaveCount(1);
        model.Nodes[0].Id.Should().Be(1); // min ID kept
    }

    [Fact]
    public void TwoNodes_OutsideTolerance_NotMerged()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 2.0)); // 2 mm — outside 1 mm tolerance
        model.Elements.Add(E(10, 1, 2));

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        model.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public void MergedNode_MinIdKept()
    {
        var model = new FeModel();
        model.Nodes.Add(N(5, 0));
        model.Nodes.Add(N(3, 0.3)); // 3 < 5 → node 3 should be kept
        model.Elements.Add(E(10, 3, 5));

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        model.Nodes.Should().HaveCount(1);
        model.Nodes[0].Id.Should().Be(3);
    }

    // ── Element remapping ─────────────────────────────────────────────────────

    [Fact]
    public void AfterMerge_ElementReferencesUpdated()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0.5));  // merged into 1
        model.Nodes.Add(N(3, 1000));
        model.Elements.Add(E(10, 1, 3));
        model.Elements.Add(E(11, 2, 3)); // node 2 → 1

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        // All elements should reference node 1, not 2
        model.Elements.Should().NotContain(e => e.StartNodeId == 2 || e.EndNodeId == 2);
    }

    [Fact]
    public void DegenerateElement_RemovedAfterMerge()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0.5));  // merge into 1
        model.Elements.Add(E(10, 1, 2)); // becomes 1→1 → degenerate

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        model.Elements.Should().BeEmpty();
    }

    [Fact]
    public void DegenerateElement_EmitsTrace()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0.5));
        model.Elements.Add(E(10, 1, 2));

        var ctx = MakeCtx(model);
        new NodeEquivalenceStage().Execute(ctx);

        model.TraceLog.Should().Contain(t =>
            t.Action == TraceAction.ElementRemoved && t.ElementId == 10);
    }

    // ── Rigid remapping ───────────────────────────────────────────────────────

    [Fact]
    public void RigidElement_DependentNodes_Remapped()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0));    // same position → merged into 1
        model.Nodes.Add(N(3, 100));  // independent
        model.Nodes.Add(N(4, 200));  // anchor element node
        model.Elements.Add(E(10, 3, 4));
        model.Rigids.Add(new RigidElement(20, 3, [2]));  // dependent = 2

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        var rigid = model.Rigids[0];
        rigid.DependentNodeIds.Should().Contain(1).And.NotContain(2);
    }

    [Fact]
    public void RigidElement_DependentEqualsIndependent_AfterMerge_RemovedFromDeps()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0));    // merged into 1
        model.Nodes.Add(N(3, 100));
        model.Nodes.Add(N(4, 200));
        model.Elements.Add(E(10, 3, 4));
        // independent = 1, dependent = 2 → after merge both = 1 → dep removed
        model.Rigids.Add(new RigidElement(20, 1, [2]));

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        var rigid = model.Rigids[0];
        rigid.DependentNodeIds.Should().NotContain(1);
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    [Fact]
    public void MergeOccurs_EmitsDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0.5));
        model.Elements.Add(E(10, 1, 2));

        var ctx = MakeCtx(model);
        new NodeEquivalenceStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d => d.Code == "NODES_MERGED");
    }

    [Fact]
    public void NoMerge_NoDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 100));
        model.Elements.Add(E(10, 1, 2));

        var ctx = MakeCtx(model);
        new NodeEquivalenceStage().Execute(ctx);

        ctx.Diagnostics.Should().NotContain(d => d.Code == "NODES_MERGED");
    }

    // ── Chain merge (A≈B, B≈C → all same group) ──────────────────────────────

    [Fact]
    public void ChainMerge_ThreeNearbyNodes_AllMerged()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 0.4));  // within 1 mm of 1
        model.Nodes.Add(N(3, 0.7));  // within 1 mm of 1 and 2
        model.Nodes.Add(N(4, 5000));
        model.Elements.Add(E(10, 1, 4));
        model.Elements.Add(E(11, 2, 4));
        model.Elements.Add(E(12, 3, 4));

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        model.Nodes.Where(n => n.Id != 4).Should().HaveCount(1);
    }

    // ── 3D distance check ─────────────────────────────────────────────────────

    [Fact]
    public void TwoNodes_SameX_DifferentY_ExceedsTol_NotMerged()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0));
        model.Nodes.Add(N(2, 0, 5)); // same X but Y=5 → dist=5 > 1 mm
        model.Elements.Add(E(10, 1, 2));

        new NodeEquivalenceStage().Execute(MakeCtx(model));

        model.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var result = new NodeEquivalenceStage().Execute(MakeCtx(new FeModel()));
        result.Should().BeTrue();
    }
}
