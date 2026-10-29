using System.Text.Json;
using FluentAssertions;
using static Cmb.Integration.Tests.DeleteRigidAddNodeApplyEditTests;

namespace Cmb.Integration.Tests;

// 부재 단면 변경(changeSection, 2026-10-29 기본 모델 편집). 요소의 PID 만 바꾸고 방향 벡터·노드·Category 는 그대로 둔다.
// 샘플 hitess_mini: 구조 PID 1~7, 배관 PID 1000~1003(base BDF 주석 PipePidBase=1000).
// E2 = PID 2(Rod), E5 = PID 5(L), E21 = PID 4(Bar, 기울어진 방향 벡터), E8 = 배관(PID 1000).
public class ChangeSectionApplyEditTests
{
    private static JsonElement BaseModel()
    {
        var sampleDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "hitess_mini", "20260617_082620"));
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(sampleDir, "06_Validation.json"))).RootElement.Clone();
    }

    private static JsonElement ElementOf(JsonElement root, int elementId) =>
        root.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == elementId);

    private static int NextElementId(JsonElement root) =>
        new[] { "elements", "rigids", "pointMasses" }
            .SelectMany(k => root.GetProperty(k).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()))
            .Max() + 1;

    private static int MaterialId(JsonElement root) => root.GetProperty("materials")[0].GetProperty("id").GetInt32();

    private static object NewL(int pid, int mid) => new { id = pid, kind = "L", dims = new[] { 100.0, 100.0, 10.0, 10.0 }, materialId = mid };

    private static string TraceOf(RunResult result) =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.EditedJson)!, "apply-trace.json"));

    [Fact]
    public void ChangeToExistingProperty_UpdatesJsonBdfAndTrace()
    {
        var result = Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 5 }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var e2 = ElementOf(doc.RootElement, 2);
        e2.GetProperty("propertyId").GetInt32().Should().Be(5);
        e2.GetProperty("startNode").GetInt32().Should().Be(3);
        e2.GetProperty("endNode").GetInt32().Should().Be(4);
        e2.GetProperty("category").GetString().Should().Be("Structure");
        File.ReadAllText(result.EditedBdf).Should().MatchRegex(@"(?m)^CBEAM\s+2\s+5\s+3\s+4");
        // 더 이상 안 쓰는 옛 PBEAML 2 는 그대로 남는다.
        File.ReadAllText(result.EditedBdf).Should().MatchRegex(@"(?m)^PBEAML\s+2\s+");
        TraceOf(result).Should().Contain("changeSection").And.Contain("E2 2->5");
    }

    [Fact]
    public void NewProperty_CreatesPbeaml_AndSecondIntentReusesIt()
    {
        var baseModel = BaseModel();
        int mid = MaterialId(baseModel);
        var result = Run(
            Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8, newProperty = NewL(8, mid) }),
            Intent("changeSection", new { elementIds = new[] { 5 }, propertyId = 8 }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        ElementOf(doc.RootElement, 2).GetProperty("propertyId").GetInt32().Should().Be(8);
        ElementOf(doc.RootElement, 5).GetProperty("propertyId").GetInt32().Should().Be(8);
        var bdf = File.ReadAllText(result.EditedBdf);
        bdf.Should().MatchRegex(@"(?m)^CBEAM\s+2\s+8\s+3\s+4");
        bdf.Should().MatchRegex(@"(?m)^CBEAM\s+5\s+8\s+9\s+10");
        System.Text.RegularExpressions.Regex.Matches(bdf, $@"(?m)^PBEAML\s+8\s+{mid}\s+L").Count.Should().Be(1);
    }

    [Fact]
    public void SecondIntentWithoutNewProperty_RecordedFirst_IsRejected()
    {
        // 앞선 changeSection 이 만들 단면만 쓸 수 있다 — 기록 순서상 뒤에서 만들면 아직 없다.
        int mid = MaterialId(BaseModel());
        Run(
            Intent("changeSection", new { elementIds = new[] { 5 }, propertyId = 8 }),
            Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8, newProperty = NewL(8, mid) }))
            .ExitCode.Should().Be(65);
    }

    [Fact]
    public void PipeElement_IsRejected()
    {
        var result = Run(Intent("changeSection", new { elementIds = new[] { 8 }, propertyId = 5 }));
        result.ExitCode.Should().Be(65, result.Log);
    }

    [Fact]
    public void MissingElement_IsRejected()
    {
        Run(Intent("changeSection", new { elementIds = new[] { 99999 }, propertyId = 5 })).ExitCode.Should().Be(65);
        // 앞선 deleteElements 가 지운 요소 — 적용 시점에 없다.
        var result = Run(
            Intent("deleteElements", new { elementIds = new[] { 2 } }),
            Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 5 }));
        result.ExitCode.Should().Be(65, result.Log);
        // 로그 인코딩이 콘솔 코드 페이지를 따라가 한글 대신 ASCII 부분만 본다.
        result.Log.Should().Contain("changeSection: element 2");
    }

    [Fact]
    public void ChangeBeforeDelete_KeepsRecordOrder()
    {
        // 단면을 바꾼 뒤 지우는 것은 정상(기록 순서 유지).
        var result = Run(
            Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 5 }),
            Intent("deleteElements", new { elementIds = new[] { 2 } }));
        result.ExitCode.Should().Be(0, result.Log);
        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        doc.RootElement.GetProperty("elements").EnumerateArray().Should().NotContain(e => e.GetProperty("id").GetInt32() == 2);
    }

    [Fact]
    public void PipeZoneProperty_IsRejected()
    {
        int mid = MaterialId(BaseModel());
        // base BDF 주석 PipePidBase=1000 — 1000 이상은 배관 구간.
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 1500, newProperty = NewL(1500, mid) }))
            .ExitCode.Should().Be(65);
        // 기존 배관 PID 로 바꾸는 것도 거부.
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 1000 })).ExitCode.Should().Be(65);
        // addElement 의 newProperty 에도 같은 검사.
        int eid = NextElementId(BaseModel());
        Run(Intent("addElement", new { elementId = eid, startNode = 4, endNode = 9, propertyId = 1500, newProperty = NewL(1500, mid) }))
            .ExitCode.Should().Be(65);
    }

    [Fact]
    public void PipePidBase_IsReadFromBaseBdf()
    {
        // 기본값(101) 이 아니라 base BDF 주석(1000)을 따른다 — 200 은 구조 구간이고 결과 BDF 도 같은 주석을 쓴다.
        int mid = MaterialId(BaseModel());
        var result = Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 200, newProperty = NewL(200, mid) }));
        result.ExitCode.Should().Be(0, result.Log);
        var bdf = File.ReadAllText(result.EditedBdf);
        bdf.Should().Contain("$ HiTessModelBuilder: PipePidBase=1000");
        int structHeader = bdf.IndexOf("STRUCTURE SECTIONS", StringComparison.Ordinal);
        int pipeHeader = bdf.IndexOf("PIPE SECTIONS", StringComparison.Ordinal);
        int pbeaml200 = System.Text.RegularExpressions.Regex.Match(bdf, @"(?m)^PBEAML\s+200\s+").Index;
        pbeaml200.Should().BeGreaterThan(structHeader).And.BeLessThan(pipeHeader);
    }

    [Fact]
    public void Orientation_IsPreserved()
    {
        var before = ElementOf(BaseModel(), 21).GetProperty("orientation").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var result = Run(Intent("changeSection", new { elementIds = new[] { 21 }, propertyId = 5 }));
        result.ExitCode.Should().Be(0, result.Log);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.EditedJson));
        var e21 = ElementOf(doc.RootElement, 21);
        e21.GetProperty("propertyId").GetInt32().Should().Be(5);
        var after = e21.GetProperty("orientation").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        after.Should().Equal(before);
        e21.GetProperty("startNode").GetInt32().Should().Be(7);
        e21.GetProperty("endNode").GetInt32().Should().Be(33);
    }

    [Fact]
    public void AddElementProperty_CanBeUsedByChangeSection()
    {
        // changeSection 을 먼저 기록해도 addElement 가 먼저 적용되므로 그 새 단면을 쓸 수 있다.
        var baseModel = BaseModel();
        int eid = NextElementId(baseModel);
        int mid = MaterialId(baseModel);
        var result = Run(
            Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8 }),
            Intent("addElement", new { elementId = eid, startNode = 4, endNode = 9, propertyId = 8, newProperty = NewL(8, mid) }));
        result.ExitCode.Should().Be(0, result.Log);

        var bdf = File.ReadAllText(result.EditedBdf);
        bdf.Should().MatchRegex(@"(?m)^CBEAM\s+2\s+8\s+3\s+4");
        bdf.Should().MatchRegex($@"(?m)^CBEAM\s+{eid}\s+8\s+4\s+9");
        System.Text.RegularExpressions.Regex.Matches(bdf, $@"(?m)^PBEAML\s+8\s+{mid}\s+L").Count.Should().Be(1);
    }

    [Fact]
    public void AddedElement_CanChangeSection()
    {
        var baseModel = BaseModel();
        int eid = NextElementId(baseModel);
        var result = Run(
            Intent("addElement", new { elementId = eid, startNode = 4, endNode = 9, propertyId = 2 }),
            Intent("changeSection", new { elementIds = new[] { eid }, propertyId = 5 }));
        result.ExitCode.Should().Be(0, result.Log);
        File.ReadAllText(result.EditedBdf).Should().MatchRegex($@"(?m)^CBEAM\s+{eid}\s+5\s+4\s+9");
    }

    [Fact]
    public void InvalidParams_AreRejected()
    {
        int mid = MaterialId(BaseModel());
        Run(Intent("changeSection", new { elementIds = Array.Empty<int>(), propertyId = 5 })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 2, 2 }, propertyId = 5 })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 0 }, propertyId = 5 })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 77 })).ExitCode.Should().Be(65);
        // newProperty: id 불일치 / 이미 있는 PID / 종류 / 치수 / 재료
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8, newProperty = NewL(9, mid) })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 5, newProperty = NewL(5, mid) })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8,
            newProperty = new { id = 8, kind = "Zed", dims = new[] { 10.0 }, materialId = mid } })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8,
            newProperty = new { id = 8, kind = "L", dims = new[] { 100.0, 0.0, 10.0, 10.0 }, materialId = mid } })).ExitCode.Should().Be(65);
        Run(Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8,
            newProperty = new { id = 8, kind = "L", dims = new[] { 100.0, 100.0, 10.0, 10.0 }, materialId = 999 } })).ExitCode.Should().Be(65);
        // 두 intent 가 같은 새 PID 를 각각 만들려 함
        Run(
            Intent("changeSection", new { elementIds = new[] { 2 }, propertyId = 8, newProperty = NewL(8, mid) }),
            Intent("changeSection", new { elementIds = new[] { 5 }, propertyId = 8, newProperty = NewL(8, mid) }))
            .ExitCode.Should().Be(65);
    }
}
