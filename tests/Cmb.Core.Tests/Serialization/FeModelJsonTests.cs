using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using FluentAssertions;

namespace Cmb.Core.Tests.Serialization;

public class FeModelJsonTests
{
    private static FeModel BuildSampleModel()
    {
        var model = new FeModel();

        model.Materials.Add(Material.DefaultSteel);
        model.Sections.Add(new BeamSection(1, BeamSectionKind.H, [400, 200, 10, 16], 1));

        var n1 = new Node(1, new Point3(0, 0, 0), NodeTags.Weld);
        var n2 = new Node(2, new Point3(1000, 0, 0));
        model.Nodes.Add(n1);
        model.Nodes.Add(n2);

        model.Elements.Add(new BeamElement(1, 1, 2, 1, EntityCategory.Structure, Vector3.UnitZ));
        model.Rigids.Add(new RigidElement(1, 1, [2, 3], "UBOLT"));
        model.PointMasses.Add(new PointMass(1, 1, 500.0));

        return model;
    }

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var model = BuildSampleModel();
        var json = model.ToJson("B", "initial");
        json.Should().Contain("\"nodes\"");
        json.Should().Contain("\"elements\"");
        json.Should().Contain("\"properties\"");
        json.Should().Contain("\"materials\"");
        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"1.1\"");
    }

    [Fact]
    public void RoundTrip_Nodes_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Nodes.Count.Should().Be(original.Nodes.Count);
        restored.Nodes[0].Id.Should().Be(1);
        restored.Nodes[0].Position.X.Should().BeApproximately(0, 1e-10);
        restored.Nodes[0].Position.Y.Should().BeApproximately(0, 1e-10);
        restored.Nodes[1].Position.X.Should().BeApproximately(1000, 1e-10);
    }

    [Fact]
    public void RoundTrip_NodeTags_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Nodes[0].HasTag(NodeTags.Weld).Should().BeTrue();
        restored.Nodes[1].HasTag(NodeTags.Weld).Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_Elements_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Elements.Count.Should().Be(1);
        restored.Elements[0].StartNodeId.Should().Be(1);
        restored.Elements[0].EndNodeId.Should().Be(2);
        restored.Elements[0].Category.Should().Be(EntityCategory.Structure);
        restored.Elements[0].Orientation.Z.Should().BeApproximately(1.0, 1e-10);
    }

    [Fact]
    public void RoundTrip_Rigids_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Rigids.Count.Should().Be(1);
        restored.Rigids[0].IndependentNodeId.Should().Be(1);
        restored.Rigids[0].DependentNodeIds.Should().BeEquivalentTo([2, 3]);
        restored.Rigids[0].Remark.Should().Be("UBOLT");
    }

    [Fact]
    public void RoundTrip_Materials_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Materials.Count.Should().Be(1);
        restored.Materials[0].E.Should().BeApproximately(206000.0, 1e-6);
        restored.Materials[0].Nu.Should().BeApproximately(0.3, 1e-10);
        restored.Materials[0].Rho.Should().BeApproximately(7.85e-9, 1e-20);
    }

    [Fact]
    public void RoundTrip_Sections_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Sections.Count.Should().Be(1);
        restored.Sections[0].Kind.Should().Be(BeamSectionKind.H);
        restored.Sections[0].Dims.Should().BeEquivalentTo([400.0, 200.0, 10.0, 16.0]);
    }

    [Fact]
    public void RoundTrip_PointMasses_Preserved()
    {
        var original = BuildSampleModel();
        var json = original.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.PointMasses.Count.Should().Be(1);
        restored.PointMasses[0].NodeId.Should().Be(1);
        restored.PointMasses[0].Mass.Should().BeApproximately(500.0, 1e-10);
    }

    [Fact]
    public void RoundTrip_EmptyModel_Works()
    {
        var empty = new FeModel();
        var json = empty.ToJson("A", "parse");
        var restored = FeModelJson.FromJson(json);

        restored.Nodes.Should().BeEmpty();
        restored.Elements.Should().BeEmpty();
        restored.Sections.Should().BeEmpty();
        restored.Materials.Should().BeEmpty();
    }

    [Fact]
    public void ToJson_MetaFields_Populated()
    {
        var model = new FeModel();
        var json = model.ToJson("C", "IntersectionStage");
        json.Should().Contain("\"phase\"");
        json.Should().Contain("\"C\"");
        json.Should().Contain("\"IntersectionStage\"");
        json.Should().Contain("\"unit\"");
        json.Should().Contain("\"mm\"");
    }

    [Fact]
    public void NodeTags_MultipleFlags_RoundTrip()
    {
        var model = new FeModel();
        model.Nodes.Add(new Node(1, Point3.Origin, NodeTags.Weld | NodeTags.Intersection | NodeTags.Boundary));

        var json = model.ToJson();
        var restored = FeModelJson.FromJson(json);

        restored.Nodes[0].HasTag(NodeTags.Weld).Should().BeTrue();
        restored.Nodes[0].HasTag(NodeTags.Intersection).Should().BeTrue();
        restored.Nodes[0].HasTag(NodeTags.Boundary).Should().BeTrue();
        restored.Nodes[0].HasTag(NodeTags.Merged).Should().BeFalse();
    }
}
