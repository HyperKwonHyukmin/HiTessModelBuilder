using System.Text.Json;
using FluentAssertions;
using static Cmb.Integration.Tests.DeleteRigidAddNodeApplyEditTests;

namespace Cmb.Integration.Tests;

// 질량 추가(addMasses)·질량 값 수정(changeMasses), 2026-10-29 기본 모델 편집.
// 샘플 hitess_mini: 원본 CONM2 = M14(N22, 0.0143 t)·M16(N26, 0.00172 t), 요소 번호 최대 28.
// E2 = N3-N4, E5 = N9-N10(연결 그룹 {9,10} 단독).
public class MassEditApplyEditTests
{
    private static JsonElement BaseModel()
    {
        var sampleDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "hitess_mini", "20260617_082620"));
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(sampleDir, "06_Validation.json"))).RootElement.Clone();
    }

    private static int NextElementId(JsonElement root) =>
        new[] { "elements", "rigids", "pointMasses" }
            .SelectMany(k => root.GetProperty(k).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()))
            .Max() + 1;

    private static JsonElement? MassOf(JsonElement root, int massId)
    {
        foreach (var pm in root.GetProperty("pointMasses").EnumerateArray())
            if (pm.GetProperty("id").GetInt32() == massId) return pm;
        return null;
    }

    private static int MaxNodeId(JsonElement root) =>
        root.GetProperty("nodes").EnumerateArray().Max(n => n.GetProperty("id").GetInt32());

    private static object Mass(int massId, int nodeId, double mass) => new { massId, nodeId, mass };

    private static string TraceOf(RunResult result) =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.EditedJson)!, "apply-trace.json"));

    [Fact]
    public void AddMasses_OnBaseNodeAndAddedNode()
    {
        var baseModel = BaseModel();
        int m1 = NextElementId(baseModel), m2 = m1 + 1;
        int newNode = MaxNodeId(baseModel) + 1;
        var result = Run(
            Intent("addNode", new { nodeId = newNode, x = 0.0, y = 0.0, z = 0.0 }),
            Intent("addElement", new { elementId = m2 + 1, startNode = 4, endNode = newNode, propertyId = 2 }),
            Intent("addMasses", new { masses = new[] { Mass(m1, 4, 2.5), Mass(m2, newNode, 1.25) } }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var pm1 = MassOf(doc.RootElement, m1)!.Value;
        pm1.GetProperty("nodeId").GetInt32().Should().Be(4);
        pm1.GetProperty("mass").GetDouble().Should().Be(2.5);
        var pm2 = MassOf(doc.RootElement, m2)!.Value;
        pm2.GetProperty("nodeId").GetInt32().Should().Be(newNode);
        pm2.GetProperty("mass").GetDouble().Should().Be(1.25);
        // 원본 질량은 그대로
        MassOf(doc.RootElement, 14)!.Value.GetProperty("mass").GetDouble().Should().Be(0.0143);

        var bdf = File.ReadAllText(result.EditedBdf);
        bdf.Should().MatchRegex($@"(?m)^CONM2\s+{m1}\s+4\s+0\s+2\.5");
        bdf.Should().MatchRegex($@"(?m)^CONM2\s+{m2}\s+{newNode}\s+0\s+1\.25");
        TraceOf(result).Should().Contain("addMasses");
    }

    [Fact]
    public void AddMasses_IdCollision_IsRejected()
    {
        var baseModel = BaseModel();
        int next = NextElementId(baseModel);
        // 원본 요소(E2)·RBE(13)·CONM2(14) 번호
        Run(Intent("addMasses", new { masses = new[] { Mass(2, 4, 1.0) } })).ExitCode.Should().Be(65);
        Run(Intent("addMasses", new { masses = new[] { Mass(13, 4, 1.0) } })).ExitCode.Should().Be(65);
        Run(Intent("addMasses", new { masses = new[] { Mass(14, 4, 1.0) } })).ExitCode.Should().Be(65);
        // addElement 가 쓰는 번호 — 기록 순서와 무관하게 거부
        Run(
            Intent("addElement", new { elementId = next, startNode = 4, endNode = 9, propertyId = 2 }),
            Intent("addMasses", new { masses = new[] { Mass(next, 4, 1.0) } })).ExitCode.Should().Be(65);
        Run(
            Intent("addMasses", new { masses = new[] { Mass(next, 4, 1.0) } }),
            Intent("addElement", new { elementId = next, startNode = 4, endNode = 9, propertyId = 2 })).ExitCode.Should().Be(65);
        // 한 intent 안 중복 · 두 addMasses 사이 중복
        Run(Intent("addMasses", new { masses = new[] { Mass(next, 4, 1.0), Mass(next, 9, 1.0) } })).ExitCode.Should().Be(65);
        Run(
            Intent("addMasses", new { masses = new[] { Mass(next, 4, 1.0) } }),
            Intent("addMasses", new { masses = new[] { Mass(next, 9, 1.0) } })).ExitCode.Should().Be(65);
    }

    [Fact]
    public void AddMasses_InvalidParams_AreRejected()
    {
        int next = NextElementId(BaseModel());
        Run(Intent("addMasses", new { masses = new[] { Mass(next, 99999, 1.0) } })).ExitCode.Should().Be(65);
        Run(Intent("addMasses", new { masses = new[] { Mass(next, 4, 0.0) } })).ExitCode.Should().Be(65);
        Run(Intent("addMasses", new { masses = new[] { Mass(next, 4, -1.0) } })).ExitCode.Should().Be(65);
        Run(Intent("addMasses", new { masses = new[] { Mass(0, 4, 1.0) } })).ExitCode.Should().Be(65);
        Run(Intent("addMasses", new { masses = Array.Empty<object>() })).ExitCode.Should().Be(65);
    }

    [Fact]
    public void AddMasses_RecordedAfterCentroidRigidWithMass_KeepsStudioId()
    {
        // 무게중심 RBE(질량 포함)가 NextUnusedElementId 로 RBE·CONM2 번호를 받기 전에 addMasses 가 적용돼야 Studio 번호가 유지된다.
        var baseModel = BaseModel();
        int massId = NextElementId(baseModel);
        var result = Run(
            Intent("addRigid", new { independentNode = (int?)null, dependentNodes = new[] { 27, 28, 30 }, createIndependentAtCentroid = true, cm = "123456", mass = 3.0 }),
            Intent("addMasses", new { masses = new[] { Mass(massId, 9, 0.5) } }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var pm = MassOf(doc.RootElement, massId)!.Value;
        pm.GetProperty("nodeId").GetInt32().Should().Be(9);
        pm.GetProperty("mass").GetDouble().Should().Be(0.5);
        doc.RootElement.GetProperty("rigids").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).Should().NotContain(massId);
        // 무게중심 질량은 다른 번호로 들어간다
        doc.RootElement.GetProperty("pointMasses").EnumerateArray()
            .Should().Contain(x => x.GetProperty("id").GetInt32() != massId && x.GetProperty("mass").GetDouble() == 3.0);
    }

    [Fact]
    public void AddedMass_LostByLaterEdit_Fails()
    {
        // N10 이 속한 연결 그룹을 지우면 추가한 질량도 빠진다 — 조용히 빠진 BDF 를 내지 않는다.
        var baseModel = BaseModel();
        int massId = NextElementId(baseModel);
        int groupId = baseModel.GetProperty("connectivity").GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("nodeIds").EnumerateArray().Any(n => n.GetInt32() == 10))
            .GetProperty("id").GetInt32();
        var result = Run(
            Intent("addMasses", new { masses = new[] { Mass(massId, 10, 1.0) } }),
            Intent("deleteGroup", new { groupId }));
        result.ExitCode.Should().Be(70, result.Log);
        result.Log.Should().Contain($"M{massId}");
    }

    [Fact]
    public void ChangeMasses_UpdatesJsonBdfAndTrace()
    {
        var result = Run(Intent("changeMasses", new { changes = new[] { new { massId = 14, mass = 1.5 } } }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var pm = MassOf(doc.RootElement, 14)!.Value;
        pm.GetProperty("mass").GetDouble().Should().Be(1.5);
        pm.GetProperty("nodeId").GetInt32().Should().Be(22);
        pm.GetProperty("sourceName").GetString().Should().Be("=21945/52169");
        // 다른 질량은 그대로
        MassOf(doc.RootElement, 16)!.Value.GetProperty("mass").GetDouble().Should().Be(0.00172);

        File.ReadAllText(result.EditedBdf).Should().MatchRegex(@"(?m)^CONM2\s+14\s+22\s+0\s+1\.5");
        TraceOf(result).Should().Contain("changeMasses").And.Contain("M14 0.0143->1.5");
    }

    [Fact]
    public void ChangeMasses_InvalidParams_AreRejected()
    {
        Run(Intent("changeMasses", new { changes = new[] { new { massId = 99999, mass = 1.0 } } })).ExitCode.Should().Be(65);
        Run(Intent("changeMasses", new { changes = new[] { new { massId = 14, mass = 0.0 } } })).ExitCode.Should().Be(65);
        Run(Intent("changeMasses", new { changes = new[] { new { massId = 14, mass = 1.0 }, new { massId = 14, mass = 2.0 } } }))
            .ExitCode.Should().Be(65);
        Run(Intent("changeMasses", new { changes = Array.Empty<object>() })).ExitCode.Should().Be(65);
        // 이번에 추가한 질량은 changeMasses 대상이 아니다(Studio 가 addMasses 기록을 고친다).
        int next = NextElementId(BaseModel());
        Run(
            Intent("addMasses", new { masses = new[] { Mass(next, 4, 1.0) } }),
            Intent("changeMasses", new { changes = new[] { new { massId = next, mass = 2.0 } } })).ExitCode.Should().Be(65);
    }

    [Fact]
    public void MassOnlyAddedNode_IsStillReportedUnbound()
    {
        // 질량만 붙은 새 GRID 는 강성이 없어 하중이 구조에 전달되지 않는다 — 미연결 경고를 남겨야 한다(리뷰 지적 2026-10-29).
        int massId = NextElementId(BaseModel());
        var result = Run(
            Intent("addNode", new { nodeId = 900, x = 0.0, y = 0.0, z = 0.0 }),
            Intent("addMasses", new { masses = new[] { Mass(massId, 900, 1.0) } }));
        result.ExitCode.Should().Be(0, result.Log);
        TraceOf(result).Should().Contain("UNBOUND_ADDED_NODES").And.Contain("하중이 전달되지 않습니다");
    }

    [Fact]
    public void ChangeMasses_ThenDeleteRigidWithMass_SkipsWithWarning()
    {
        // 값을 바꾼 장비 질량(M14 @ COG N22)의 RBE 13 을 나중에 '질량 함께 삭제' — 원본 RBE 삭제가 먼저 적용돼
        // 값 변경 시점에 질량이 없다. 최종 모델에 M14 가 없는 것은 Studio 미리보기와 같으므로 실패가 아니라 경고다.
        var result = Run(
            Intent("changeMasses", new { changes = new[] { new { massId = 14, mass = 1.0 }, new { massId = 16, mass = 0.5 } } }),
            Intent("deleteRigid", new { rigidId = 13, removeMass = true }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        MassOf(doc.RootElement, 14).Should().BeNull();
        MassOf(doc.RootElement, 16)!.Value.GetProperty("mass").GetDouble().Should().Be(0.5);
        TraceOf(result).Should().Contain("MASS_CHANGE_SKIPPED").And.Contain("M14");
    }
}
