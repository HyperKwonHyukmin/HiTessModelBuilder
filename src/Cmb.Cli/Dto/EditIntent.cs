// M-1 리팩터: Program.cs 에서 분리한 편집 인텐트(*_edit.json) DTO/직렬화/트레이스. 동작 불변.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record StageMetaSnapshot(
    string Phase,
    string StageName,
    int StageIndex,
    string Timestamp);

internal sealed class EditIntentDocument
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("stageRef")] public EditStageRef StageRef { get; set; } = new();
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("createdBy")] public string CreatedBy { get; set; } = "";
    [JsonPropertyName("intents")] public List<EditIntent> Intents { get; set; } = [];
}

internal sealed class EditStageRef
{
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("stageName")] public string StageName { get; set; } = "";
    [JsonPropertyName("sourceTimestamp")] public string SourceTimestamp { get; set; } = "";
}

internal sealed class EditIntent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement Params { get; set; }
    [JsonPropertyName("validation")] public EditIntentValidation Validation { get; set; } = new();
}

internal sealed class EditIntentValidation
{
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
}

internal sealed class EditApplyTraceDocument
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("intentFile")] public string IntentFile { get; set; } = "";
    [JsonPropertyName("baseStage")] public string BaseStage { get; set; } = "";
    [JsonPropertyName("intents")] public List<EditIntent> Intents { get; set; } = [];
    [JsonPropertyName("operations")] public List<Dictionary<string, object?>> Operations { get; set; } = [];
}

internal static class EditTrace
{
    public static Dictionary<string, object?> Operation(
        string code,
        string level,
        string? intentId,
        string? kind = null,
        string? details = null,
        int? rbeId = null,
        string? reason = null,
        IReadOnlyList<int>? removedGM = null,
        int? massId = null,
        int? nodeId = null,
        string? card = null,
        int? removedGID = null)
    {
        var op = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["level"] = level,
        };
        if (!string.IsNullOrWhiteSpace(intentId)) op["intentId"] = intentId;
        if (!string.IsNullOrWhiteSpace(kind)) op["kind"] = kind;
        if (!string.IsNullOrWhiteSpace(details)) op["details"] = details;
        if (rbeId.HasValue) op["rbeId"] = rbeId.Value;
        if (!string.IsNullOrWhiteSpace(reason)) op["reason"] = reason;
        if (removedGM is { Count: > 0 }) op["removedGM"] = removedGM;
        if (massId.HasValue) op["massId"] = massId.Value;
        if (nodeId.HasValue) op["nodeId"] = nodeId.Value;
        if (!string.IsNullOrWhiteSpace(card)) op["card"] = card;
        if (removedGID.HasValue) op["removedGID"] = removedGID.Value;
        return op;
    }
}

internal static class EditIntentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
