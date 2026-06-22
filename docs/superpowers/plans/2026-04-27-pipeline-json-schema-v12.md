# Pipeline JSON Schema v1.2 Enhancement Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pipeline_json_enhancement_spec.md`에 명시된 11개 항목을 파이프라인 JSON 출력에 반영하여 schemaVersion을 1.1 → 1.2로 올린다.

**Architecture:**
- `FeModelJson.cs` 에 새 DTO 추가 및 `ToJson()` 시그니처 확장 (diagnostics, stageIndex, durationMs, stageDeltas, stageHistory, validationResult, elementQuality)
- `PipelineRunner.Run()` 콜백에 `durationMs`와 `StageDeltaInfo` 추가
- `Program.cs` 에서 타이밍·stageIndex·stageHistory 누적·validationResult 조립
- `HealthMetricsAnalyzer` 에 elementQuality 계산 추가
- FinalValidationStage 에서 validationResult 조립

**Tech Stack:** .NET 8, C# 12, System.Text.Json Source Generators, xUnit, FluentAssertions

---

## 수정 파일 목록

| 파일 | 변경 종류 |
|------|----------|
| `src/Cmb.Core/Serialization/FeModelJson.cs` | DTO 추가 · ToJson 시그니처 확장 · ToDto 로직 수정 |
| `src/Cmb.Pipeline/Core/PipelineRunner.cs` | 콜백 시그니처 변경 (durationMs, StageDeltaInfo 추가) |
| `src/Cmb.Pipeline/Core/StageDeltaInfo.cs` | 신규 — 스테이지 전후 스냅샷 레코드 |
| `src/Cmb.Pipeline/Analysis/HealthMetricsAnalyzer.cs` | elementQuality 계산 추가 |
| `src/Cmb.Pipeline/Stages/FinalValidationStage.cs` | validationResult 빌드 |
| `src/Cmb.Cli/Program.cs` | 콜백 업데이트 (stageIndex·duration·stageHistory·validationResult) |
| `tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs` | 신규 — Task 1~4 테스트 |
| `tests/Cmb.Pipeline.Tests/Analysis/HealthMetricsAnalyzerTests.cs` | elementQuality 테스트 추가 |
| `CLAUDE.md` | schemaVersion, 파이프라인 다이어그램, tolerance 표 업데이트 |

---

## Task 1: `diagnostics[]` 실제 내용 출력 + `msg` → `message` 개명 + schemaVersion 1.2

**현재 상태:** `ToDto()` 에서 `Diagnostics = []` 하드코딩 → JSON에 항상 빈 배열.  
**목표:** 스테이지 진단 항목을 JSON에 포함. `"msg"` 필드명을 스펙에 맞게 `"message"` 로 변경.

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (DiagnosticDto, FeModelJson.ToJson, ToDto, schemaVersion)
- Modify: `src/Cmb.Cli/Program.cs:226-228` (stageDiagnostics 전달)
- Create: `tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs`

- [ ] **Step 1-1: 실패하는 테스트 작성**

`tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs` 파일을 생성:

```csharp
using System.Text.Json;
using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Core;
using FluentAssertions;

namespace Cmb.Core.Tests.Serialization;

public class FeModelJsonSchemaV12Tests
{
    private static FeModel MinimalModel()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        m.Nodes.Add(new Node(2, new Point3(100, 0, 0)));
        var sec = new BeamSection(10, BeamSectionKind.Rod, [25.0], 1);
        var mat = new Material(1, "STEEL", 200000, 0.3, 7850e-9);
        m.Sections.Add(sec);
        m.Materials.Add(mat);
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        return m;
    }

    [Fact]
    public void ToJson_WithDiagnostics_PopulatesDiagnosticsArray()
    {
        var model = MinimalModel();
        var diags = new Diagnostic[]
        {
            new(DiagnosticSeverity.Warning, "FREE_END_NODE", "Node 1 is a free end.", nodeId: 1),
            new(DiagnosticSeverity.Error,   "SHORT_ELEMENT", "Element 1 is too short.", elementId: 1),
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
}
```

- [ ] **Step 1-2: 테스트 실패 확인**

```
dotnet test tests/Cmb.Core.Tests --filter "FullyQualifiedName~FeModelJsonSchemaV12Tests" --no-build
```
Expected: 3개 실패 (message 필드 없음, schemaVersion 1.1, diagnostics 비어있음)

- [ ] **Step 1-3: `DiagnosticDto` 필드명 변경**

`src/Cmb.Core/Serialization/FeModelJson.cs` 의 `DiagnosticDto`:

```csharp
public sealed class DiagnosticDto
{
    [JsonPropertyName("severity")] public string Severity { get; set; } = "";
    [JsonPropertyName("code")]     public string Code     { get; set; } = "";
    [JsonPropertyName("message")]  public string Message  { get; set; } = "";   // 변경: msg → message
    [JsonPropertyName("elemId")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                   public int?   ElemId   { get; set; }
    [JsonPropertyName("nodeId")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                   public int?   NodeId   { get; set; }
}
```

- [ ] **Step 1-4: `ToJson()` 시그니처에 `stageDiagnostics` 추가**

`FeModelJson.ToJson()` 메서드 전체를 교체:

```csharp
public static string ToJson(
    this FeModel model,
    string phase = "",
    string stageName = "",
    ConnectivityDto? connectivity = null,
    HealthMetricsDto? healthMetrics = null,
    IReadOnlyList<Diagnostic>? stageDiagnostics = null)
{
    var dto = ToDto(model, phase, stageName);
    dto.Connectivity  = connectivity;
    dto.HealthMetrics = healthMetrics;
    dto.Diagnostics   = stageDiagnostics is { Count: > 0 }
        ? stageDiagnostics.Select(d => new DiagnosticDto
          {
              Severity = d.Severity.ToString().ToLowerInvariant(),
              Code     = d.Code,
              Message  = d.Message,
              ElemId   = d.ElementId,
              NodeId   = d.NodeId,
          }).ToList()
        : [];
    return JsonSerializer.Serialize(dto, _writeOptions);
}
```

