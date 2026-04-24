using System.Text.Json;
using System.Text.Json.Serialization;
using Cmb.Core.Model.Raw;

namespace Cmb.Core.Serialization;

// ── JSON DTOs ─────────────────────────────────────────────────────────────────

public sealed class RawDumpDto
{
    [JsonPropertyName("meta")]   public RawDumpMetaDto Meta { get; set; } = new();
    [JsonPropertyName("beams")]  public List<RawBeamDto> Beams { get; set; } = [];
    [JsonPropertyName("pipes")]  public List<RawPipeDto> Pipes { get; set; } = [];
    [JsonPropertyName("equips")] public List<RawEquipDto> Equips { get; set; } = [];
    [JsonPropertyName("skips")]  public List<RawSkipDto> Skips { get; set; } = [];
}

public sealed class RawDumpMetaDto
{
    [JsonPropertyName("inputFolder")] public string InputFolder { get; set; } = "";
    [JsonPropertyName("timestamp")]   public string Timestamp { get; set; } = "";
    [JsonPropertyName("beamCount")]   public int BeamCount { get; set; }
    [JsonPropertyName("pipeCount")]   public int PipeCount { get; set; }
    [JsonPropertyName("equipCount")]  public int EquipCount { get; set; }
    [JsonPropertyName("skipCount")]   public int SkipCount { get; set; }
}

public sealed class RawBeamDto
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("sectionType")] public string SectionType { get; set; } = "";
    [JsonPropertyName("sizeRaw")]     public string SizeRaw { get; set; } = "";
    [JsonPropertyName("dims")]        public double[] Dims { get; set; } = [];
    [JsonPropertyName("startPos")]    public double[] StartPos { get; set; } = [];
    [JsonPropertyName("endPos")]      public double[] EndPos { get; set; } = [];
    [JsonPropertyName("ori")]         public double[] Ori { get; set; } = [];
    [JsonPropertyName("weld")]        public string Weld { get; set; } = "";
}

public sealed class RawPipeDto
{
    [JsonPropertyName("name")]     public string Name { get; set; } = "";
    [JsonPropertyName("type")]     public string Type { get; set; } = "";
    [JsonPropertyName("branch")]   public string Branch { get; set; } = "";
    [JsonPropertyName("pos")]      public double[] Pos { get; set; } = [];
    [JsonPropertyName("aPos")]     public double[] APos { get; set; } = [];
    [JsonPropertyName("lPos")]     public double[] LPos { get; set; } = [];
    [JsonPropertyName("normal")]   public double[] Normal { get; set; } = [];
    [JsonPropertyName("interPos")] public double[]? InterPos { get; set; }
    [JsonPropertyName("p3Pos")]    public double[]? P3Pos { get; set; }
    [JsonPropertyName("rest")]     public string? Rest { get; set; }
    [JsonPropertyName("outDia")]   public double OutDia { get; set; }
    [JsonPropertyName("thick")]    public double Thick { get; set; }
    [JsonPropertyName("outDia2")]  public double OutDia2 { get; set; }
    [JsonPropertyName("thick2")]   public double Thick2 { get; set; }
    [JsonPropertyName("mass")]     public double Mass { get; set; }
    [JsonPropertyName("remark")]   public string? Remark { get; set; }
}

public sealed class RawEquipDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("pos")]  public double[] Pos { get; set; } = [];
    [JsonPropertyName("cog")]  public double[] Cog { get; set; } = [];
    [JsonPropertyName("mass")] public double Mass { get; set; }
}

public sealed class RawSkipDto
{
    [JsonPropertyName("kind")]       public string Kind { get; set; } = "";
    [JsonPropertyName("lineNumber")] public int LineNumber { get; set; }
    [JsonPropertyName("name")]       public string Name { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason { get; set; } = "";
}

// ── Source Generator Context ──────────────────────────────────────────────────

[JsonSerializable(typeof(RawDumpDto))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class RawDesignDataJsonContext : JsonSerializerContext { }

// ── Serialization helper ──────────────────────────────────────────────────────

public static class RawDesignDataJson
{
    public static string ToRawJson(this RawDesignData data, string inputFolder = "")
    {
        var dto = new RawDumpDto
        {
            Meta = new RawDumpMetaDto
            {
                InputFolder = inputFolder,
                Timestamp   = DateTime.UtcNow.ToString("o"),
                BeamCount   = data.Beams.Count,
                PipeCount   = data.Pipes.Count,
                EquipCount  = data.Equips.Count,
                SkipCount   = data.Skips.Count,
            },
            Beams  = data.Beams.Select(b => new RawBeamDto
            {
                Name        = b.Name,
                SectionType = b.SectionType,
                SizeRaw     = b.SizeRaw,
                Dims        = b.Dims,
                StartPos    = b.StartPos,
                EndPos      = b.EndPos,
                Ori         = b.Ori,
                Weld        = b.Weld,
            }).ToList(),
            Pipes  = data.Pipes.Select(p => new RawPipeDto
            {
                Name     = p.Name,
                Type     = p.Type,
                Branch   = p.Branch,
                Pos      = p.Pos,
                APos     = p.APos,
                LPos     = p.LPos,
                Normal   = p.Normal,
                InterPos = p.InterPos,
                P3Pos    = p.P3Pos,
                Rest     = p.Rest,
                OutDia   = p.OutDia,
                Thick    = p.Thick,
                OutDia2  = p.OutDia2,
                Thick2   = p.Thick2,
                Mass     = p.Mass,
                Remark   = p.Remark,
            }).ToList(),
            Equips = data.Equips.Select(e => new RawEquipDto
            {
                Name = e.Name,
                Pos  = e.Pos,
                Cog  = e.Cog,
                Mass = e.Mass,
            }).ToList(),
            Skips  = data.Skips.Select(s => new RawSkipDto
            {
                Kind       = s.Kind,
                LineNumber = s.LineNumber,
                Name       = s.Name,
                Reason     = s.Reason,
            }).ToList(),
        };

        return JsonSerializer.Serialize(dto, RawDesignDataJsonContext.Default.RawDumpDto);
    }
}
