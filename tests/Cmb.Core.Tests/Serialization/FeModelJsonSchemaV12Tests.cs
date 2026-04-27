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

    [Fact]
    public void ToJson_FreeEndNode_GetsTagFreeEnd()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(100, 0, 0)));
        m.Nodes.Add(new Node(3, new Point3(200, 0, 0)));
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        m.Materials.Add(new Material(1, "S", 200000, 0.3, 7.85e-6));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        m.Elements.Add(new BeamElement(2, 2, 3, 10, EntityCategory.Structure, Vector3.UnitZ));

        var doc   = JsonDocument.Parse(m.ToJson());
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray()
                       .ToDictionary(n => n.GetProperty("id").GetInt32(),
                                     n => n.GetProperty("tags").EnumerateArray()
                                           .Select(t => t.GetString()!).ToList());

        nodes[1].Should().Contain("FreeEnd");   // degree 1
        nodes[2].Should().NotContain("FreeEnd"); // degree 2
        nodes[3].Should().Contain("FreeEnd");   // degree 1
    }

    [Fact]
    public void ToJson_OrphanNode_GetsTagOrphan()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));   // element 없음 → degree 0

        var doc  = JsonDocument.Parse(m.ToJson());
        var tags = doc.RootElement.GetProperty("nodes").EnumerateArray().First()
                      .GetProperty("tags").EnumerateArray()
                      .Select(t => t.GetString()!).ToList();

        tags.Should().Contain("Orphan");
    }

    [Fact]
    public void ToJson_IntersectionNode_GetsTagIntersection()
    {
        var m = new FeModel();
        for (int i = 1; i <= 4; i++)
            m.Nodes.Add(new Node(i, new Point3(i * 100, 0, 0)));
        // Node 2 has NodeTags.Intersection set (as IntersectionStage would do in the real pipeline)
        m.Nodes.First(n => n.Id == 2).AddTag(NodeTags.Intersection);
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        m.Materials.Add(new Material(1, "S", 200000, 0.3, 7.85e-6));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        m.Elements.Add(new BeamElement(2, 2, 3, 10, EntityCategory.Structure, Vector3.UnitZ));
        m.Elements.Add(new BeamElement(3, 2, 4, 10, EntityCategory.Structure, Vector3.UnitZ));

        var doc  = JsonDocument.Parse(m.ToJson());
        var node2Tags = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("id").GetInt32() == 2)
            .GetProperty("tags").EnumerateArray()
            .Select(t => t.GetString()!).ToList();

        node2Tags.Should().Contain("Intersection");
    }

    [Fact]
    public void ToJson_BoundaryNode_NeverGetsFreeEndOrOrphan()
    {
        var m = new FeModel();
        var n = new Node(1, new Point3(0, 0, 0));
        n.AddTag(NodeTags.Boundary);
        m.Nodes.Add(n);

        var doc  = JsonDocument.Parse(m.ToJson());
        var tags = doc.RootElement.GetProperty("nodes").EnumerateArray().First()
                      .GetProperty("tags").EnumerateArray()
                      .Select(t => t.GetString()!).ToList();

        tags.Should().NotContain("FreeEnd");
        tags.Should().NotContain("Orphan");
    }

    [Fact]
    public void ToJson_WithStageDeltas_IncludesDeltas()
    {
        var m = MinimalModel();
        var deltas = new StageDeltasDto
        {
            FromStage       = "NodeEquivalence",
            ToStage         = "Intersection",
            ElemCountBefore = 100,
            ElemCountAfter  = 112,
            NodeCountBefore = 80,
            NodeCountAfter  = 90,
        };

        var doc = JsonDocument.Parse(m.ToJson(stageDeltas: deltas));
        var sd  = doc.RootElement.GetProperty("stageDeltas");

        sd.GetProperty("fromStage").GetString().Should().Be("NodeEquivalence");
        sd.GetProperty("toStage").GetString().Should().Be("Intersection");
        sd.GetProperty("netElementDelta").GetInt32().Should().Be(12);
        sd.GetProperty("netNodeDelta").GetInt32().Should().Be(10);
    }

    [Fact]
    public void ToJson_WithValidationResult_IncludesItInOutput()
    {
        var vr = new ValidationResultDto
        {
            Passed       = true,
            ErrorCount   = 0,
            WarningCount = 2,
            Summary      = "모델이 유효합니다.",
            Checklist    =
            [
                new() { Check = "connectivity", Passed = true, Detail = "단일 연결 그룹" },
            ],
        };

        var doc = JsonDocument.Parse(MinimalModel().ToJson(validationResult: vr));
        var vrd = doc.RootElement.GetProperty("validationResult");

        vrd.GetProperty("passed").GetBoolean().Should().BeTrue();
        vrd.GetProperty("errorCount").GetInt32().Should().Be(0);
        vrd.GetProperty("warningCount").GetInt32().Should().Be(2);
        vrd.GetProperty("checklist").GetArrayLength().Should().Be(1);
        vrd.GetProperty("checklist")[0].GetProperty("check").GetString().Should().Be("connectivity");
    }

    [Fact]
    public void ToJson_WithoutValidationResult_FieldOmitted()
    {
        var doc = JsonDocument.Parse(MinimalModel().ToJson());
        doc.RootElement.TryGetProperty("validationResult", out _).Should().BeFalse();
    }

    [Fact]
    public void ToJson_WithStageHistory_IncludesItInOutput()
    {
        var history = new List<StageHistoryEntryDto>
        {
            new()
            {
                StageIndex           = 1,
                Stage                = "Meshing",
                Timestamp            = "2026-01-01T00:00:00Z",
                ProcessingDurationMs = 150L,
                ElementCount         = 42,
                NodeCount            = 30,
                IssueCount           = new StageHistoryIssueCountDto { Error = 0, Warning = 1, Info = 3 },
            },
            new()
            {
                StageIndex           = 2,
                Stage                = "NodeEquivalence",
                Timestamp            = "2026-01-01T00:00:01Z",
                ProcessingDurationMs = 80L,
                ElementCount         = 42,
                NodeCount            = 28,
                IssueCount           = new StageHistoryIssueCountDto { Error = 0, Warning = 0, Info = 2 },
            },
        };

        var doc = JsonDocument.Parse(MinimalModel().ToJson(stageHistory: history));
        var sh  = doc.RootElement.GetProperty("stageHistory");

        sh.GetArrayLength().Should().Be(2);
        var first = sh[0];
        first.GetProperty("stageIndex").GetInt32().Should().Be(1);
        first.GetProperty("stage").GetString().Should().Be("Meshing");
        first.GetProperty("processingDurationMs").GetInt64().Should().Be(150L);
        first.GetProperty("elementCount").GetInt32().Should().Be(42);
        first.GetProperty("nodeCount").GetInt32().Should().Be(30);
        var ic = first.GetProperty("issueCount");
        ic.GetProperty("error").GetInt32().Should().Be(0);
        ic.GetProperty("warning").GetInt32().Should().Be(1);
        ic.GetProperty("info").GetInt32().Should().Be(3);
    }

    [Fact]
    public void ToJson_WithoutStageHistory_FieldOmitted()
    {
        var doc = JsonDocument.Parse(MinimalModel().ToJson());
        doc.RootElement.TryGetProperty("stageHistory", out _).Should().BeFalse();
    }
}
