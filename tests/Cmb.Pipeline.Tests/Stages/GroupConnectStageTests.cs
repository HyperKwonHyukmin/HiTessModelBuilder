using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class GroupConnectStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, double snapTolMm = 50.0, double nodeMergeMm = 1.0)
        => new(model, new RunOptions(new Tolerances(
                GroupConnectSnapTolMm: snapTolMm,
                NodeMergeMm: nodeMergeMm)),
            NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end, EntityCategory cat = EntityCategory.Structure)
        => new(id, start, end, 1, cat, Up);

    // ── Single group — already connected, no action ────────────────────────

    [Fact]
    public void SingleGroup_ReturnsTrue_NoChange()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 50, 0), N(3, 100, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);

        var result = new GroupConnectStage().Execute(MakeCtx(model));

        result.Should().BeTrue();
        model.Elements.Should().HaveCount(2);
        model.Nodes.Should().HaveCount(3);
    }

    // ── Slave free-end within snap tolerance → snapped ────────────────────

    [Fact]
    public void SlaveGroup_FreeEndWithinSnapTol_RemapsElementToMasterNode()
    {
        var model = new FeModel();
        // Master: 1(0,0) → 2(100,0).  Slave: 3(140,0) → 4(200,0)
        // Projection of node 3 onto master segment: (100,0), dist=40mm < snapTol(50mm)
        // Node 2 is at (100,0) → reuse (dist=0 ≤ NodeMergeMm=1mm)
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 140, 0), N(4, 200, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new GroupConnectStage().Execute(MakeCtx(model));

        var slaveElem = model.Elements.First(e => e.Id == 11);
        slaveElem.StartNodeId.Should().Be(2);
        slaveElem.EndNodeId.Should().Be(4);
    }

    [Fact]
    public void SlaveGroup_Snapped_OldFreeEndNodeRemoved()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 140, 0), N(4, 200, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new GroupConnectStage().Execute(MakeCtx(model));

        // Node 3 was the free-end: now remapped, no longer referenced → removed
        model.Nodes.Should().NotContain(n => n.Id == 3);
        model.Nodes.Should().HaveCount(3);
    }

    [Fact]
    public void SlaveGroup_Snapped_EmitsInfoDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 140, 0), N(4, 200, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(model);
        new GroupConnectStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Info && d.Code == "GROUPS_CONNECTED");
    }

    // ── Slave too far → warning, no snap ──────────────────────────────────

    [Fact]
    public void SlaveGroup_TooFar_EmitsWarningDiagnostic()
    {
        var model = new FeModel();
        // Slave starts at 200mm; closest point on master is (100,0) → dist=100mm > snapTol(50mm)
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 200, 0), N(4, 300, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(model);
        new GroupConnectStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Warning && d.Code == "GROUP_NOT_CONNECTED");
    }

    [Fact]
    public void SlaveGroup_TooFar_ElementUnchanged()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 200, 0), N(4, 300, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new GroupConnectStage().Execute(MakeCtx(model));

        var slaveElem = model.Elements.First(e => e.Id == 11);
        slaveElem.StartNodeId.Should().Be(3);
        slaveElem.EndNodeId.Should().Be(4);
    }

    // ── All-pipe slave group → skipped (handled by UboltRbeStage) ────────

    [Fact]
    public void AllPipeSlaveGroup_Skipped_NoSnap()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 140, 0), N(4, 200, 0)]);
        model.Elements.Add(E(10, 1, 2, EntityCategory.Structure));
        model.Elements.Add(E(11, 3, 4, EntityCategory.Pipe));

        var ctx = MakeCtx(model);
        new GroupConnectStage().Execute(ctx);

        ctx.Diagnostics.Should().NotContain(d => d.Code == "GROUPS_CONNECTED");
        model.Elements.First(e => e.Id == 11).StartNodeId.Should().Be(3);
    }

    // ── Projection mid-segment → creates new node ─────────────────────────

    [Fact]
    public void SlaveGroup_ProjectionFarFromExistingNodes_CreatesNewNodeAtProjection()
    {
        var model = new FeModel();
        // Slave free-end node 3 at (50,30); projects onto master at (50,0), dist=30mm < 50mm
        // No existing node within NodeMergeMm(1mm) of (50,0) → new node created
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 30), N(4, 50, 80)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new GroupConnectStage().Execute(MakeCtx(model));

        model.Nodes.Should().Contain(n =>
            Math.Abs(n.Position.X - 50) < 0.01 && Math.Abs(n.Position.Y) < 0.01);
    }

    // ── Projection coincides with existing master node → reuse ───────────

    [Fact]
    public void SlaveGroup_ProjectionCoincidentWithMasterNode_NodeCountDecreases()
    {
        var model = new FeModel();
        // Projection of node 3(140,0) lands on node 2(100,0); reuse node 2 → node 3 removed
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 140, 0), N(4, 200, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        int before = model.Nodes.Count;
        new GroupConnectStage().Execute(MakeCtx(model, nodeMergeMm: 1.0));

        model.Nodes.Count.Should().BeLessThan(before); // node 3 removed, no new node added
    }

    // ── Empty model ────────────────────────────────────────────────────────

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var result = new GroupConnectStage().Execute(MakeCtx(new FeModel()));
        result.Should().BeTrue();
    }
}