- [ ] **Step 1-5: `schemaVersion` 을 "1.2" 로 변경**

`ToDto()` 내부:
```csharp
SchemaVersion = "1.2",
```

- [ ] **Step 1-6: `Program.cs` 콜백에서 `stageDiagnostics` 전달**

`src/Cmb.Cli/Program.cs` 의 `onStageComplete` 콜백 내 `ToJson` 호출 부분:

```csharp
File.WriteAllText(jsonFile, m.ToJson(phase: "C", stageName: stageName,
    connectivity: connectivity, healthMetrics: healthMetrics,
    stageDiagnostics: stageDiagnostics));
```

- [ ] **Step 1-7: 빌드 + 테스트 통과 확인**

```
dotnet build HiTessModelBuilder.sln
dotnet test tests/Cmb.Core.Tests --filter "FullyQualifiedName~FeModelJsonSchemaV12Tests"
```
Expected: 3개 PASS

- [ ] **Step 1-8: 전체 테스트 회귀 없음 확인**

```
dotnet test HiTessModelBuilder.sln
```

- [ ] **Step 1-9: 커밋**

```
git add src/Cmb.Core/Serialization/FeModelJson.cs src/Cmb.Cli/Program.cs tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs
git commit -m "✨ feat: wire stage diagnostics into JSON output (schema v1.2, msg→message)"
```

---

## Task 2: `elements[].lengthMm` 사전 계산 필드 추가

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (BeamElementDto, ToDto)
- Modify: `tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs`

- [ ] **Step 2-1: 실패하는 테스트 추가**

`FeModelJsonSchemaV12Tests.cs` 에 추가:

```csharp
[Fact]
public void ToJson_ElementHasCorrectLengthMm()
{
    var m = new FeModel();
    m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
    m.Nodes.Add(new Node(2, new Point3(300, 0, 0)));
    m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
    m.Materials.Add(new Material(1, "S", 200000, 0.3, 7.85e-6));
    m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));

    var doc = JsonDocument.Parse(m.ToJson());
    var elem = doc.RootElement.GetProperty("elements").EnumerateArray().First();
    elem.GetProperty("lengthMm").GetDouble().Should().BeApproximately(300.0, 0.001);
}
```

- [ ] **Step 2-2: 테스트 실패 확인**

```
dotnet test tests/Cmb.Core.Tests --filter "ElementHasCorrectLengthMm"
```
Expected: FAIL with "property 'lengthMm' not found"

- [ ] **Step 2-3: `BeamElementDto` 에 `LengthMm` 추가**

```csharp
public sealed class BeamElementDto
{
    // 기존 필드들 유지 ...
    [JsonPropertyName("lengthMm")] public double LengthMm { get; set; }
}
```

- [ ] **Step 2-4: `ToDto()` 에서 nodeById 선언 및 길이 계산**

`ToDto()` 메서드 최상단에 nodeById 빌드 추가 (현재 없음):

```csharp
private static FeModelDto ToDto(FeModel model, string phase, string stageName)
{
    var nodeById = model.Nodes.ToDictionary(n => n.Id);  // ← 추가

    return new FeModelDto
    {
        // ...
        Elements = model.Elements.Select(e =>
        {
            double len = 0.0;
            if (nodeById.TryGetValue(e.StartNodeId, out var sn) &&
                nodeById.TryGetValue(e.EndNodeId,   out var en))
                len = Math.Round(sn.Position.DistanceTo(en.Position), 3);

            return new BeamElementDto
            {
                Id          = e.Id,
                Type        = "BEAM",
                StartNode   = e.StartNodeId,
                EndNode     = e.EndNodeId,
                PropertyId  = e.PropertyId,
                Category    = e.Category.ToString(),
                Orientation = [e.Orientation.X, e.Orientation.Y, e.Orientation.Z],
                SourceName  = e.SourceName,
                ParentElemId = e.ParentElementId,
                LengthMm    = len,
            };
        }).ToList(),
        // ...
    };
}
```

- [ ] **Step 2-5: 빌드 + 테스트 통과**

```
dotnet build HiTessModelBuilder.sln
dotnet test tests/Cmb.Core.Tests --filter "FullyQualifiedName~FeModelJsonSchemaV12Tests"
```

- [ ] **Step 2-6: 커밋**

```
git add src/Cmb.Core/Serialization/FeModelJson.cs tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs
git commit -m "✨ feat: add elements[].lengthMm to JSON output"
```

---

## Task 3: `meta.stageIndex` + `meta.processingDurationMs`

`PipelineRunner` 콜백 시그니처를 변경하고, `Program.cs` 에서 인덱스와 경과시간을 전달한다.

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (MetaDto, ToJson 시그니처, ToDto)
- Modify: `src/Cmb.Pipeline/Core/PipelineRunner.cs` (콜백 시그니처)
- Create: `src/Cmb.Pipeline/Core/StageDeltaInfo.cs`
- Modify: `src/Cmb.Cli/Program.cs` (콜백 람다 시그니처, ToJson 호출)

> **Note:** StageDeltaInfo는 Task 5(stageDeltas)에서도 사용된다. 여기서 미리 생성.

- [ ] **Step 3-1: 실패하는 테스트 추가**

`FeModelJsonSchemaV12Tests.cs` 에 추가:

