using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class MeshingStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, RunOptions? options = null)
        => new(model, options ?? RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x) => new(id, new Point3(x, 0, 0));

    private static BeamElement E(int id, int start, int end, EntityCategory cat = EntityCategory.Structure)
        => new(id, start, end, 1, cat, Up);

    // ── Structure meshing ─────────────────────────────────────────────────────

    [Fact]
    public void StructureElement_ExceedsMaxLength_IsSplit()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 5000)); // 5000 mm > 2000 * 1.1
        model.Elements.Add(E(10, 1, 2));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(3); // ceil(5000/2000) = 3 segments
    }

    [Fact]
    public void StructureElement_BelowMaxLength_NotSplit()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 1500)); // 1500 mm < 2000 * 1.1 = 2200
        model.Elements.Add(E(10, 1, 2));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(1);
    }

    [Fact]
    public void StructureElement_Split_OriginalNodePreserved()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 4001)); // just over 2*1.1*2000 → 2 segments
        model.Elements.Add(E(10, 1, 2));

        int nodeCountBefore = model.Nodes.Count;
        new MeshingStage().Execute(MakeCtx(model));

        model.Nodes.Should().Contain(n => n.Id == 1);
        model.Nodes.Should().Contain(n => n.Id == 2);
        model.Nodes.Count.Should().BeGreaterThan(nodeCountBefore); // intermediate node added
    }

    // ── Pipe meshing ──────────────────────────────────────────────────────────

    [Fact]
    public void PipeElement_UsePipeThreshold()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 1500)); // 1500 mm > 1000 * 1.1 = 1100 → split for Pipe
        model.Elements.Add(E(10, 1, 2, EntityCategory.Pipe));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2); // ceil(1500/1000) = 2 segments
    }

    [Fact]
    public void PipeElement_BelowPipeThreshold_NotSplit()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 900)); // 900 < 1000 * 1.1 = 1100
        model.Elements.Add(E(10, 1, 2, EntityCategory.Pipe));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(1);
    }

    // ── Equipment skip ────────────────────────────────────────────────────────

    [Fact]
    public void EquipmentElement_NeverSplit()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 9999));
        model.Elements.Add(E(10, 1, 2, EntityCategory.Equipment));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(1);
    }

    // ── Parentage and traceability ────────────────────────────────────────────

    [Fact]
    public void SplitElements_HaveParentElementId()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 5000));
        model.Elements.Add(E(10, 1, 2));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().AllSatisfy(e => e.ParentElementId.Should().Be(10));
    }

    [Fact]
    public void SplitElements_InheritSourceName()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 5000));
        model.Elements.Add(new BeamElement(10, 1, 2, 1, EntityCategory.Structure, Up, sourceName: "BEAM_42"));

        new MeshingStage().Execute(MakeCtx(model));

        model.Elements.Should().AllSatisfy(e => e.SourceName.Should().Be("BEAM_42"));
    }

    [Fact]
    public void SplitElements_EmitsDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 5000));
        model.Elements.Add(E(10, 1, 2));

        var ctx = MakeCtx(model);
        new MeshingStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d => d.Code == "MESHED");
    }

    // ── ID collision safety ───────────────────────────────────────────────────

    [Fact]
    public void SplitElements_NewIdsDoNotCollideWithRigids()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 5000));
        model.Elements.Add(E(10, 1, 2));
        model.Rigids.Add(new RigidElement(20, 1, [2])); // rigid uses ID 20

        new MeshingStage().Execute(MakeCtx(model));

        var allElemIds = model.Elements.Select(e => e.Id).ToList();
        var allRigidIds = model.Rigids.Select(r => r.Id).ToList();
        allElemIds.Should().NotIntersectWith(allRigidIds);
    }

    // ── Custom threshold ──────────────────────────────────────────────────────

    [Fact]
    public void CustomMeshSize_Respected()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0));
        model.Nodes.Add(N(2, 600)); // 600 mm
        model.Elements.Add(E(10, 1, 2));

        // threshold = 300 mm → ceil(600/300) = 2 segments, and 600 > 300*1.1=330
        var opts = new RunOptions(new Tolerances(MeshingMaxLengthStructure: 300.0));
        new MeshingStage().Execute(MakeCtx(model, opts));

        model.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var result = new MeshingStage().Execute(MakeCtx(new FeModel()));
        result.Should().BeTrue();
    }
}
