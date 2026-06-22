using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 모델 무결성·BDF export 호환성 최종 검증.
/// Error 발생 시 Execute() == false → BDF 출력 차단 (CLI exit code 2).
/// </summary>
public sealed class FinalValidationStage : IPipelineStage
{
    /// <summary>NASTRAN small field 형식의 ID 한계 (8자리).</summary>
    private const int BdfSmallFieldMaxId = 99_999_999;

    public string Name => "FinalValidation";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        var tols  = ctx.Options.Tolerances;

        RigidElementNormalizer.RemoveCircularDependencies(ctx, Name);

        // 공유 인덱스 한 번만 빌드
        var nodeById     = model.Nodes.ToDictionary(n => n.Id);
        var nodeIds      = new HashSet<int>(nodeById.Keys);
        var sectionIds   = model.Sections.Select(s => s.Id).ToHashSet();
        var materialIds  = model.Materials.Select(m => m.Id).ToHashSet();
        var referenced   = BuildReferencedNodeIds(model);

        RecalculateBeamOrientations(model, nodeById);
        CheckMissingProperties(ctx, model, sectionIds);
        CheckMissingMaterials(ctx, model, materialIds);
        CheckMissingReferencedNodes(ctx, model, nodeIds);
        CheckOrphanNodes(ctx, model, referenced);
        CheckFreeEndNodes(ctx, model, nodeById);
        CheckElementLengths(ctx, model, nodeById, tols);
        CheckRigidElements(ctx, model);
        CheckBoxUboltDependentDirections(ctx, model, nodeById, tols);
        CheckBdfExportCompatibility(ctx, model);