```csharp
[Fact]
public void ToJson_MetaHasStageIndexAndDurationMs()
{
    var json = MinimalModel().ToJson(stageIndex: 3, processingDurationMs: 42L);
    var doc  = JsonDocument.Parse(json);
    var meta = doc.RootElement.GetProperty("meta");
    meta.GetProperty("stageIndex").GetInt32().Should().Be(3);
    meta.GetProperty("processingDurationMs").GetInt64().Should().Be(42L);
}
```

- [ ] **Step 3-2: 테스트 실패 확인**

```
dotnet test tests/Cmb.Core.Tests --filter "MetaHasStageIndex"
```

- [ ] **Step 3-3: `MetaDto` 필드 추가**

```csharp
public sealed class MetaDto
{
    [JsonPropertyName("phase")]                 public string Phase                  { get; set; } = "";
    [JsonPropertyName("stageName")]             public string StageName              { get; set; } = "";
    [JsonPropertyName("stageIndex")]            public int    StageIndex             { get; set; }
    [JsonPropertyName("timestamp")]             public string Timestamp              { get; set; } = "";
    [JsonPropertyName("processingDurationMs")]  public long   ProcessingDurationMs   { get; set; }
    [JsonPropertyName("unit")]                  public string Unit                   { get; set; } = "mm";
    [JsonPropertyName("schemaVersion")]         public string SchemaVersion          { get; set; } = "1.2";
}
```

- [ ] **Step 3-4: `ToJson()` 에 파라미터 추가**

```csharp
public static string ToJson(
    this FeModel model,
    string phase = "",
    string stageName = "",
    ConnectivityDto? connectivity = null,
    HealthMetricsDto? healthMetrics = null,
    IReadOnlyList<Diagnostic>? stageDiagnostics = null,
    int stageIndex = 0,
    long processingDurationMs = 0L)
{
    var dto = ToDto(model, phase, stageName, stageIndex, processingDurationMs);
    // ... 나머지 동일
}
```

`ToDto()` 시그니처도 변경:

```csharp
private static FeModelDto ToDto(FeModel model, string phase, string stageName,
    int stageIndex = 0, long processingDurationMs = 0L)
{
    // ...
    Meta = new MetaDto
    {
        Phase                = phase,
        StageName            = stageName,
        StageIndex           = stageIndex,
        Timestamp            = DateTime.UtcNow.ToString("o"),
        ProcessingDurationMs = processingDurationMs,
        Unit                 = model.LengthUnit,
        SchemaVersion        = "1.2",
    },
    // ...
}
```

- [ ] **Step 3-5: `StageDeltaInfo.cs` 신규 생성**

`src/Cmb.Pipeline/Core/StageDeltaInfo.cs`:

```csharp
namespace Cmb.Pipeline.Core;

public sealed record StageDeltaInfo(
    string FromStage,
    int    ElemCountBefore,
    int    ElemCountAfter,
    int    NodeCountBefore,
    int    NodeCountAfter)
{
    public int NetElementDelta => ElemCountAfter - ElemCountBefore;
    public int NetNodeDelta    => NodeCountAfter - NodeCountBefore;
}
```

- [ ] **Step 3-6: `PipelineRunner` 콜백 시그니처 변경**

`src/Cmb.Pipeline/Core/PipelineRunner.cs`:

```csharp
public static PipelineReport Run(
    FeModel                                         model,
    IReadOnlyList<IPipelineStage>                  stages,
    RunOptions                                     options,
    ILogger                                        logger,
    Action<string, FeModel, IReadOnlyList<Diagnostic>, long, StageDeltaInfo>? onStageComplete = null)
{
    var stageReports    = new List<StageReport>();
    string? stoppedAfter = null;
    bool pipelineOk     = true;
    string prevStageName = "initial";

    foreach (var stage in stages)
    {
        logger.LogInformation("Stage [{Name}] starting", stage.Name);

        var ctx          = new StageContext(model, options, logger);
        int elemBefore   = model.Elements.Count;
        int nodeBefore   = model.Nodes.Count;
        var sw           = Stopwatch.StartNew();
        bool ok          = false;

        try
        {
            ok = stage.Execute(ctx);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stage [{Name}] threw an exception", stage.Name);
            ctx.AddDiagnostic(DiagnosticSeverity.Error, "STAGE_EXCEPTION",
                $"{stage.Name}: {ex.Message}");
            ok = false;
        }

        sw.Stop();
        var delta = new StageDeltaInfo(prevStageName,
            elemBefore, model.Elements.Count,
            nodeBefore, model.Nodes.Count);
        stageReports.Add(new StageReport(stage.Name, ok, sw.Elapsed, ctx.Diagnostics));

        logger.LogInformation("Stage [{Name}] {Result} in {Elapsed:F0}ms",
            stage.Name, ok ? "succeeded" : "FAILED", sw.Elapsed.TotalMilliseconds);

        onStageComplete?.Invoke(stage.Name, model, ctx.Diagnostics,
            (long)sw.Elapsed.TotalMilliseconds, delta);

        prevStageName = stage.Name;

        if (!ok)
        {
            pipelineOk = false;
            break;
        }

        if (options.StopAfterStage is not null &&
            string.Equals(stage.Name, options.StopAfterStage, StringComparison.OrdinalIgnoreCase))
        {
            stoppedAfter = stage.Name;
            break;
        }
    }

    return new PipelineReport(pipelineOk, stoppedAfter, stageReports);
}
```

- [ ] **Step 3-7: `Program.cs` 콜백 람다 시그니처 업데이트**

`src/Cmb.Cli/Program.cs` 의 `onStageComplete` 람다:

