using System.Text.Json;
using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using FluentAssertions;

namespace Cmb.Core.Tests.Serialization;

public class FeModelJsonSchemaV12Tests
{
    private static FeModel MinimalModel()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(100, 0, 0)));
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        m.Materials.Add(new Material(1, "STEEL", 200000, 0.3, 7850e-9));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        return m;
    }

    [Fact]
    public void ToJson_WithDiagnostics_PopulatesDiagnosticsArray()
    {
        var model = MinimalModel();
        var diags = new DiagnosticDto[]
        {
            new() { Severity = "warning", Code = "FREE_END_NODE", Message = "Node 1 is a free end.", NodeId = 1 },
            new() { Severity = "error",   Code = "SHORT_ELEMENT", Message = "Element 1 is too short.", ElemId = 1 },
        };

        var json = model.ToJson(phase: "C", stageName: "Test", stageDiagnostics: diags);
        var doc  = JsonDocument.Parse(json);

        var arr = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        arr.Should().HaveCount(2);
        arr[0].GetProperty("code").GetString().Should().Be("FREE_END_NODE");
        arr[0].GetProperty("severity").GetString().Should().Be("warning");
        arr[0].GetProperty("message").GetString().Should().Contain("free end");
        arr[0].GetProperty("nodeId").GetInt32().Should().Be(1);
        arr[1].GetProperty("code").GetString().Should().Be("SHORT_ELEMENT");
        arr[1].GetProperty("elemId").GetInt32().Should().Be(1);
    }

    [Fact]
    public void ToJson_SchemaVersionIs1_2()
    {
        var json = MinimalModel().ToJson();
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("meta").GetProperty("schemaVersion").GetString()
            .Should().Be("1.2");
    }

    [Fact]
    public void ToJson_EmptyDiagnostics_DiagnosticsArrayIsEmpty()
    {
        var json = MinimalModel().ToJson();
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void ToJson_ElementHasCorrectLengthMm()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(300, 0, 0)));
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        m.Materials.Add(new Material(1, "S", 200000, 0.3, 7.85e-6));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));

        var doc  = JsonDocument.Parse(m.ToJson());
        var elem = doc.RootElement.GetProperty("elements").EnumerateArray().First();
        elem.GetProperty("lengthMm").GetDouble().Should().BeApproximately(300.0, 0.001);
    }

    [Fact]
    public void ToJson_MetaHasStageIndexAndDurationMs()
    {
        var json = MinimalModel().ToJson(stageIndex: 3, processingDurationMs: 42L);
        var doc  = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("meta");
        meta.GetProperty("stageIndex").GetInt32().Should().Be(3);
        meta.GetProperty("processingDurationMs").GetInt64().Should().Be(42L);
    }
}
