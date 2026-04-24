namespace Cmb.Core.Model.Context;

public sealed class FeModel
{
    public string LengthUnit { get; } = "mm";
    public List<Model.Node> Nodes { get; } = [];
    public List<Model.BeamElement> Elements { get; } = [];
    public List<Model.RigidElement> Rigids { get; } = [];
    public List<Model.PointMass> PointMasses { get; } = [];
    public List<Model.BeamSection> Sections { get; } = [];
    public List<Model.Material> Materials { get; } = [];
    public List<Model.TraceEvent> TraceLog { get; } = [];

    public Model.Node? FindNode(int id) => Nodes.Find(n => n.Id == id);
    public Model.BeamSection? FindSection(int id) => Sections.Find(s => s.Id == id);
    public Model.Material? FindMaterial(int id) => Materials.Find(m => m.Id == id);

    public void AddTrace(Model.TraceEvent ev) => TraceLog.Add(ev);

    public void AddTrace(
        Model.TraceAction action,
        string stageName,
        int? elementId = null,
        int? nodeId = null,
        int? relatedElementId = null,
        int? relatedNodeId = null,
        string? note = null)
        => TraceLog.Add(new Model.TraceEvent(action, stageName, elementId, nodeId, relatedElementId, relatedNodeId, note));
}