```csharp
onStageComplete: (stageName, m, stageDiagnostics, durationMs, delta) =>
{
    idx++;
    var connectivity  = ConnectivityAnalyzer.Analyze(m);
    var healthMetrics = HealthMetricsAnalyzer.Analyze(m, stageDiagnostics, options.Tolerances);
    var prefix   = Path.Combine(outDir, $"{idx:D2}_{stageName}");
    var jsonFile = prefix + ".json";
    File.WriteAllText(jsonFile, m.ToJson(phase: "C", stageName: stageName,
        connectivity: connectivity, healthMetrics: healthMetrics,
        stageDiagnostics: stageDiagnostics,
        stageIndex: idx,
        processingDurationMs: durationMs));

    // ... 이후 Console.WriteLine 들은 그대로 유지
```

- [ ] **Step 3-8: 빌드 + 테스트 통과**

```
dotnet build HiTessModelBuilder.sln
dotnet test HiTessModelBuilder.sln
```
Expected: 전체 테스트 PASS

- [ ] **Step 3-9: 커밋**

```
git add src/Cmb.Core/Serialization/FeModelJson.cs src/Cmb.Pipeline/Core/PipelineRunner.cs src/Cmb.Pipeline/Core/StageDeltaInfo.cs src/Cmb.Cli/Program.cs tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs
git commit -m "✨ feat: add meta.stageIndex and processingDurationMs to JSON output"
```

---

## Task 4: `nodes[].tags` 자동 계산 (FreeEnd / Orphan / Intersection / UboltAnchor)

Node 객체를 뮤테이션하지 않고, `ToDto()` 내에서 degree 맵 기반으로 JSON Tags를 증보한다.

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (ToDto — Nodes 매핑)

- [ ] **Step 4-1: 실패하는 테스트 추가**

```csharp
[Fact]
public void ToJson_FreeEndNode_GetsTagFreeEnd()
{
    // Node 1 — degree 1 (FreeEnd), Node 2 — degree 2 (not FreeEnd)
    var m = new FeModel();
    m.Nodes.Add(new Node(1, new Point3(0,   0, 0)));
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

    nodes[1].Should().Contain("FreeEnd");
    nodes[2].Should().NotContain("FreeEnd");
    nodes[3].Should().Contain("FreeEnd");
}

[Fact]
public void ToJson_OrphanNode_GetsTagOrphan()
{
    var m = new FeModel();
    m.Nodes.Add(new Node(1, new Point3(0, 0, 0)));   // orphan — no element

    var doc  = JsonDocument.Parse(m.ToJson());
    var tags = doc.RootElement.GetProperty("nodes").EnumerateArray().First()
                  .GetProperty("tags").EnumerateArray()
                  .Select(t => t.GetString()!).ToList();

    tags.Should().Contain("Orphan");
}

[Fact]
public void ToJson_IntersectionNode_GetsTagIntersection()
{
    // Node 2 — 3 elements meet → degree 3
    var m = new FeModel();
    for (int i = 1; i <= 4; i++)
        m.Nodes.Add(new Node(i, new Point3(i * 100, 0, 0)));
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
```

- [ ] **Step 4-2: 테스트 실패 확인**

```
dotnet test tests/Cmb.Core.Tests --filter "FullyQualifiedName~FeModelJsonSchemaV12Tests"
```
Expected: 4개 신규 실패

- [ ] **Step 4-3: `ToDto()` 에 자동 태그 계산 로직 추가**

`ToDto()` 내부, `nodeById` 선언 직후에 추가:

```csharp
// 요소 기반 degree 계산 (NodeTags 뮤테이션 없이 JSON 직렬화 시점에만 증보)
var nodeDegree = new Dictionary<int, int>();
foreach (var e in model.Elements)
{
    nodeDegree[e.StartNodeId] = nodeDegree.GetValueOrDefault(e.StartNodeId) + 1;
    nodeDegree[e.EndNodeId]   = nodeDegree.GetValueOrDefault(e.EndNodeId)   + 1;
}
var uboltAnchorIds = model.Rigids
    .Where(r => r.Remark == "UBOLT")
    .Select(r => r.IndependentNodeId)
    .ToHashSet();
```

`Nodes` 매핑에서:

```csharp
Nodes = model.Nodes.Select(n =>
{
    var tags = FlagsToStringList(n.Tags);
    bool isBoundary = n.Tags.HasFlag(NodeTags.Boundary);
    int deg = nodeDegree.GetValueOrDefault(n.Id, 0);

    if (deg == 0 && !isBoundary) tags.Add("Orphan");
    if (deg == 1 && !isBoundary) tags.Add("FreeEnd");
    if (deg >= 3)                tags.Add("Intersection");
    if (uboltAnchorIds.Contains(n.Id)) tags.Add("UboltAnchor");

    return new NodeDto { Id = n.Id, X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z, Tags = tags };
}).ToList(),
```

- [ ] **Step 4-4: 빌드 + 테스트 통과**

```
dotnet build HiTessModelBuilder.sln && dotnet test HiTessModelBuilder.sln
```
Expected: 전체 GREEN

- [ ] **Step 4-5: 커밋**

```
git add src/Cmb.Core/Serialization/FeModelJson.cs tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs
git commit -m "✨ feat: auto-compute FreeEnd/Orphan/Intersection/UboltAnchor node tags in JSON"
```

---

## Task 5: `stageDeltas` 필드 추가

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (StageDeltasDto 신규 + FeModelDto 필드 + ToJson 파라미터)
- Modify: `src/Cmb.Cli/Program.cs` (stageDeltas 전달)

- [ ] **Step 5-1: 실패하는 테스트 추가**

```csharp
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
    sd.GetProperty("netElementDelta").GetInt32().Should().Be(12);
    sd.GetProperty("netNodeDelta").GetInt32().Should().Be(10);
}
```

