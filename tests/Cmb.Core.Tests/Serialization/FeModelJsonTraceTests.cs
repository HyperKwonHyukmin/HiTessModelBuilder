using System.Text.Json;
using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using FluentAssertions;

namespace Cmb.Core.Tests.Serialization;

public class FeModelJsonTraceTests
{
    private static FeModel MinimalModel()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(10, BeamSectionKind.H, [368, 32, 200, 10], 1));
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(1000, 0, 0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ,
            sourceName: "=SRC_001"));
        return m;
    }

    // ── sourceName round-trip ─────────────────────────────────────────────────

    [Fact]
    public void ToJson_WithSourceName_IncludesSourceNameField()
    {
        var model = MinimalModel();
        var json  = model.ToJson();

        json.Should().Contain("\"sourceName\"");
        json.Should().Contain("=SRC_001");
    }

    [Fact]
    public void RoundTrip_SourceName_Preserved()
    {
        var model    = MinimalModel();
        var restored = FeModelJson.FromJson(model.ToJson());

        restored.Elements[0].SourceName.Should().Be("=SRC_001");
    }

    [Fact]
    public void ToJson_NullSourceName_FieldOmitted()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(10, BeamSectionKind.H, [368, 32, 200, 10], 1));
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(1000, 0, 0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));

        var json = m.ToJson();
        json.Should().NotContain("sourceName");
    }

    // ── trace array ───────────────────────────────────────────────────────────

    [Fact]
    public void ToJson_WithTraceEvents_IncludesTraceArray()
    {
        var model = MinimalModel();
        model.AddTrace(TraceAction.ElementCreated, "initial", elementId: 1, note: "=SRC_001");

        var json = model.ToJson();
        json.Should().Contain("\"trace\"");
        json.Should().Contain("ElementCreated");
        json.Should().Contain("initial");
    }

    [Fact]
    public void ToJson_EmptyTraceLog_TraceKeyOmitted()
    {
        var json = MinimalModel().ToJson();
        // "trace" 키는 _keys 설명에 있으므로 최상위 배열 필드 absent 여부를 JSON 파싱으로 검증
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("trace", out _).Should().BeFalse("빈 TraceLog 는 직렬화 생략");
    }

    [Fact]
    public void RoundTrip_TraceEvents_Preserved()
    {
        var model = MinimalModel();
        model.AddTrace(TraceAction.ElementSplit, "IntersectionStage",
            elementId: 5, relatedElementId: 1, note: "split at node 47");
        model.AddTrace(TraceAction.NodeMerged, "NodeEquivalenceStage",
            nodeId: 12, relatedNodeId: 8, note: "merged within 1.0mm");

        var restored = FeModelJson.FromJson(model.ToJson());

        restored.TraceLog.Should().HaveCount(2);
        restored.TraceLog[0].Action.Should().Be(TraceAction.ElementSplit);
        restored.TraceLog[0].StageName.Should().Be("IntersectionStage");
        restored.TraceLog[1].Action.Should().Be(TraceAction.NodeMerged);
        restored.TraceLog[1].RelatedNodeId.Should().Be(8);
    }

    // ── schema version ────────────────────────────────────────────────────────

    [Fact]
    public void ToJson_SchemaVersion_Is1_1()
    {
        var json = MinimalModel().ToJson();
        json.Should().Contain("\"schemaVersion\": \"1.1\"");
    }
}
