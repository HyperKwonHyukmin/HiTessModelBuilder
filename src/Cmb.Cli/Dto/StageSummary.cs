// M-1 리팩터: Program.cs 에서 분리한 StageSummary(00_StageSummary.json) DTO/직렬화. 동작 불변.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;

internal sealed class StageSummaryDocument
{
    [JsonPropertyName("_keys")] public Dictionary<string, string> Keys { get; set; } = StageSummaryJson.SchemaKeys;
    [JsonPropertyName("meta")] public StageSummaryMeta Meta { get; set; } = new();
    [JsonPropertyName("summary")] public StageSummaryTotals Summary { get; set; } = new();
    [JsonPropertyName("stages")] public List<StageSummaryRow> Stages { get; set; } = [];

    public void AddStage(StageSummaryRow row)
    {
        Stages.RemoveAll(s => s.StageIndex == row.StageIndex);
        Stages.Add(row);
        Stages.Sort((a, b) => a.StageIndex.CompareTo(b.StageIndex));
        StageSummaryJson.PopulateSummary(this);
    }

    public string ToJson() => JsonSerializer.Serialize(this, StageSummaryJson.Options);
}

internal sealed class StageSummaryMeta
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    [JsonPropertyName("unit")] public string Unit { get; set; } = "mm";
    [JsonPropertyName("purpose")] public string Purpose { get; set; } =
        "Phase-level algorithm summary index. Detailed FEM state remains in each phase JSON.";
}

internal sealed class StageSummaryTotals
{
    [JsonPropertyName("stageCount")] public int StageCount { get; set; }
    [JsonPropertyName("firstStage")] public string FirstStage { get; set; } = "";
    [JsonPropertyName("lastStage")] public string LastStage { get; set; } = "";
    [JsonPropertyName("finalNodeCount")] public int FinalNodeCount { get; set; }
    [JsonPropertyName("finalElementCount")] public int FinalElementCount { get; set; }
    [JsonPropertyName("finalRigidCount")] public int FinalRigidCount { get; set; }
    [JsonPropertyName("finalPointMassCount")] public int FinalPointMassCount { get; set; }
    [JsonPropertyName("totalErrors")] public int TotalErrors { get; set; }
    [JsonPropertyName("totalWarnings")] public int TotalWarnings { get; set; }
    [JsonPropertyName("totalInfos")] public int TotalInfos { get; set; }