- [ ] **Step 5-2: `StageDeltasDto` 신규 + `FeModelDto` 필드 추가**

`FeModelJson.cs`:

```csharp
public sealed class StageDeltasDto
{
    [JsonPropertyName("fromStage")]       public string FromStage       { get; set; } = "";
    [JsonPropertyName("toStage")]         public string ToStage         { get; set; } = "";
    [JsonPropertyName("elemCountBefore")] public int    ElemCountBefore { get; set; }
    [JsonPropertyName("elemCountAfter")]  public int    ElemCountAfter  { get; set; }
    [JsonPropertyName("nodeCountBefore")] public int    NodeCountBefore { get; set; }
    [JsonPropertyName("nodeCountAfter")]  public int    NodeCountAfter  { get; set; }
    [JsonPropertyName("netElementDelta")] public int    NetElementDelta => ElemCountAfter - ElemCountBefore;
    [JsonPropertyName("netNodeDelta")]    public int    NetNodeDelta    => NodeCountAfter - NodeCountBefore;
}
```

`FeModelDto` 에 필드 추가:

```csharp
[JsonPropertyName("stageDeltas")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public StageDeltasDto? StageDeltas { get; set; }
```

`[JsonSerializable(typeof(StageDeltasDto))]` 을 `FeModelJsonContext` 에 추가.

- [ ] **Step 5-3: `ToJson()` 에 파라미터 추가**

```csharp
public static string ToJson(
    this FeModel model,
    ...
    StageDeltasDto? stageDeltas = null)
{
    var dto = ToDto(...);
    dto.StageDeltas = stageDeltas;
    ...
}
```

- [ ] **Step 5-4: `Program.cs` 에서 `StageDeltaInfo` → `StageDeltasDto` 변환 후 전달**

`onStageComplete` 람다 내:

```csharp
var deltasDto = new StageDeltasDto
{
    FromStage       = delta.FromStage,
    ToStage         = stageName,
    ElemCountBefore = delta.ElemCountBefore,
    ElemCountAfter  = delta.ElemCountAfter,
    NodeCountBefore = delta.NodeCountBefore,
    NodeCountAfter  = delta.NodeCountAfter,
};

File.WriteAllText(jsonFile, m.ToJson(..., stageDeltas: deltasDto));
```

- [ ] **Step 5-5: 빌드 + 테스트 통과 + 커밋**

```
dotnet build HiTessModelBuilder.sln && dotnet test HiTessModelBuilder.sln
git add src/Cmb.Core/Serialization/FeModelJson.cs src/Cmb.Cli/Program.cs tests/Cmb.Core.Tests/Serialization/FeModelJsonSchemaV12Tests.cs
git commit -m "✨ feat: add stageDeltas to JSON output"
```

---

## Task 6: `elementQuality` 분포 통계 추가 (HealthMetrics 확장)

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (ElementQualityDto + LengthDistributionDto 신규, HealthMetricsDto 필드 추가)
- Modify: `src/Cmb.Pipeline/Analysis/HealthMetricsAnalyzer.cs` (ComputeElementQuality 추가)
- Modify: `tests/Cmb.Pipeline.Tests/Analysis/HealthMetricsAnalyzerTests.cs` (테스트 추가 또는 신규 파일)

- [ ] **Step 6-1: 실패하는 테스트 작성**

`tests/Cmb.Pipeline.Tests/Analysis/HealthMetricsAnalyzerTests.cs` (없으면 신규):

```csharp
using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Analysis;

public class HealthMetricsAnalyzerTests
{
    private static FeModel BuildThreeElementModel()
    {
        // 길이 100, 200, 300 mm 3개 요소
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0,   0, 0)));
        m.Nodes.Add(new Node(2, new Point3(100, 0, 0)));
        m.Nodes.Add(new Node(3, new Point3(300, 0, 0)));
        m.Nodes.Add(new Node(4, new Point3(600, 0, 0)));
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        m.Materials.Add(new Material(1, "S", 200000, 0.3, 7.85e-6));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        m.Elements.Add(new BeamElement(2, 2, 3, 10, EntityCategory.Structure, Vector3.UnitZ));
        m.Elements.Add(new BeamElement(3, 3, 4, 10, EntityCategory.Structure, Vector3.UnitZ));
        return m;
    }

    [Fact]
    public void Analyze_ElementQuality_CorrectMinMaxMean()
    {
        var result = HealthMetricsAnalyzer.Analyze(BuildThreeElementModel());

        result.ElementQuality.Should().NotBeNull();
        result.ElementQuality!.LengthDistribution.Min.Should().BeApproximately(100.0, 0.01);
        result.ElementQuality.LengthDistribution.Max.Should().BeApproximately(300.0, 0.01);
        result.ElementQuality.LengthDistribution.Mean.Should().BeApproximately(200.0, 0.01);
    }

    [Fact]
    public void Analyze_ElementQuality_ShortElementCount()
    {
        var m = BuildThreeElementModel();
        // 길이 0.5mm 요소 추가 (ShortElemMinMm=5.0 기준)
        m.Nodes.Add(new Node(10, new Point3(1000,   0, 0)));
        m.Nodes.Add(new Node(11, new Point3(1000.5, 0, 0)));
        m.Elements.Add(new BeamElement(10, 10, 11, 10, EntityCategory.Structure, Vector3.UnitZ));

        var result = HealthMetricsAnalyzer.Analyze(m, tolerances: new Tolerances());
        result.ElementQuality!.ShortElementCount.Should().Be(1);
    }
}
```

- [ ] **Step 6-2: 실패 확인**

