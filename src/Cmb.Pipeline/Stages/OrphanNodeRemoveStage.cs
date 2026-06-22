using Cmb.Core.Model;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 어떤 BeamElement / RigidElement(indep+dep) 로도 연결되지 않는 노드(orphan)를 모두 제거한다.
/// PointMass(CONM2) 만 붙어있는 노드는 구조적으로 공중에 떠 있는 상태이므로
/// 해당 노드와 함께 PointMass 자체도 제거한다.
/// 각 삭제 노드/요소는 TraceAction.NodeRemoved / ElementRemoved 로 기록되어
/// JSON 의 trace 영역에서 lifecycle 추적 가능.
/// Boundary 태그가 있는 노드는 SPC seed 보존을 위해 제외.
/// </summary>
public sealed class OrphanNodeRemoveStage : IPipelineStage
{
    public string Name => "OrphanNodeRemove";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;

        // 구조적 연결: BeamElement 와 RigidElement(indep+dep) 만 인정
        // PointMass 는 구조 연결이 아닌 질량 부착이므로 제외
        var structurallyReferenced = new HashSet<int>(model.Nodes.Count);
        foreach (var e in model.Elements)
        {
            structurallyReferenced.Add(e.StartNodeId);
            structurallyReferenced.Add(e.EndNodeId);
        }
        foreach (var r in model.Rigids)
        {
            structurallyReferenced.Add(r.IndependentNodeId);
            foreach (int dep in r.DependentNodeIds)
                structurallyReferenced.Add(dep);
        }

        // node → 부착된 PointMass id 들
        var pmByNode = new Dictionary<int, List<int>>();
        foreach (var pm in model.PointMasses)
        {
            if (!pmByNode.TryGetValue(pm.NodeId, out var list))
            {
                list = new List<int>();
                pmByNode[pm.NodeId] = list;
            }
            list.Add(pm.Id);
        }

        var boundarySet = new HashSet<int>();
        foreach (var n in model.Nodes)
            if (n.HasTag(NodeTags.Boundary))
                boundarySet.Add(n.Id);

        var toRemoveNodes      = new List<int>();
        var toRemovePm         = new List<int>();   // PointMass.Id 로 제거
        int floatingPmRemoved  = 0;                 // PointMass-only 노드(공중 부유)
        int pureOrphanRemoved  = 0;                 // 어디에도 참조 없는 노드
        int preservedBoundary  = 0;

        foreach (var n in model.Nodes)
        {
            bool hasStruct = structurallyReferenced.Contains(n.Id);
            if (hasStruct) continue;
            if (boundarySet.Contains(n.Id)) { preservedBoundary++; continue; }

            toRemoveNodes.Add(n.Id);
            if (pmByNode.TryGetValue(n.Id, out var pmIds))
            {
                toRemovePm.AddRange(pmIds);
                floatingPmRemoved++;
            }
            else
            {
                pureOrphanRemoved++;
            }
        }

        if (toRemoveNodes.Count == 0)
        {
            if (preservedBoundary > 0)
                ctx.AddDiagnostic(DiagnosticSeverity.Info, "ORPHAN_NODE_PRESERVED",
                    $"{preservedBoundary} orphan node(s) preserved due to Boundary tag.");
            return true;
        }

        var nodeRemoveSet = new HashSet<int>(toRemoveNodes);
        model.Nodes.RemoveAll(n => nodeRemoveSet.Contains(n.Id));

        if (toRemovePm.Count > 0)
        {
            var pmRemoveSet = new HashSet<int>(toRemovePm);
            model.PointMasses.RemoveAll(pm => pmRemoveSet.Contains(pm.Id));
            foreach (int pmId in toRemovePm)
                ctx.AddTrace(TraceAction.ElementRemoved, Name,
                    elementId: pmId, note: "PointMass on floating node (no element/rigid)");
        }

        foreach (int id in toRemoveNodes)
        {
            string note = pmByNode.ContainsKey(id)
                ? "floating (only PointMass reference)"
                : "orphan (no element/rigid/pointMass reference)";
            ctx.AddTrace(TraceAction.NodeRemoved, Name, nodeId: id, note: note);
        }

        if (pureOrphanRemoved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "ORPHAN_NODE_REMOVED",
                $"{pureOrphanRemoved} orphan node(s) removed (no element/rigid/pointMass reference).");
        if (floatingPmRemoved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FLOATING_POINTMASS_REMOVED",
                $"{floatingPmRemoved} floating PointMass node(s) removed (only PointMass attached, no element/rigid).");
        if (preservedBoundary > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "ORPHAN_NODE_PRESERVED",
                $"{preservedBoundary} orphan node(s) preserved due to Boundary tag.");

        return true;
    }
}
