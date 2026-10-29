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

    /// <summary>
    /// 질량이 있는데 부착 노드를 못 찾아 모델에서 빠진 장비. 입력 감사가 그 CSV 행을 error / equipment_mass_not_attached 로
    /// 남기는 근거다 — trace 에만 남기면 감사엔 'converted' 로 보여 WorkBench 판정이 질량 손실을 못 잡는다(3529_m04 5행).
    /// </summary>
    public List<DroppedEquipmentMass> DroppedEquipment { get; } = [];

    /// <summary>
    /// 호선 번호(예: "3454"). BDF 머리 주석 <c>$ HiTessModelBuilder: HullNo=...</c> 과 JSON <c>meta.hullNo</c> 로 나간다.
    /// 해석에는 쓰이지 않고, 이후 권상·LogBook 단계가 이 BDF 의 호선을 알아내는 통로다. 없으면 null.
    /// </summary>
    public string? HullNo { get; set; }

    /// <summary>
    /// 호선 문자열 정규화 — 앞뒤 공백 제거·대문자. 영문·숫자·'-'·'_' 1~20자만 허용하고 그 밖은 null.
    /// BDF 주석 한 줄에 그대로 들어가므로 줄바꿈·'$'·비ASCII 를 막는다.
    /// </summary>
    public static string? NormalizeHullNo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();
        if (s.Length > 20) return null;
        foreach (var c in s)
            if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) return null;
        return s;
    }

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

    /// <summary>
    /// RBE2와 PointMass의 노드 참조를 일괄 재매핑한다.
    /// oldToRep[oldId] = newId 매핑. 키에 없는 ID는 그대로 유지.
    /// 재매핑 후 dep 리스트가 비어있는 RBE2는 삭제한다.
    /// </summary>
    /// <summary>
    /// 모델 전체를 pivot 기준으로 주축(axis: 'X'/'Y'/'Z') 회전한다(강체 회전).
    /// 노드 좌표와 빔 요소의 방향 벡터(CBEAM v-vector)를 함께 회전해 단면 방향을 보존한다.
    /// RBE2/PointMass 는 노드 참조만 가지므로 노드 회전으로 함께 이동한다.
    /// angleDeg 가 360의 배수면 no-op. 반환값은 회전된 노드 수.
    /// </summary>
    public int RotateAboutPivot(char axis, double angleDeg, Geometry.Point3 pivot)
    {
        double norm = ((angleDeg % 360.0) + 360.0) % 360.0;
        if (norm == 0.0)
            return 0;

        var rotatedNodes = new List<Model.Node>(Nodes.Count);
        foreach (var n in Nodes)
        {
            var p = Geometry.AxisRotation.RotatePoint(n.Position, axis, angleDeg, pivot);
            rotatedNodes.Add(new Model.Node(n.Id, p, n.Tags));
        }
        Nodes.Clear();
        Nodes.AddRange(rotatedNodes);

        var rotatedElems = new List<Model.BeamElement>(Elements.Count);
        foreach (var e in Elements)
        {
            var o = Geometry.AxisRotation.RotateVector(e.Orientation, axis, angleDeg);
            rotatedElems.Add(new Model.BeamElement(
                e.Id, e.StartNodeId, e.EndNodeId, e.PropertyId, e.Category, o, e.SourceName, e.ParentElementId));
        }
        Elements.Clear();
        Elements.AddRange(rotatedElems);

        return rotatedNodes.Count;
    }

    public void RemapNodeReferences(IDictionary<int, int> oldToRep)
    {
        int Map(int id) => oldToRep.TryGetValue(id, out var v) ? v : id;

        var newRigids = new List<Model.RigidElement>(Rigids.Count);
        foreach (var r in Rigids)
        {
            int newIndep = Map(r.IndependentNodeId);
            var newDeps  = r.DependentNodeIds.Select(Map);
            var rebuilt  = new Model.RigidElement(r.Id, newIndep, newDeps, r.Remark, r.SourceName, r.Cm, r.DetailRemark);
            if (rebuilt.DependentNodeIds.Count > 0)
                newRigids.Add(rebuilt);
        }
        Rigids.Clear();
        Rigids.AddRange(newRigids);

        var newPointMasses = new List<Model.PointMass>(PointMasses.Count);
        foreach (var pm in PointMasses)
        {
            int newNodeId = Map(pm.NodeId);
            var rebuilt = newNodeId != pm.NodeId
                ? new Model.PointMass(pm.Id, newNodeId, pm.Mass, pm.SourceName)
                : pm;
            newPointMasses.Add(rebuilt);
        }
        PointMasses.Clear();
        PointMasses.AddRange(newPointMasses);
    }
}

/// <summary>장비 질량 누락 1건 — CSV name, 운용 질량(kg), 사유.</summary>
public sealed record DroppedEquipmentMass(string Name, double MassKg, string Reason);
