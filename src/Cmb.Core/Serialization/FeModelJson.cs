using System.Text.Json;
using System.Text.Json.Serialization;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;

namespace Cmb.Core.Serialization;

// ── JSON DTOs (matches PRD section 6.1 schema) ───────────────────────────────

public sealed class FeModelDto
{
    [JsonPropertyName("meta")]        public MetaDto Meta { get; set; } = new();
    [JsonPropertyName("nodes")]       public List<NodeDto> Nodes { get; set; } = [];
    [JsonPropertyName("elements")]    public List<BeamElementDto> Elements { get; set; } = [];
    [JsonPropertyName("rigids")]      public List<RigidElementDto> Rigids { get; set; } = [];
    [JsonPropertyName("properties")]  public List<BeamSectionDto> Properties { get; set; } = [];
    [JsonPropertyName("materials")]   public List<MaterialDto> Materials { get; set; } = [];
    [JsonPropertyName("pointMasses")] public List<PointMassDto> PointMasses { get; set; } = [];
    [JsonPropertyName("diagnostics")] public List<DiagnosticDto> Diagnostics { get; set; } = [];
    [JsonPropertyName("trace")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TraceEventDto>? Trace { get; set; }
}

public sealed class MetaDto
{
    [JsonPropertyName("phase")]         public string Phase { get; set; } = "";
    [JsonPropertyName("stageName")]     public string StageName { get; set; } = "";
    [JsonPropertyName("timestamp")]     public string Timestamp { get; set; } = "";
    [JsonPropertyName("unit")]          public string Unit { get; set; } = "mm";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "1.1";
}

public sealed class NodeDto
{
    [JsonPropertyName("id")]   public int Id { get; set; }
    [JsonPropertyName("x")]    public double X { get; set; }
    [JsonPropertyName("y")]    public double Y { get; set; }
    [JsonPropertyName("z")]    public double Z { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
}

public sealed class BeamElementDto
{
    [JsonPropertyName("id")]                                                        public int Id { get; set; }
    [JsonPropertyName("type")]                                                      public string Type { get; set; } = "BEAM";
    [JsonPropertyName("startNode")]                                                 public int StartNode { get; set; }
    [JsonPropertyName("endNode")]                                                   public int EndNode { get; set; }
    [JsonPropertyName("propertyId")]                                                public int PropertyId { get; set; }
    [JsonPropertyName("category")]                                                  public string Category { get; set; } = "";
    [JsonPropertyName("orientation")]                                               public double[] Orientation { get; set; } = [0, 0, 1];
    [JsonPropertyName("sourceName")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceName { get; set; }
    [JsonPropertyName("parentElemId")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ParentElemId { get; set; }
}

public sealed class RigidElementDto
{
    [JsonPropertyName("id")]                                                          public int Id { get; set; }
    [JsonPropertyName("independentNode")]                                             public int IndependentNode { get; set; }
    [JsonPropertyName("dependentNodes")]                                              public List<int> DependentNodes { get; set; } = [];
    [JsonPropertyName("remark")]                                                      public string Remark { get; set; } = "";
    [JsonPropertyName("sourceName")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceName { get; set; }
}

public sealed class BeamSectionDto
{
    [JsonPropertyName("id")]         public int Id { get; set; }
    [JsonPropertyName("kind")]       public string Kind { get; set; } = "";
    [JsonPropertyName("dims")]       public double[] Dims { get; set; } = [];
    [JsonPropertyName("materialId")] public int MaterialId { get; set; }
}

public sealed class MaterialDto
{
    [JsonPropertyName("id")]   public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("E")]    public double E { get; set; }
    [JsonPropertyName("nu")]   public double Nu { get; set; }
    [JsonPropertyName("rho")]  public double Rho { get; set; }
}

public sealed class PointMassDto
{
    [JsonPropertyName("id")]                                                          public int Id { get; set; }
    [JsonPropertyName("nodeId")]                                                      public int NodeId { get; set; }
    [JsonPropertyName("mass")]                                                        public double Mass { get; set; }
    [JsonPropertyName("sourceName")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceName { get; set; }
}

public sealed class TraceEventDto
{
    [JsonPropertyName("action")]                                                         public string Action { get; set; } = "";
    [JsonPropertyName("stage")]                                                          public string Stage { get; set; } = "";
    [JsonPropertyName("elemId")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ElemId { get; set; }
    [JsonPropertyName("nodeId")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NodeId { get; set; }
    [JsonPropertyName("relatedElemId")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RelatedElemId { get; set; }
    [JsonPropertyName("relatedNodeId")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RelatedNodeId { get; set; }
    [JsonPropertyName("note")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
}

public sealed class DiagnosticDto
{
    [JsonPropertyName("severity")] public string Severity { get; set; } = "";
    [JsonPropertyName("code")]     public string Code { get; set; } = "";
    [JsonPropertyName("msg")]      public string Msg { get; set; } = "";
    [JsonPropertyName("elemId")]   public int? ElemId { get; set; }
    [JsonPropertyName("nodeId")]   public int? NodeId { get; set; }
}

// ── Source Generator Context ──────────────────────────────────────────────────

[JsonSerializable(typeof(FeModelDto))]
[JsonSerializable(typeof(TraceEventDto))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class FeModelJsonContext : JsonSerializerContext { }

// ── Serialization helpers ─────────────────────────────────────────────────────

public static class FeModelJson
{
    public static string ToJson(
        this FeModel model,
        string phase = "",
        string stageName = "")
    {
        var dto = ToDto(model, phase, stageName);
        return JsonSerializer.Serialize(dto, FeModelJsonContext.Default.FeModelDto);
    }

    public static FeModel FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize(json, FeModelJsonContext.Default.FeModelDto)
                  ?? throw new JsonException("Deserialized FeModelDto is null.");
        return FromDto(dto);
    }

    // ── FeModel → DTO ─────────────────────────────────────────────────────────

    private static FeModelDto ToDto(FeModel model, string phase, string stageName) => new()
    {
        Meta = new MetaDto
        {
            Phase = phase,
            StageName = stageName,
            Timestamp = DateTime.UtcNow.ToString("o"),
            Unit = model.LengthUnit,
            SchemaVersion = "1.1",
        },
        Nodes = model.Nodes.Select(n => new NodeDto
        {
            Id = n.Id,
            X = n.Position.X,
            Y = n.Position.Y,
            Z = n.Position.Z,
            Tags = FlagsToStringList(n.Tags),
        }).ToList(),
        Elements = model.Elements.Select(e => new BeamElementDto
        {
            Id = e.Id,
            Type = "BEAM",
            StartNode = e.StartNodeId,
            EndNode = e.EndNodeId,
            PropertyId = e.PropertyId,
            Category = e.Category.ToString(),
            Orientation = [e.Orientation.X, e.Orientation.Y, e.Orientation.Z],
            SourceName = e.SourceName,
            ParentElemId = e.ParentElementId,
        }).ToList(),
        Rigids = model.Rigids.Select(r => new RigidElementDto
        {
            Id = r.Id,
            IndependentNode = r.IndependentNodeId,
            DependentNodes = r.DependentNodeIds.ToList(),
            Remark = r.Remark,
            SourceName = r.SourceName,
        }).ToList(),
        Properties = model.Sections.Select(s => new BeamSectionDto
        {
            Id = s.Id,
            Kind = s.Kind.ToString(),
            Dims = s.Dims,
            MaterialId = s.MaterialId,
        }).ToList(),
        Materials = model.Materials.Select(m => new MaterialDto
        {
            Id = m.Id,
            Name = m.Name,
            E = m.E,
            Nu = m.Nu,
            Rho = m.Rho,
        }).ToList(),
        PointMasses = model.PointMasses.Select(p => new PointMassDto
        {
            Id = p.Id,
            NodeId = p.NodeId,
            Mass = p.Mass,
            SourceName = p.SourceName,
        }).ToList(),
        Trace = model.TraceLog.Count > 0
            ? model.TraceLog.Select(t => new TraceEventDto
            {
                Action = t.Action.ToString(),
                Stage = t.StageName,
                ElemId = t.ElementId,
                NodeId = t.NodeId,
                RelatedElemId = t.RelatedElementId,
                RelatedNodeId = t.RelatedNodeId,
                Note = t.Note,
            }).ToList()
            : null,
    };

    // ── DTO → FeModel ─────────────────────────────────────────────────────────

    private static FeModel FromDto(FeModelDto dto)
    {
        var model = new FeModel();

        foreach (var n in dto.Nodes)
        {
            var tags = StringListToFlags(n.Tags);
            model.Nodes.Add(new Model.Node(n.Id, new Geometry.Point3(n.X, n.Y, n.Z), tags));
        }

        foreach (var e in dto.Elements)
        {
            var cat = Enum.Parse<EntityCategory>(e.Category);
            var orient = e.Orientation.Length >= 3
                ? new Geometry.Vector3(e.Orientation[0], e.Orientation[1], e.Orientation[2])
                : Geometry.Vector3.UnitZ;
            model.Elements.Add(new Model.BeamElement(e.Id, e.StartNode, e.EndNode, e.PropertyId, cat, orient, e.SourceName, e.ParentElemId));
        }

        foreach (var r in dto.Rigids)
            model.Rigids.Add(new Model.RigidElement(r.Id, r.IndependentNode, r.DependentNodes, r.Remark, r.SourceName));

        foreach (var s in dto.Properties)
        {
            var kind = Enum.Parse<BeamSectionKind>(s.Kind);
            model.Sections.Add(new Model.BeamSection(s.Id, kind, s.Dims, s.MaterialId));
        }

        foreach (var m in dto.Materials)
            model.Materials.Add(new Model.Material(m.Id, m.Name, m.E, m.Nu, m.Rho));

        foreach (var p in dto.PointMasses)
            model.PointMasses.Add(new Model.PointMass(p.Id, p.NodeId, p.Mass, p.SourceName));

        if (dto.Trace is { Count: > 0 })
        {
            foreach (var t in dto.Trace)
            {
                if (Enum.TryParse<Model.TraceAction>(t.Action, out var action))
                    model.AddTrace(action, t.Stage, t.ElemId, t.NodeId, t.RelatedElemId, t.RelatedNodeId, t.Note);
            }
        }

        return model;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> FlagsToStringList(NodeTags tags)
    {
        var result = new List<string>();
        foreach (NodeTags flag in Enum.GetValues<NodeTags>())
        {
            if (flag == NodeTags.None) continue;
            if ((tags & flag) != 0)
                result.Add(flag.ToString());
        }
        return result;
    }

    private static NodeTags StringListToFlags(List<string> tags)
    {
        var result = NodeTags.None;
        foreach (var s in tags)
            if (Enum.TryParse<NodeTags>(s, out var flag))
                result |= flag;
        return result;
    }
}
