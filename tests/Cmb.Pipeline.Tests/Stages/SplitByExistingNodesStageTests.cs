using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class SplitByExistingNodesStageTests
{
    private static StageContext MakeCtx(FeModel model)
        => new(model, RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Vector3.UnitZ);

    [Fact]
    public void IntermediateExistingNode_SplitChildren_DoNotReuseOriginalElementId()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 0)]);
        model.Elements.Add(E(10, 1, 2));

        new SplitByExistingNodesStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2);
        model.Elements.Select(e => e.Id).Should().NotContain(10);
        model.Elements.Should().AllSatisfy(e => e.ParentElementId.Should().Be(10));
    }
}