```
dotnet test tests/Cmb.Pipeline.Tests --filter "FullyQualifiedName~HealthMetricsAnalyzerTests"
```

- [ ] **Step 6-3: `LengthDistributionDto`, `ElementQualityDto` 신규 DTO 추가**

`FeModelJson.cs`:

```csharp
public sealed class LengthDistributionDto
{
    [JsonPropertyName("min")]    public double Min    { get; set; }
    [JsonPropertyName("max")]    public double Max    { get; set; }
    [JsonPropertyName("mean")]   public double Mean   { get; set; }
    [JsonPropertyName("median")] public double Median { get; set; }
    [JsonPropertyName("p10")]    public double P10    { get; set; }
    [JsonPropertyName("p90")]    public double P90    { get; set; }
}

public sealed class ElementQualityDto
{
    [JsonPropertyName("lengthDistribution")]      public LengthDistributionDto LengthDistribution      { get; set; } = new();
    [JsonPropertyName("shortElementCount")]        public int                   ShortElementCount        { get; set; }
    [JsonPropertyName("shortElementThresholdMm")]  public double                ShortElementThresholdMm  { get; set; }
    [JsonPropertyName("aspectRatioOk")]            public bool                  AspectRatioOk            { get; set; } = true;
}
```

`HealthMetricsDto` 에 필드 추가:

```csharp
[JsonPropertyName("elementQuality")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public ElementQualityDto? ElementQuality { get; set; }
```

`FeModelJsonContext` 에 `[JsonSerializable]` 추가 (LengthDistributionDto, ElementQualityDto).

- [ ] **Step 6-4: `HealthMetricsAnalyzer` 에 `ComputeElementQuality` 추가**

```csharp
public static HealthMetricsDto Analyze(
    FeModel model,
    IReadOnlyList<Diagnostic>? diagnostics = null,
    Tolerances? tolerances = null)
{
    var tol          = tolerances ?? new Tolerances();
    var connectivity = ConnectivityAnalyzer.Analyze(model);

    return new HealthMetricsDto
    {
        Totals           = ComputeTotals(model),
        Issues           = ComputeIssues(model, tol, connectivity),
        DiagnosticCounts = ComputeDiagnosticCounts(diagnostics),
        ElementQuality   = ComputeElementQuality(model, tol),
    };
}

private static ElementQualityDto? ComputeElementQuality(FeModel model, Tolerances tol)
{
    if (model.Elements.Count == 0) return null;

    var nodeById = model.Nodes.ToDictionary(n => n.Id);
    var lengths  = new List<double>(model.Elements.Count);

    foreach (var e in model.Elements)
    {
        if (nodeById.TryGetValue(e.StartNodeId, out var sn) &&
            nodeById.TryGetValue(e.EndNodeId,   out var en))
            lengths.Add(sn.Position.DistanceTo(en.Position));
    }

    if (lengths.Count == 0) return null;
    lengths.Sort();

    double sum  = lengths.Sum();
    double mean = sum / lengths.Count;
    double min  = lengths[0];
    double max  = lengths[^1];
    double med  = Percentile(lengths, 0.50);
    double p10  = Percentile(lengths, 0.10);
    double p90  = Percentile(lengths, 0.90);
    int shortCount = lengths.Count(l => l < tol.ShortElemMinMm);

    return new ElementQualityDto
    {
        LengthDistribution = new LengthDistributionDto
        {
            Min    = Math.Round(min,  3),
            Max    = Math.Round(max,  3),
            Mean   = Math.Round(mean, 3),
            Median = Math.Round(med,  3),
            P10    = Math.Round(p10,  3),
            P90    = Math.Round(p90,  3),
        },
        ShortElementCount       = shortCount,
        ShortElementThresholdMm = tol.ShortElemMinMm,
        AspectRatioOk           = true,
    };
}

private static double Percentile(List<double> sorted, double p)
{
    if (sorted.Count == 1) return sorted[0];
    double idx = p * (sorted.Count - 1);
    int lo = (int)idx;
    int hi = Math.Min(lo + 1, sorted.Count - 1);
    return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
}
```

- [ ] **Step 6-5: 빌드 + 테스트 통과 + 커밋**

```
dotnet build HiTessModelBuilder.sln && dotnet test HiTessModelBuilder.sln
git add src/Cmb.Core/Serialization/FeModelJson.cs src/Cmb.Pipeline/Analysis/HealthMetricsAnalyzer.cs tests/Cmb.Pipeline.Tests/Analysis/HealthMetricsAnalyzerTests.cs
git commit -m "✨ feat: add elementQuality distribution stats to healthMetrics"
```

---

## Task 7: `validationResult` (FinalValidation 스테이지 전용)

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (ValidationResultDto + ValidationCheckDto 신규, FeModelDto 필드 추가, ToJson 파라미터)
- Modify: `src/Cmb.Cli/Program.cs` (FinalValidation 시 validationResult 조립 + 전달)

> validationResult는 ToJson 파라미터로 주입. Program.cs 에서 stageName == "FinalValidation" 일 때만 조립.

- [ ] **Step 7-1: 신규 DTO 추가**

```csharp
public sealed class ValidationCheckDto
{
    [JsonPropertyName("check")]  public string Check  { get; set; } = "";
    [JsonPropertyName("passed")] public bool   Passed { get; set; }
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

public sealed class ValidationResultDto
{
    [JsonPropertyName("passed")]       public bool   Passed       { get; set; }
    [JsonPropertyName("errorCount")]   public int    ErrorCount   { get; set; }
    [JsonPropertyName("warningCount")] public int    WarningCount { get; set; }
    [JsonPropertyName("summary")]      public string Summary      { get; set; } = "";
    [JsonPropertyName("checklist")]    public List<ValidationCheckDto> Checklist { get; set; } = [];
}
```

