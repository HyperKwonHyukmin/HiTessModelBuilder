using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class EmptyRigidExclusionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyRigid_IsExcludedWithRecord_AndLeavesNodeBeamMassAndBoundaryUntouched(bool trace)
    {
        var model = new FeModel();
        var anchor = new Node(1293, new Point3(144000, -16746, 32240));
        model.Nodes.AddRange([anchor, new Node(1294, new Point3(144100, -16746, 32240))]);
        model.Elements.Add(new BeamElement(3971, 1293, 1294, 1, EntityCategory.Pipe, Vector3.UnitZ));
        model.Sections.Add(new BeamSection(1, BeamSectionKind.Tube, [203.2, 198.42], 1));
        model.Materials.Add(new Material(1, "Steel", 206000, 0.3, 7.85e-9));
        var mass = new PointMass(9000, 1293, 0.02, "retained mass");
        model.PointMasses.Add(mass);
        model.Rigids.Add(new RigidElement(1022, 1293, [], "UBOLT", "=21945/82982", "12", "BOX"));
        var tags = anchor.Tags;
        var ctx = new StageContext(model, RunOptions.Default with { RecordTrace = trace }, NullLogger.Instance);

        new FinalValidationStage().Execute(ctx).Should().BeTrue();

        model.Rigids.Should().BeEmpty();
        model.Nodes.Should().HaveCount(2);
        model.Elements.Should().ContainSingle(e => e.Id == 3971);
        model.PointMasses.Should().ContainSingle().Which.Should().BeSameAs(mass);
        anchor.Tags.Should().Be(tags);
        anchor.Position.Should().Be(new Point3(144000, -16746, 32240));
        ctx.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        var record = ctx.Diagnostics.Should().ContainSingle(d => d.Code == "EMPTY_RIGID_EXCLUDED").Subject;
        record.ElementId.Should().Be(1022);
        record.NodeId.Should().Be(1293);
        record.Message.Should().Contain("=21945/82982").And.Contain("144000").And.Contain("-16746").And.Contain("32240");
        if (trace) model.TraceLog.Should().ContainSingle(t => t.Action == TraceAction.ElementRemoved && t.ElementId == 1022);
    }

    [Fact]
    public void ConnectedRigid_IsNotExcluded()
    {
        var model = new FeModel();
        model.Nodes.AddRange([new Node(1, new Point3(0, 0, 0)), new Node(2, new Point3(100, 0, 0))]);
        var rigid = new RigidElement(10, 1, [2], "UBOLT", "connected");
        model.Rigids.Add(rigid);
        var ctx = new StageContext(model, RunOptions.Default, NullLogger.Instance);
        new FinalValidationStage().Execute(ctx).Should().BeTrue();
        model.Rigids.Should().ContainSingle().Which.Should().BeSameAs(rigid);
        ctx.Diagnostics.Should().NotContain(d => d.Code == "EMPTY_RIGID_EXCLUDED");
    }
}