        bool hasError = ctx.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        return !hasError;
    }

    // ── Shared index builders ─────────────────────────────────────────────────

    private static HashSet<int> BuildReferencedNodeIds(FeModel model)
    {
        var ids = new HashSet<int>();
        foreach (var elem in model.Elements)
        {
            ids.Add(elem.StartNodeId);
            ids.Add(elem.EndNodeId);
        }
        foreach (var rigid in model.Rigids)
        {
            ids.Add(rigid.IndependentNodeId);
            foreach (var dep in rigid.DependentNodeIds) ids.Add(dep);
        }
        foreach (var pm in model.PointMasses) ids.Add(pm.NodeId);
        return ids;
    }

    // ── Existing checks ───────────────────────────────────────────────────────

    private static void RecalculateBeamOrientations(FeModel model, Dictionary<int, Node> nodeById)
    {
        for (int i = 0; i < model.Elements.Count; i++)
        {
            var elem = model.Elements[i];
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId, out var nB)) continue;

            if (nA.Position.DistanceTo(nB.Position) <= 1e-9) continue;

            var orientation = BeamOrientation.CalculateBarOrientation(nA.Position, nB.Position);
            model.Elements[i] = new BeamElement(
                elem.Id,
                elem.StartNodeId,
                elem.EndNodeId,
                elem.PropertyId,
                elem.Category,
                orientation,
                elem.SourceName,
                elem.ParentElementId);
        }
    }

    private static void CheckMissingProperties(StageContext ctx, FeModel model, HashSet<int> sectionIds)
    {
        foreach (var elem in model.Elements)
        {
            if (!sectionIds.Contains(elem.PropertyId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_PROPERTY",
                    $"Element {elem.Id} references PropertyId {elem.PropertyId} which does not exist.",
                    elementId: elem.Id);
        }
    }

    private static void CheckMissingMaterials(StageContext ctx, FeModel model, HashSet<int> materialIds)
    {
        foreach (var section in model.Sections)
        {
            if (!materialIds.Contains(section.MaterialId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_MATERIAL",
                    $"Section {section.Id} references MaterialId {section.MaterialId} which does not exist.");
        }
    }

    private static void CheckMissingReferencedNodes(StageContext ctx, FeModel model, HashSet<int> nodeIds)
    {
        foreach (var elem in model.Elements)
        {
            if (!nodeIds.Contains(elem.StartNodeId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_NODE",
                    $"Element {elem.Id} references StartNodeId {elem.StartNodeId} which does not exist.",
                    elementId: elem.Id, nodeId: elem.StartNodeId);
            if (!nodeIds.Contains(elem.EndNodeId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_NODE",
                    $"Element {elem.Id} references EndNodeId {elem.EndNodeId} which does not exist.",
                    elementId: elem.Id, nodeId: elem.EndNodeId);
        }

        foreach (var rigid in model.Rigids)
        {
            if (!nodeIds.Contains(rigid.IndependentNodeId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_NODE",
                    $"Rigid {rigid.Id} references IndependentNodeId {rigid.IndependentNodeId} which does not exist.",
                    elementId: rigid.Id, nodeId: rigid.IndependentNodeId);
            foreach (int dep in rigid.DependentNodeIds)
            {
                if (!nodeIds.Contains(dep))
                    ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_NODE",
                        $"Rigid {rigid.Id} references DependentNodeId {dep} which does not exist.",
                        elementId: rigid.Id, nodeId: dep);
            }
        }

        foreach (var pm in model.PointMasses)
        {
            if (!nodeIds.Contains(pm.NodeId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_NODE",
                    $"Point mass {pm.Id} references NodeId {pm.NodeId} which does not exist.",
                    elementId: pm.Id, nodeId: pm.NodeId);
        }
    }

    private static void CheckOrphanNodes(StageContext ctx, FeModel model, HashSet<int> referenced)
    {
        foreach (var node in model.Nodes)
        {
            if (!referenced.Contains(node.Id))
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "ORPHAN_NODE",
                    $"Node {node.Id} at ({node.Position.X:F1},{node.Position.Y:F1},{node.Position.Z:F1}) is not referenced by any element/rigid/PointMass.",
                    nodeId: node.Id);
        }
    }

    private static void CheckFreeEndNodes(StageContext ctx, FeModel model, Dictionary<int, Node> nodeById)
    {
        var nodeRefCount = new Dictionary<int, int>();
        foreach (var elem in model.Elements)
        {
            nodeRefCount[elem.StartNodeId] = nodeRefCount.GetValueOrDefault(elem.StartNodeId) + 1;
            nodeRefCount[elem.EndNodeId]   = nodeRefCount.GetValueOrDefault(elem.EndNodeId)   + 1;
        }

        // RBE 연결 노드도 차수에 포함 (이미 요소에 연결된 노드에 한정)
        foreach (var rigid in model.Rigids)
        {
            if (nodeRefCount.ContainsKey(rigid.IndependentNodeId))
                nodeRefCount[rigid.IndependentNodeId]++;
            foreach (int dep in rigid.DependentNodeIds)
                if (nodeRefCount.ContainsKey(dep))
                    nodeRefCount[dep]++;
        }

        foreach (var (nodeId, count) in nodeRefCount)
        {
            if (count != 1) continue;
            if (!nodeById.TryGetValue(nodeId, out var node)) continue;
            if (node.Tags.HasFlag(NodeTags.Boundary)) continue;

            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "FREE_END_NODE",
                $"Node {nodeId} is a free end (connected to exactly 1 element).",
                nodeId: nodeId);
        }
    }

    // ── New checks ────────────────────────────────────────────────────────────

    /// <summary>
    /// 길이 0 element는 NASTRAN에서 singular Jacobian → Error.
    /// ZeroLengthMm 초과 ~ ShortElemMinMm 미만은 Warning.
    /// </summary>
    private static void CheckElementLengths(StageContext ctx, FeModel model,
        Dictionary<int, Node> nodeById, Tolerances tols)
    {
        foreach (var elem in model.Elements)
        {
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

            double len = nA.Position.DistanceTo(nB.Position);
            if (len <= tols.ZeroLengthMm)
            {
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "ZERO_LENGTH_ELEMENT",
                    $"Element {elem.Id} length {len:F4}mm ≤ {tols.ZeroLengthMm:F2}mm — singular Jacobian risk.",
                    elementId: elem.Id);
            }
            else if (len < tols.ShortElemMinMm)
            {
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "SHORT_ELEMENT",
                    $"Element {elem.Id} length {len:F3}mm < {tols.ShortElemMinMm:F1}mm — verify mesh.",
                    elementId: elem.Id);
            }
        }
    }

    /// <summary>
    /// 빈 dep RBE2는 BDF에서 무효 (NASTRAN parse error). Error.
    /// </summary>
    private static void CheckRigidElements(StageContext ctx, FeModel model)
    {
        foreach (var rigid in model.Rigids)
        {
            if (rigid.DependentNodeIds.Count == 0)
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "EMPTY_RIGID",
                    $"Rigid {rigid.Id} (indep N{rigid.IndependentNodeId}) has zero dependent nodes — invalid RBE2.",
                    elementId: rigid.Id, nodeId: rigid.IndependentNodeId);
        }

        CheckNodeRigidMultiplicity(ctx, model);
        CheckRigidDependencyCycles(ctx, model);
    }

    /// <summary>
    /// 한 노드가 서로 다른 2개 이상의 RBE2 에 등장하면 안 된다(indep/dep 불문).
    /// RigidSharedNodeMergeStage 가 끝단에서 보장하므로, 여기서 걸리면 회귀(regression)다.
    /// 다운스트림 빌드를 막지 않도록 Warning 으로 보고(차단은 RBE2 cycle/empty 가 담당).
    /// </summary>
    private static void CheckNodeRigidMultiplicity(StageContext ctx, FeModel model)
    {
        var nodeToRigidIds = new Dictionary<int, List<int>>();
        void Touch(int nodeId, int rigidId)
        {
            if (!nodeToRigidIds.TryGetValue(nodeId, out var ids)) { ids = []; nodeToRigidIds[nodeId] = ids; }
            if (!ids.Contains(rigidId)) ids.Add(rigidId);
        }
        foreach (var rigid in model.Rigids)
        {
            Touch(rigid.IndependentNodeId, rigid.Id);
            foreach (int dep in rigid.DependentNodeIds) Touch(dep, rigid.Id);
        }

        foreach (var (nodeId, rigidIds) in nodeToRigidIds)
        {
            if (rigidIds.Count < 2) continue;
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "NODE_IN_MULTIPLE_RBE2",
                $"Node N{nodeId} is referenced by {rigidIds.Count} RBE2 elements ({string.Join(",", rigidIds.OrderBy(id => id))}) — " +
                "one node must belong to a single RBE2 (RigidSharedNodeMergeStage should have merged these).",
                nodeId: nodeId);
        }
    }

    private static void CheckRigidDependencyCycles(StageContext ctx, FeModel model)
    {
        var adjacency = new Dictionary<int, List<RigidDependencyEdge>>();
        foreach (var rigid in model.Rigids)
        {
            foreach (int dep in rigid.DependentNodeIds)
            {
                if (!adjacency.TryGetValue(rigid.IndependentNodeId, out var edges))
                {
                    edges = [];
                    adjacency[rigid.IndependentNodeId] = edges;
                }
                edges.Add(new RigidDependencyEdge(rigid.Id, rigid.IndependentNodeId, dep));
            }
        }

        var color = new Dictionary<int, int>();
        var nodeStack = new List<int>();
        var edgeStack = new List<RigidDependencyEdge>();
        var reported = new HashSet<string>();

        foreach (int nodeId in adjacency.Keys.OrderBy(id => id).ToList())
        {
            if (color.GetValueOrDefault(nodeId) == 0)
                Visit(nodeId);
        }

        void Visit(int nodeId)
        {
            color[nodeId] = 1;
            nodeStack.Add(nodeId);

            if (adjacency.TryGetValue(nodeId, out var edges))
            {
                foreach (var edge in edges.OrderBy(e => e.RigidId).ThenBy(e => e.ToNodeId))
                {
                    int nextColor = color.GetValueOrDefault(edge.ToNodeId);
                    if (nextColor == 1)
                    {
                        int cycleStart = nodeStack.IndexOf(edge.ToNodeId);
                        if (cycleStart >= 0)
                        {
                            var cycleEdges = edgeStack.Skip(cycleStart).Append(edge).ToList();
                            string key = string.Join(",", cycleEdges.Select(e => e.RigidId).OrderBy(id => id));
                            if (reported.Add(key))
                            {
                                string nodes = string.Join(" -> ", nodeStack.Skip(cycleStart).Append(edge.ToNodeId).Select(id => $"N{id}"));
                                string rigids = string.Join(",", cycleEdges.Select(e => e.RigidId));
                                ctx.AddDiagnostic(DiagnosticSeverity.Error, "RBE2_CIRCULAR_DEPENDENCY",
                                    $"RBE2 dependency cycle detected ({nodes}) through RBE2 {rigids}. " +
                                    "This can produce Nastran FATAL 6202 singular RMM in MCE1.",
                                    elementId: edge.RigidId,
                                    nodeId: edge.ToNodeId);
                            }
                        }
                    }
                    else if (nextColor == 0)
                    {
                        edgeStack.Add(edge);
                        Visit(edge.ToNodeId);
                        edgeStack.RemoveAt(edgeStack.Count - 1);
                    }
                }
            }

            nodeStack.RemoveAt(nodeStack.Count - 1);
            color[nodeId] = 2;
        }
    }

    private readonly record struct RigidDependencyEdge(int RigidId, int FromNodeId, int ToNodeId);

    private static void CheckBoxUboltDependentDirections(
        StageContext ctx,
        FeModel model,
        Dictionary<int, Node> nodeById,
        Tolerances tols)
    {
        var sectionById = model.Sections.ToDictionary(s => s.Id);
        var pipeOwnerByNid = new Dictionary<int, BeamElement>();
        foreach (var e in model.Elements)
        {
            if (e.Category != EntityCategory.Pipe) continue;
            pipeOwnerByNid.TryAdd(e.StartNodeId, e);
            pipeOwnerByNid.TryAdd(e.EndNodeId, e);
        }

        foreach (var rigid in model.Rigids)
        {
            if (rigid.Remark != "UBOLT" ||
                !string.Equals(rigid.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!nodeById.TryGetValue(rigid.IndependentNodeId, out var indep) ||
                !pipeOwnerByNid.TryGetValue(rigid.IndependentNodeId, out var ownerPipe) ||
                !nodeById.TryGetValue(ownerPipe.StartNodeId, out var pA) ||
                !nodeById.TryGetValue(ownerPipe.EndNodeId, out var pB))
                continue;

            var axis = pB.Position - pA.Position;
            if (axis.Length < 1e-9) continue;
            axis = axis.Normalize();

            var globalUp = Math.Abs(axis.Z) > 0.99 ? Vector3.UnitY : Vector3.UnitZ;
            var right = Vector3.Cross(axis, globalUp).Normalize();
            var up = Vector3.Cross(right, axis).Normalize();
            double pipeRadius = sectionById.TryGetValue(ownerPipe.PropertyId, out var sec)
                ? sec.MaxCrossSectionDim()
                : 0.0;
            double sideCrossLimit = pipeRadius + tols.ExtendExtraMarginMm;

            foreach (int depId in rigid.DependentNodeIds)
            {
                if (!nodeById.TryGetValue(depId, out var dep)) continue;
                var diff = dep.Position - indep.Position;
                double u = Vector3.Dot(diff, up);
                double r = Vector3.Dot(diff, right);
                string side = Math.Abs(u) >= Math.Abs(r)
                    ? (u >= 0 ? "top" : "bottom")
                    : (r >= 0 ? "right" : "left");
                double cross = side is "top" or "bottom" ? Math.Abs(r) : Math.Abs(u);
                if (cross <= sideCrossLimit + 1e-6) continue;

                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "BOX_UBOLT_DEP_OUTSIDE_SIDE",
                    $"BOX U-bolt RBE {rigid.Id} dep N{depId} is classified as {side} but is {cross:F1}mm outside the local side span (limit {sideCrossLimit:F1}mm).",
                    elementId: rigid.Id, nodeId: depId);
            }
        }
    }

    /// <summary>
    /// NASTRAN small field format은 ID 1~99,999,999 까지 허용.
    /// 한계 초과 시 BdfWriter가 column 어긋나 silent corruption 위험.
    /// </summary>
    private static void CheckBdfExportCompatibility(StageContext ctx, FeModel model)
    {
        int maxNode = model.Nodes.Count    > 0 ? model.Nodes.Max(n => n.Id)    : 0;
        int maxElem = model.Elements.Count > 0 ? model.Elements.Max(e => e.Id) : 0;
        int maxRigid = model.Rigids.Count  > 0 ? model.Rigids.Max(r => r.Id)   : 0;
        int maxPm    = model.PointMasses.Count > 0 ? model.PointMasses.Max(p => p.Id) : 0;
        int maxSec   = model.Sections.Count > 0 ? model.Sections.Max(s => s.Id) : 0;
        int maxMat   = model.Materials.Count > 0 ? model.Materials.Max(m => m.Id) : 0;

        ReportIfTooLarge(ctx, "node", maxNode);
        ReportIfTooLarge(ctx, "element", maxElem);
        ReportIfTooLarge(ctx, "rigid", maxRigid);
        ReportIfTooLarge(ctx, "pointMass", maxPm);
        ReportIfTooLarge(ctx, "section", maxSec);
        ReportIfTooLarge(ctx, "material", maxMat);
    }

    private static void ReportIfTooLarge(StageContext ctx, string kind, int maxId)
    {
        if (maxId > BdfSmallFieldMaxId)
            ctx.AddDiagnostic(DiagnosticSeverity.Error, "ID_OVERFLOW",
                $"Max {kind} ID {maxId} exceeds NASTRAN small field limit ({BdfSmallFieldMaxId}). " +
                $"BDF small-field export will produce malformed cards.");
    }
}