`FeModelDto` 에 추가:

```csharp
[JsonPropertyName("validationResult")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public ValidationResultDto? ValidationResult { get; set; }
```

`FeModelJsonContext` 에 `[JsonSerializable]` 추가.

- [ ] **Step 7-2: `ToJson()` 에 `validationResult` 파라미터 추가**

```csharp
public static string ToJson(
    this FeModel model,
    ...
    ValidationResultDto? validationResult = null)
{
    var dto = ToDto(...);
    dto.ValidationResult = validationResult;
    ...
}
```

- [ ] **Step 7-3: `Program.cs` 에서 FinalValidation 후 조립**

`onStageComplete` 람다 내:

```csharp
ValidationResultDto? validationResult = null;
if (stageName == "FinalValidation")
{
    int errorCount   = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    int warningCount = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
    bool passed      = errorCount == 0;

    var connectivity2 = connectivity; // 이미 위에서 계산됨
    bool singleGroup  = connectivity2.GroupCount <= 1;
    bool noOrphan     = healthMetrics.Issues.OrphanNodes == 0;
    bool noShort      = healthMetrics.Issues.ShortElements == 0;
    int boundaryCount = m.Nodes.Count(n => n.HasTag(NodeTags.Boundary));

    validationResult = new ValidationResultDto
    {
        Passed       = passed,
        ErrorCount   = errorCount,
        WarningCount = warningCount,
        Summary      = passed
            ? $"모델이 유효합니다. 경고 {warningCount}개."
            : $"모델에 오류 {errorCount}개가 있습니다.",
        Checklist    =
        [
            new() { Check = "connectivity",      Passed = singleGroup,         Detail = singleGroup ? "단일 연결 그룹" : $"{connectivity2.GroupCount}개 그룹 분리" },
            new() { Check = "noZeroLength",       Passed = noShort,             Detail = noShort ? "길이=0 요소 없음" : $"{healthMetrics.Issues.ShortElements}개 짧은 요소" },
            new() { Check = "noOrphanNodes",      Passed = noOrphan,            Detail = noOrphan ? "고립 노드 없음" : $"{healthMetrics.Issues.OrphanNodes}개 고립 노드" },
            new() { Check = "boundaryCondition",  Passed = boundaryCount > 0,   Detail = $"경계조건 노드 {boundaryCount}개" },
        ],
    };
}

File.WriteAllText(jsonFile, m.ToJson(..., validationResult: validationResult));
```

- [ ] **Step 7-4: 빌드 + 전체 테스트 + 커밋**

```
dotnet build HiTessModelBuilder.sln && dotnet test HiTessModelBuilder.sln
git add src/Cmb.Core/Serialization/FeModelJson.cs src/Cmb.Cli/Program.cs
git commit -m "✨ feat: add validationResult to FinalValidation stage JSON"
```

---

## Task 8: `stageHistory` (마지막 스테이지 전용)

**Files:**
- Modify: `src/Cmb.Core/Serialization/FeModelJson.cs` (StageHistoryEntryDto 신규, FeModelDto 필드, ToJson 파라미터)
- Modify: `src/Cmb.Cli/Program.cs` (stageHistory 누적 + 마지막 스테이지 전달)

- [ ] **Step 8-1: `StageHistoryEntryDto` DTO 추가**

```csharp
public sealed class StageHistoryEntryDto
{
    [JsonPropertyName("stageIndex")]          public int    StageIndex          { get; set; }
    [JsonPropertyName("stage")]               public string Stage               { get; set; } = "";
    [JsonPropertyName("timestamp")]           public string Timestamp           { get; set; } = "";
    [JsonPropertyName("processingDurationMs")] public long  ProcessingDurationMs { get; set; }
    [JsonPropertyName("elementCount")]        public int    ElementCount        { get; set; }
    [JsonPropertyName("nodeCount")]           public int    NodeCount           { get; set; }
    [JsonPropertyName("issueCount")]          public StageHistoryIssueCountDto IssueCount { get; set; } = new();
}

public sealed class StageHistoryIssueCountDto
{
    [JsonPropertyName("error")]   public int Error   { get; set; }
    [JsonPropertyName("warning")] public int Warning { get; set; }
    [JsonPropertyName("info")]    public int Info    { get; set; }
}
```

`FeModelDto` 에 추가:

```csharp
[JsonPropertyName("stageHistory")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<StageHistoryEntryDto>? StageHistory { get; set; }
```

`FeModelJsonContext` 에 `[JsonSerializable]` 추가.

- [ ] **Step 8-2: `ToJson()` 파라미터 추가**

```csharp
public static string ToJson(
    this FeModel model,
    ...
    List<StageHistoryEntryDto>? stageHistory = null)
{
    var dto = ToDto(...);
    dto.StageHistory = stageHistory;
    ...
}
```

- [ ] **Step 8-3: `Program.cs` 에서 stageHistory 누적**

`BuildBuildFullCommand()` 내 람다 실행 전:

```csharp
var stageHistory = new List<StageHistoryEntryDto>();
```

`onStageComplete` 람다 내:

```csharp
stageHistory.Add(new StageHistoryEntryDto
{
    StageIndex           = idx,
    Stage                = stageName,
    Timestamp            = DateTime.UtcNow.ToString("o"),
    ProcessingDurationMs = durationMs,
    ElementCount         = m.Elements.Count,
    NodeCount            = m.Nodes.Count,
    IssueCount           = new StageHistoryIssueCountDto
    {
        Error   = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
        Warning = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
        Info    = stageDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Info),
    },
});

bool isLastStage = stages.LastOrDefault()?.Name == stageName
                   || (options.StopAfterStage is not null &&
                       string.Equals(stageName, options.StopAfterStage, StringComparison.OrdinalIgnoreCase));

File.WriteAllText(jsonFile, m.ToJson(...,
    stageHistory: isLastStage ? stageHistory : null));
```

