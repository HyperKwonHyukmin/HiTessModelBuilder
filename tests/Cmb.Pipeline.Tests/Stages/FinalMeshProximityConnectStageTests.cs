using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class FinalMeshProximityConnectStageTests
{
    private static StageContext Ctx(FeModel model, Tolerances? tolerances = null)
        => new(model, new RunOptions(tolerances ?? new Tolerances()), NullLogger.Instance);

    [Fact]
    public void ConnectsFinalMeshStructureNodeToNearbyInteriorStructureElement()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, 0, 0, 7), N(2, 0, 100, 7), N(3, 0, 200, 7), N(4, 0, 300, 7),
            N(10, -80, 100, 0), N(11, 80, 100, 0),
            N(12, -80, 200, 0), N(13, 80, 200, 0),
        ]);
        model.Elements.AddRange([
            E(100, 1, 2, EntityCategory.Structure, 1),
            E(101, 2, 3, EntityCategory.Structure, 1),
            E(102, 3, 4, EntityCategory.Structure, 1),
            E(200, 10, 11, EntityCategory.Structure, 2),
            E(201, 12, 13, EntityCategory.Structure, 2),
        ]);
        model.AddTrace(TraceAction.NodeCreated, "FinalMeshSize", nodeId: 2, relatedElementId: 99);
        model.AddTrace(TraceAction.NodeCreated, "FinalMeshSize", nodeId: 3, relatedElementId: 99);

        var ctx = Ctx(model);
        new FinalMeshProximityConnectStage().Execute(ctx);

        model.Rigids.Should().HaveCount(2);
        model.Rigids.Should().OnlyContain(r => r.Remark == "FINAL_MESH_PROXIMITY");
        model.Rigids.SelectMany(r => r.DependentNodeIds).Should().BeEquivalentTo([2, 3]);
        ctx.Diagnostics.Should().Contain(d => d.Code == "FINAL_MESH_PROXIMITY_RBE_CREATED");

        new SplitByExistingNodesStage().Execute(ctx);
        model.Elements.Should().NotContain(e => e.Id == 200);
        model.Elements.Should().NotContain(e => e.Id == 201);
    }

    [Fact]
    public void DoesNotConnectToPipeTarget()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, 0, 0, 7), N(2, 0, 100, 7), N(3, 0, 200, 7),
            N(10, -80, 100, 0), N(11, 80, 100, 0),
        ]);
        model.Elements.AddRange([
            E(100, 1, 2, EntityCategory.Structure, 1),
            E(101, 2, 3, EntityCategory.Structure, 1),
            E(200, 10, 11, EntityCategory.Pipe, 2),
        ]);
        model.AddTrace(TraceAction.NodeCreated, "FinalMeshSize", nodeId: 2);

        new FinalMeshProximityConnectStage().Execute(Ctx(model));

        model.Rigids.Should().BeEmpty();
    }

    [Fact]
    public void DoesNotConnectParallelCandidate()
    {
        var model = BaseModel();
        model.Nodes.AddRange([
            N(1, 0, 0, 7), N(2, 0, 100, 7), N(3, 0, 200, 7),
            N(10, 7, 50, 0), N(11, 7, 150, 0),
        ]);
        model.Elements.AddRange([
            E(100, 1, 2, EntityCategory.Structure, 1),
            E(101, 2, 3, EntityCategory.Structure, 1),
            E(200, 10, 11, EntityCategory.Structure, 2),
        ]);
        model.AddTrace(TraceAction.NodeCreated, "FinalMeshSize", nodeId: 2);

        new FinalMeshProximityConnectStage().Execute(Ctx(model));

        model.Rigids.Should().BeEmpty();
    }

    [Fact]
    public void DoesNotConnectLargeSourceComponent()
    {
        var model = BaseModel();
        for (int i = 1; i <= 25; i++)
            model.Nodes.Add(N(i, 0, i * 100, 7));
        model.Nodes.AddRange([N(100, -80, 500, 0), N(101, 80, 500, 0)]);

        for (int i = 1; i < 25; i++)
            model.Elements.Add(E(100 + i, i, i + 1, EntityCategory.Structure, 1));
        model.Elements.Add(E(300, 100, 101, EntityCategory.Structure, 2));
        model.AddTrace(TraceAction.NodeCreated, "FinalMeshSize", nodeId: 5);

        new FinalMeshProximityConnectStage().Execute(Ctx(model));

        model.Rigids.Should().BeEmpty();
    }

    private static FeModel BaseModel()
    {
        var model = new FeModel();
        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.L, [75, 75, 6, 6], 1));
        model.Sections.Add(new BeamSection(2, BeamSectionKind.L, [125, 75, 7, 7], 1));
        return model;
    }

    private static Node N(int id, double x, double y, double z)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int a, int b, EntityCategory category, int property)
        => new(id, a, b, property, category, Vector3.UnitZ);
}