    [JsonPropertyName("massProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StageSummaryMassProperties? MassProperties { get; set; }

    /// <summary>
    /// 배관 내부 유체를 순수 강재 밀도(7.85e-9 tonne/mm³)로 대체해 재계산한 질량/무게중심.
    /// Studio 는 로드 직후 이 상태를 기본으로 표시하므로, WorkBench 화면과 값이 일치하도록 함께 저장한다.
    /// </summary>
    [JsonPropertyName("massPropertiesFluidEmpty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StageSummaryMassProperties? MassPropertiesFluidEmpty { get; set; }
}

internal sealed class StageSummaryMassProperties
{
    [JsonPropertyName("totalMassTon")] public double TotalMassTon { get; set; }
    [JsonPropertyName("beamMassTon")] public double BeamMassTon { get; set; }
    [JsonPropertyName("pointMassTon")] public double PointMassTon { get; set; }
    [JsonPropertyName("centerOfGravityMm")] public double[] CenterOfGravityMm { get; set; } = [0, 0, 0];
}

internal sealed class StageSummaryRow
{
    [JsonPropertyName("stageIndex")] public int StageIndex { get; set; }
    [JsonPropertyName("stageName")] public string StageName { get; set; } = "";
    [JsonPropertyName("jsonFile")] public string JsonFile { get; set; } = "";
    [JsonPropertyName("bdfFile")] public string BdfFile { get; set; } = "";
    [JsonPropertyName("processingDurationMs")] public long ProcessingDurationMs { get; set; }
    [JsonPropertyName("counts")] public StageSummaryCounts Counts { get; set; } = new();
    [JsonPropertyName("delta")] public StageSummaryDelta Delta { get; set; } = new();
    [JsonPropertyName("connectivity")] public StageSummaryConnectivity Connectivity { get; set; } = new();
    [JsonPropertyName("health")] public StageSummaryHealth Health { get; set; } = new();
    [JsonPropertyName("diagnostics")] public StageSummaryDiagnostics Diagnostics { get; set; } = new();
}

internal sealed class StageSummaryCounts
{
    [JsonPropertyName("nodes")] public int Nodes { get; set; }
    [JsonPropertyName("elements")] public int Elements { get; set; }
    [JsonPropertyName("rigids")] public int Rigids { get; set; }
    [JsonPropertyName("pointMasses")] public int PointMasses { get; set; }
}

internal sealed class StageSummaryDelta
{
    [JsonPropertyName("elementsCreated")] public int ElementsCreated { get; set; }
    [JsonPropertyName("elementsRemoved")] public int ElementsRemoved { get; set; }
    [JsonPropertyName("elementsSplit")] public int ElementsSplit { get; set; }
    [JsonPropertyName("nodesMerged")] public int NodesMerged { get; set; }
    [JsonPropertyName("nodesMoved")] public int NodesMoved { get; set; }
    [JsonPropertyName("netElementDelta")] public int NetElementDelta { get; set; }
    [JsonPropertyName("netNodeDelta")] public int NetNodeDelta { get; set; }
}

internal sealed class StageSummaryConnectivity
{
    [JsonPropertyName("groupCount")] public int GroupCount { get; set; }
    [JsonPropertyName("largestGroupElementCount")] public int LargestGroupElementCount { get; set; }
    [JsonPropertyName("largestGroupNodeRatio")] public double LargestGroupNodeRatio { get; set; }
    [JsonPropertyName("isolatedNodeCount")] public int IsolatedNodeCount { get; set; }
}

internal sealed class StageSummaryHealth
{
    [JsonPropertyName("freeEndCount")] public int FreeEndCount { get; set; }
    [JsonPropertyName("orphanNodeCount")] public int OrphanNodeCount { get; set; }
    [JsonPropertyName("shortElementCount")] public int ShortElementCount { get; set; }
    [JsonPropertyName("unresolvedUboltCount")] public int UnresolvedUboltCount { get; set; }
    [JsonPropertyName("disconnectedGroupCount")] public int DisconnectedGroupCount { get; set; }
}

internal sealed class StageSummaryDiagnostics
{
    [JsonPropertyName("error")] public int Error { get; set; }
    [JsonPropertyName("warning")] public int Warning { get; set; }
    [JsonPropertyName("info")] public int Info { get; set; }
}

internal static class StageSummaryJson
{
    internal static readonly Dictionary<string, string> SchemaKeys = new()
    {
        ["meta"] = "단계 요약 메타 정보 (schemaVersion, timestamp, unit, purpose)",
        ["summary"] = "전체 phase 실행 요약 (stageCount, firstStage, lastStage, final counts, diagnostic totals, massProperties, massPropertiesFluidEmpty)",
        ["summary.massProperties"] = "최종 모델 전체 질량/무게중심(원본 밀도, 배관 유체 포함) — totalMassTon(beam+point 합), beamMassTon, pointMassTon, centerOfGravityMm[x,y,z]",
        ["summary.massPropertiesFluidEmpty"] = "배관 부재 밀도를 순수 강재(7.85e-9 tonne/mm³)로 대체해 재계산한 질량/무게중심. Studio 로드 직후 기본 표시값과 동일 (fields = massProperties와 동일)",
        ["stages"] = "phase별 FEM 상태 및 변화량 요약 배열. 상세 노드/요소 목록은 각 phase JSON에서 확인",
        ["stages.stageIndex"] = "phase 출력 번호. 파일명 접두 번호와 동일",
        ["stages.stageName"] = "phase 이름",
        ["stages.jsonFile"] = "해당 phase 상세 JSON 파일명",
        ["stages.bdfFile"] = "해당 phase BDF 파일명",
        ["stages.processingDurationMs"] = "phase boundary를 생성한 마지막 내부 stage 처리 시간(ms)",
        ["stages.counts"] = "phase 완료 시점의 nodes/elements/rigids/pointMasses 개수",
        ["stages.delta"] = "이전 phase 대비 created/removed/split/merged/moved 및 net 변화량",
        ["stages.connectivity"] = "phase 완료 시점 연결성 요약",
        ["stages.health"] = "phase 완료 시점 주요 건전성 지표",
        ["stages.diagnostics"] = "phase JSON에 포함된 진단 severity별 개수",
    };

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static StageSummaryRow FromPhase(
        int phaseIndex,
        string phaseName,
        string jsonFile,
        string bdfFile,
        long processingDurationMs,
        FeModel model,
        StageDeltasDto delta,
        ConnectivityDto connectivity,
        HealthMetricsDto healthMetrics,
        IReadOnlyList<DiagnosticDto> diagnostics)
    {
        var counts = CountStageDiagnostics(diagnostics);
        return new StageSummaryRow
        {
            StageIndex = phaseIndex,
            StageName = phaseName,
            JsonFile = jsonFile,
            BdfFile = bdfFile,
            ProcessingDurationMs = processingDurationMs,
            Counts = new StageSummaryCounts
            {
                Nodes = model.Nodes.Count,
                Elements = model.Elements.Count,
                Rigids = model.Rigids.Count,
                PointMasses = model.PointMasses.Count,
            },
            Delta = new StageSummaryDelta
            {
                ElementsCreated = delta.ElementsCreated,
                ElementsRemoved = delta.ElementsRemoved,
                ElementsSplit = delta.ElementsSplit,
                NodesMerged = delta.NodesMerged,
                NodesMoved = delta.NodesMoved,
                NetElementDelta = delta.NetElementDelta,
                NetNodeDelta = delta.NetNodeDelta,
            },
            Connectivity = new StageSummaryConnectivity
            {
                GroupCount = connectivity.GroupCount,
                LargestGroupElementCount = connectivity.LargestGroupElementCount,
                LargestGroupNodeRatio = connectivity.LargestGroupNodeRatio,
                IsolatedNodeCount = connectivity.IsolatedNodeCount,
            },
            Health = new StageSummaryHealth
            {
                FreeEndCount = healthMetrics.Issues.FreeEndNodes,
                OrphanNodeCount = healthMetrics.Issues.OrphanNodes,
                ShortElementCount = healthMetrics.Issues.ShortElements,
                UnresolvedUboltCount = healthMetrics.Issues.UnresolvedUbolts,
                DisconnectedGroupCount = healthMetrics.Issues.DisconnectedGroups,
            },
            Diagnostics = new StageSummaryDiagnostics
            {
                Error = counts.Error,
                Warning = counts.Warning,
                Info = counts.Info,
            },
        };
    }

    public static void PopulateSummary(StageSummaryDocument doc)
    {
        doc.Summary.StageCount = doc.Stages.Count;
        doc.Summary.FirstStage = doc.Stages.FirstOrDefault()?.StageName ?? "";
        doc.Summary.LastStage = doc.Stages.LastOrDefault()?.StageName ?? "";

        var final = doc.Stages.LastOrDefault();
        doc.Summary.FinalNodeCount = final?.Counts.Nodes ?? 0;
        doc.Summary.FinalElementCount = final?.Counts.Elements ?? 0;
        doc.Summary.FinalRigidCount = final?.Counts.Rigids ?? 0;
        doc.Summary.FinalPointMassCount = final?.Counts.PointMasses ?? 0;

        // Pipeline success/failure should reflect the final exported model.
        // Earlier phase errors can be transient health findings that later stages repair.
        doc.Summary.TotalErrors = final?.Diagnostics.Error ?? 0;
        doc.Summary.TotalWarnings = doc.Stages.Sum(s => s.Diagnostics.Warning);
        doc.Summary.TotalInfos = doc.Stages.Sum(s => s.Diagnostics.Info);
    }

    private static StageSummaryDiagnostics CountStageDiagnostics(IReadOnlyList<DiagnosticDto> diagnostics)
    {
        var counts = new StageSummaryDiagnostics();
        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Severity.ToLowerInvariant())
            {
                case "error": counts.Error++; break;
                case "warning": counts.Warning++; break;
                case "info": counts.Info++; break;
            }
        }
        return counts;
    }
}