> `stages` 배열은 `onStageComplete` 클로저 외부에 선언돼 있으므로 캡처 가능.

- [ ] **Step 8-4: 빌드 + 전체 테스트 + 커밋**

```
dotnet build HiTessModelBuilder.sln && dotnet test HiTessModelBuilder.sln
git add src/Cmb.Core/Serialization/FeModelJson.cs src/Cmb.Cli/Program.cs
git commit -m "✨ feat: add stageHistory to final stage JSON output"
```

---

## Task 9: Element ID 정책 감사 (spec #5-A/B/C/D)

**목적:** 파이프라인 전체에서 ID 재사용이 없는지, `parentElemId`가 올바르게 설정되는지 검증한다.

**Files:**
- Read: `src/Cmb.Core/Model/Context/FeModel.cs` (IdAllocator 동작 확인)
- Read: `src/Cmb.Pipeline/Stages/SplitByExistingNodesStage.cs` (분할 시 parentElemId 설정 확인)
- Read: `src/Cmb.Pipeline/Stages/NodeEquivalenceStage.cs` (노드 병합 시 요소 재생성 확인)
- Read: `src/Cmb.Pipeline/Stages/IntersectionStage.cs` (교차 분할 확인)

- [ ] **Step 9-1: `FeModel.AllocElemId()` 단조 증가 확인**

```
# FeModel.cs 읽기
Read src/Cmb.Core/Model/Context/FeModel.cs
```

`IdAllocator` 가 `++_nextId` 방식이면 ID 재사용 없음. `SyncIdCounters()` 가 `Max(existing ids) + 1` 로 초기화하는지 확인.

만약 `SyncIdCounters()` 가 ID를 재할당(reset)한다면 → **버그**: 삭제된 요소 ID가 재사용될 수 있다. 이 경우:
- `SyncIdCounters()` 가 이미 할당된 최댓값 이상만 초기화하도록 수정

- [ ] **Step 9-2: `SplitByExistingNodesStage` 분할 시 `parentElemId` 설정 확인**

분할로 생긴 요소들이 `new BeamElement(newId, ..., parentElemId: originalId)` 패턴으로 생성되는지 확인.
없으면 추가.

- [ ] **Step 9-3: `NodeEquivalenceStage`, `IntersectionStage` 요소 재생성 시 `parentElemId` 확인**

각 스테이지에서 요소를 교체(remove + create)할 때 `parentElemId` 를 이전 요소 ID로 설정하는지 확인.
없으면 추가.

- [ ] **Step 9-4: 빌드 + 전체 테스트**

```
dotnet build HiTessModelBuilder.sln && dotnet test HiTessModelBuilder.sln
```

- [ ] **Step 9-5: 필요한 수정이 있었으면 커밋**

```
git add <수정된 파일들>
git commit -m "🔧 fix: ensure parentElemId is set on split/recreated elements"
```

---

## Task 10: `CLAUDE.md` 업데이트

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 10-1: `schemaVersion` 및 JSON 스키마 섹션 업데이트**

`CLAUDE.md` 의 "Phase별 CLI 출력" 표에서:
- `schemaVersion="1.0"` → `"1.2"` 로 변경
- meta 필드 목록에 `stageIndex`, `processingDurationMs` 추가

- [ ] **Step 10-2: 커밋**

```
git add CLAUDE.md
git commit -m "📝 docs: update CLAUDE.md for schema v1.2 JSON fields"
```

---

## 검증 포인트 (dotnet run build-full 실행 후)

```
dotnet run --project src/Cmb.Cli -- build-full --input csv/01
```

확인 사항:
1. 모든 stage JSON 에 `"schemaVersion": "1.2"` 존재
2. `diagnostics` 배열이 비어있지 않은 스테이지 존재 (FinalValidation 등)
3. `elements[0].lengthMm` 이 양수
4. `meta.stageIndex` 가 1부터 순서대로 증가
5. `meta.processingDurationMs` 가 0 이상
6. `nodes[].tags` 에 FreeEnd/Orphan/Intersection 태그가 적절히 포함
7. `stageDeltas.netElementDelta` 가 스테이지 전후 요소 수 차이와 일치
8. `healthMetrics.elementQuality.lengthDistribution.min` > 0
9. 마지막 스테이지 JSON 에 `validationResult` 및 `stageHistory` 존재
10. `stageHistory` 길이 = 스테이지 수

---

## 구현 우선순위 요약

| Task | 스펙 항목 | 난이도 | 영향도 |
|------|----------|--------|--------|
| 1    | #3 diagnostics[] | 低 | 高 — 뷰어 클릭-강조 기능 |
| 2    | #4 lengthMm | 低 | 中 |
| 3    | #6 stageIndex + duration | 低 | 中 |
| 4    | #8 node tags | 低 | 中 |
| 5    | #9 stageDeltas | 中 | 低 |
| 6    | #11 elementQuality | 中 | 低 |
| 7    | #7 validationResult | 中 | 中 |
| 8    | #10 stageHistory | 低 | 低 |
| 9    | #5-A/B/C/D ID policy | 高 | 高 — Lifecycle 추적 전제 |
| 10   | docs CLAUDE.md | 低 | — |

> **spec #1 (healthMetrics) · #2 (connectivity)** 는 이미 `build-full` 에서 올바르게 출력되고 있어 별도 Task 없음. `build-raw` 는 파이프라인 전 단계이므로 analytics 미포함이 의도된 동작.
