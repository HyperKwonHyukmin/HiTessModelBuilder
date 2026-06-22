using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class FinalTopologyHealingStageTests
{
    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Vector3.UnitZ);

    [Fact]
    public void ExistingInlineNodeNearEndpoint_MergesEndpointInsteadOfCreatingShortChild()
    {
        var model = new FeModel();
        model.Nodes.AddRange(
        [
            N(1, 0, 0),
            N(2, 100, 0),
            N(3, 2.5, 0),
            N(4, 2.5, 50),
            N(5, 0, -50),
        ]);
        model.Elements.AddRange(
        [
            E(10, 1, 2),
            E(11, 3, 4),
            E(12, 5, 1),
        ]);

        new FinalTopologyHealingStage().Execute(MakeCtx(model));

        model.Nodes.Select(n => n.Id).Should().NotContain(1);
        model.Elements.Should().Contain(e => e.StartNodeId == 3 && e.EndNodeId == 2);
        model.Elements.Should().Contain(e => e.StartNodeId == 5 && e.EndNodeId == 3);
        ConnectivityAnalyzer.Analyze(model).GroupCount.Should().Be(1);
    }

    [Fact]
    public void ExistingInlineNodeAwayFromEndpoint_IsSplitByExistingNodes()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 0)]);
        model.Elements.Add(E(10, 1, 2));

        new FinalTopologyHealingStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2);
        model.Elements.Should().Contain(e => e.StartNodeId == 1 && e.EndNodeId == 3);
        model.Elements.Should().Contain(e => e.StartNodeId == 3 && e.EndNodeId == 2);
    }

    [Fact]
    public void ReciprocalEndpointMerge_KeepsOneNodeAndRemapsAllReferences()
    {
        var model = new FeModel();
        model.Nodes.AddRange(
        [
            N(13, 0, 0),
            N(28, 2, 0),
            N(100, 100, 0),
            N(101, -98, 0),
            N(102, 2, 50),
        ]);
        model.Elements.AddRange(
        [
            E(18, 13, 100),
            E(19, 28, 101),
            E(20, 28, 102),
        ]);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.H, [100, 50, 6, 8], 1));
        model.Materials.Add(new Material(1, "Steel", 206000, 0.3, 7.85e-9));
        model.Rigids.Add(new RigidElement(3347, 13, [28], "TEST"));

        new FinalTopologyHealingStage().Execute(MakeCtx(model));
        var result = new FinalValidationStage().Execute(MakeCtx(model));

        result.Should().BeTrue();
        model.Nodes.Select(n => n.Id).Should().Contain(13);
        model.Nodes.Select(n => n.Id).Should().NotContain(28);
        model.Elements.Should().OnlyContain(e => e.StartNodeId != 28 && e.EndNodeId != 28);
        model.Rigids.Should().NotContain(r =>
            r.IndependentNodeId == 28 || r.DependentNodeIds.Contains(28));
    }
}
