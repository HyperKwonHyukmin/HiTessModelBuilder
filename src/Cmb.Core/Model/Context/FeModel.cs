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

    private int _nextNodeId = 1;
    private int _nextElemId = 1;

    /// <summary>단조 증가 노드 ID. 삭제 후에도 이전 ID를 재사용하지 않습니다.</summary>
    public int AllocNodeId() => _nextNodeId++;

    /// <summary>단조 증가 요소 ID (BeamElement / RigidElement / PointMass 공유). 삭제 후에도 이전 ID를 재사용하지 않습니다.</summary>
    public int AllocElemId() => _nextElemId++;

    /// <summary>FeModelBuilder 빌드 완료 후 한 번 호출하여 카운터를 현재 최대 ID 기준으로 맞춥니다.</summary>
    public void SyncIdCounters()
    {
        if (Nodes.Count > 0)
            _nextNodeId = Math.Max(_nextNodeId, Nodes.Max(n => n.Id) + 1);

        int maxElem = 0;
        if (Elements.Count > 0)    maxElem = Math.Max(maxElem, Elements.Max(e => e.Id));
        if (Rigids.Count > 0)      maxElem = Math.Max(maxElem, Rigids.Max(r => r.Id));
        if (PointMasses.Count > 0) maxElem = Math.Max(maxElem, PointMasses.Max(pm => pm.Id));
        _nextElemId = Math.Max(_nextElemId, maxElem + 1);
    }

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
