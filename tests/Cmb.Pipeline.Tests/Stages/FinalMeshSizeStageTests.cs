using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class FinalMeshSizeStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x) => new(id, new Point3(x, 0, 0));

    private static BeamElement E(int id, int start, int end, EntityCategory cat)
        => new(id, start, end, 1, cat, Up, sourceName: $"E{id}");

    [Fact]
    public void Structure_LengthEqualToMeshSize_IsSplitUntilSegmentsAreBelowLimit()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000)]);
        model.Elements.Add(E(10, 1, 2, EntityCategory.Structure));

        var ctx = MakeCtx(model);
        new FinalMeshSizeStage(500.0, null).Execute(ctx);

        model.Elements.Should().HaveCount(3);
        var lengths = model.Elements
            .Select(e => model.FindNode(e.StartNodeId)!.Position.DistanceTo(
                model.FindNode(e.EndNodeId)!.Position))
            .ToList();
        lengths.Should().OnlyContain(length => length < 500.0);
        ctx.Diagnostics.Should().Contain(d => d.Code == "FINAL_MESH_SIZE_APPLIED");
    }

    [Fact]
    public void Pipe_UsesPipeLimit()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1200)]);
        model.Elements.Add(E(10, 1, 2, EntityCategory.Pipe));

        new FinalMeshSizeStage(null, 500.0).Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(3);
    }

    [Fact]
    public void Equipment_IsNotSplit()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 5000)]);
        model.Elements.Add(E(10, 1, 2, EntityCategory.Equipment));

        new FinalMeshSizeStage(500.0, 500.0).Execute(MakeCtx(model));

        model.Elements.Should().ContainSingle(e => e.Id == 10);
    }

    [Fact]
    public void Disabled_NoOp()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 5000)]);
        model.Elements.Add(E(10, 1, 2, EntityCategory.Structure));

        new FinalMeshSizeStage(null, null).Execute(MakeCtx(model));

        model.Elements.Should().ContainSingle(e => e.Id == 10);
    }
}
