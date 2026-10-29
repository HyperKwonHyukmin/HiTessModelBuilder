using System.Text.Json;
using FluentAssertions;
using static Cmb.Integration.Tests.DeleteRigidAddNodeApplyEditTests;

namespace Cmb.Integration.Tests;

// 노드 이동·부재 추가(2026-10-28 기본 모델 편집). Studio 가 최종 절대좌표·요소 번호·단면을 intent 에 박고 엔진은 그대로 적용한다.
// 샘플 hitess_mini: E2 = N3–N4(N4 자유단), N9 자유단.
public class MoveNodesAddElementApplyEditTests
{
    private static JsonElement BaseModel()
    {
        var sampleDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "hitess_mini", "20260617_082620"));
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(sampleDir, "06_Validation.json"))).RootElement.Clone();
    }

    private static int PropertyOf(JsonElement root, int elementId) =>
        root.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == elementId)
            .GetProperty("propertyId").GetInt32();

    private static int NextElementId(JsonElement root) =>
        new[] { "elements", "rigids", "pointMasses" }
            .SelectMany(k => root.GetProperty(k).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()))
            .Max() + 1;

    [Fact]
    public void MoveNodes_WritesNewCoordinates()
    {
        var result = Run(Intent("moveNodes", new { moves = new[] { new { nodeId = 4, x = 90000.0, y = 4000.5, z = 38000.0 } } }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var n4 = doc.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetInt32() == 4);
        n4.GetProperty("x").GetDouble().Should().BeApproximately(90000.0, 1e-6);
        n4.GetProperty("y").GetDouble().Should().BeApproximately(4000.5, 1e-6);
        n4.GetProperty("z").GetDouble().Should().BeApproximately(38000.0, 1e-6);
    }

    [Fact]
    public void AddElement_WithExistingProperty_AppearsInJsonAndBdf()
    {
        var baseModel = BaseModel();
        int eid = NextElementId(baseModel);
        int pid = PropertyOf(baseModel, 2);
        var result = Run(Intent("addElement", new { elementId = eid, startNode = 4, endNode = 9, propertyId = pid }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var added = doc.RootElement.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == eid);
        added.GetProperty("startNode").GetInt32().Should().Be(4);
        added.GetProperty("endNode").GetInt32().Should().Be(9);
        added.GetProperty("category").GetString().Should().Be("Structure");
        File.ReadAllText(result.EditedBdf).Should().MatchRegex($@"(?m)^CBEAM\s+{eid}\s+{pid}\s+4\s+9");
    }

    [Fact]
    public void AddElement_WithCatalogSection_WritesNewPbeaml()
    {
        var baseModel = BaseModel();
        int eid = NextElementId(baseModel);
        int pid = baseModel.GetProperty("properties").EnumerateArray().Max(p => p.GetProperty("id").GetInt32()) + 1;
        int mid = baseModel.GetProperty("materials")[0].GetProperty("id").GetInt32();
        var result = Run(Intent("addElement", new
        {
            elementId = eid, startNode = 4, endNode = 9, propertyId = pid,
            newProperty = new { id = pid, kind = "L", dims = new[] { 75.0, 75.0, 9.0, 9.0 }, materialId = mid },
        }));
        result.ExitCode.Should().Be(0, result.Log);
        File.ReadAllText(result.EditedBdf).Should().MatchRegex($@"(?m)^PBEAML\s+{pid}\s+{mid}\s+L");
    }

    [Fact]
    public void AddElement_RecordedAfterCentroidRigid_KeepsStudioId()
    {
        // 무게중심 RBE 가 NextUnusedElementId 로 번호를 받기 전에 addElement 가 적용돼야 Studio 번호가 유지된다.
        var baseModel = BaseModel();
        int eid = NextElementId(baseModel);
        int pid = PropertyOf(baseModel, 2);
        var result = Run(
            Intent("addRigid", new { independentNode = (int?)null, dependentNodes = new[] { 27, 28, 30 }, createIndependentAtCentroid = true, cm = "123456" }),
            Intent("addElement", new { elementId = eid, startNode = 4, endNode = 9, propertyId = pid }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        doc.RootElement.GetProperty("elements").EnumerateArray().Should().Contain(e => e.GetProperty("id").GetInt32() == eid);
        doc.RootElement.GetProperty("rigids").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).Should().NotContain(eid);
    }

    [Fact]
    public void InvalidEdits_AreRejected()
    {
        var baseModel = BaseModel();
        int pid = PropertyOf(baseModel, 2);
        Run(Intent("addElement", new { elementId = 2, startNode = 4, endNode = 9, propertyId = pid })).ExitCode.Should().Be(65);
        Run(Intent("addElement", new { elementId = 90001, startNode = 4, endNode = 9, propertyId = 9999 })).ExitCode.Should().Be(65);
        Run(Intent("addElement", new { elementId = 90001, startNode = 4, endNode = 4, propertyId = pid })).ExitCode.Should().Be(65);
        Run(Intent("moveNodes", new { moves = new[] { new { nodeId = 99999, x = 0.0, y = 0.0, z = 0.0 } } })).ExitCode.Should().Be(65);
    }
}
