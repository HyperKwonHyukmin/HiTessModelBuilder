using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class FinalValidationStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x = 0, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end, int propId = 1)
        => new(id, start, end, propId, EntityCategory.Structure, Up);

    private static BeamSection Section(int id, int matId = 1)
        => new(id, BeamSectionKind.H, [400, 200, 13, 21], matId);

    private static Material Steel()
        => new(1, "Steel", 206000, 0.3, 7.85e-9);

    // ── Valid model passes ────────────────────────────────────────────────────

    [Fact]
    public void ValidModel_ReturnsTrue_NoDiagnostics()
    {
        var model = new FeModel();
        var n1 = N(1, 0, 0);   n1.AddTag(NodeTags.Boundary);
        var n3 = N(3, 200, 0); n3.AddTag(NodeTags.Boundary);
        model.Nodes.AddRange([n1, N(2, 100, 0), n3]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        var result = new FinalValidationStage().Execute(ctx);

        result.Should().BeTrue();
        ctx.Diagnostics.Should().BeEmpty();
    }

    // ── Missing property → Error ──────────────────────────────────────────────

    [Fact]
    public void MissingProperty_EmitsError()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0)]);
        model.Elements.Add(E(10, 1, 2, propId: 99)); // propId 99 doesn't exist
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Error && d.Code == "MISSING_PROPERTY");
    }

    [Fact]
    public void MissingProperty_ReturnsFalse()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0)]);
        model.Elements.Add(E(10, 1, 2, propId: 99));
        model.Materials.Add(Steel());

        var result = new FinalValidationStage().Execute(MakeCtx(model));

        result.Should().BeFalse();
    }

    // ── Missing material → Error ──────────────────────────────────────────────

    [Fact]
    public void MissingMaterial_EmitsError()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0)]);
        model.Elements.Add(E(10, 1, 2));
        model.Sections.Add(new BeamSection(1, BeamSectionKind.H, [400, 200, 13, 21], 99)); // matId 99 missing

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Error && d.Code == "MISSING_MATERIAL");
    }

    // ── Orphan node → Warning ─────────────────────────────────────────────────

    [Fact]
    public void OrphanNode_EmitsWarning()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 999, 999)]); // node 3 unreferenced
        model.Elements.Add(E(10, 1, 2));
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Code == "ORPHAN_NODE" && d.NodeId == 3);
    }

    [Fact]
    public void OrphanNode_PipelineStillSucceeds()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 999, 999)]);
        model.Elements.Add(E(10, 1, 2));
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var result = new FinalValidationStage().Execute(MakeCtx(model));

        result.Should().BeTrue(); // Warning only → still pass
    }

    // ── Free-end node → Warning ───────────────────────────────────────────────

    [Fact]
    public void FreeEndNode_EmitsWarning()
    {
        // node 1 connects only to elem 10 (start) → free end
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0)]);
        model.Elements.Add(E(10, 1, 2));
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Where(d => d.Code == "FREE_END_NODE").Should().HaveCount(2); // both ends are free
    }

    [Fact]
    public void FreeEndNode_TaggedBoundary_NotReported()
    {
        var model = new FeModel();
        var n1 = N(1, 0, 0);
        n1.AddTag(NodeTags.Boundary);
        var n2 = N(2, 100, 0);
        n2.AddTag(NodeTags.Boundary);
        model.Nodes.AddRange([n1, n2]);
        model.Elements.Add(E(10, 1, 2));
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Should().NotContain(d => d.Code == "FREE_END_NODE");
    }

    [Fact]
    public void FreeEndNode_MiddleNodeNotFlagged()
    {
        // chain: 1-2-3, node 2 is interior (2 connections) → not free end
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 200, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Where(d => d.Code == "FREE_END_NODE")
            .Should().NotContain(d => d.NodeId == 2);
    }

    // ── RBE-connected node not flagged as free end ────────────────────────────

    [Fact]
    public void FreeEndNode_ConnectedViaRbeToo_NotReported()
    {
        // Pipe element E10: N1→N2.  N2 is also the independent node of RBE1 (U-bolt snapped)
        // N2 has element-degree=1 but RBE makes effective degree=2 → should NOT be flagged
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 100, 50)]);
        model.Elements.Add(new BeamElement(10, 1, 2, 1, EntityCategory.Pipe, Up));
        model.Rigids.Add(new RigidElement(20, 2, [3], "UBOLT", null));
        model.Sections.Add(Section(1));
        model.Materials.Add(Steel());

        var ctx = MakeCtx(model);
        new FinalValidationStage().Execute(ctx);

        ctx.Diagnostics.Where(d => d.Code == "FREE_END_NODE")
            .Should().NotContain(d => d.NodeId == 2);
    }
}
