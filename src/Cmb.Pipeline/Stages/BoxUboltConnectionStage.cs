using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// Connects BOX U-bolts to the nearest structure member in each local top/bottom/left/right bucket.
/// This is the CMB equivalent of HiTESS UboltBoxConnectionModifier.
/// </summary>
public sealed class BoxUboltConnectionStage : IPipelineStage
{
    public string Name => "BoxUboltConnection";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        var boxUbolts = model.Rigids
            .Select((Rigid, Index) => (Rigid, Index))
            .Where(x => x.Rigid.Remark == "UBOLT" &&
                        string.Equals(x.Rigid.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase) &&
                        x.Rigid.DependentNodeIds.Count == 0)
            .ToList();
        if (boxUbolts.Count == 0) return true;

        var structureElems = model.Elements
            .Where(e => e.Category == EntityCategory.Structure)
            .ToList();
        if (structureElems.Count == 0)
        {
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "BOX_UBOLT_NO_STRUCTURE",
                $"{boxUbolts.Count} BOX U-bolt(s) have no structure to connect to.");
            return true;
        }

        var tols           = ctx.Options.Tolerances;
        var nodeById       = model.Nodes.ToDictionary(n => n.Id);
        double mergeTol    = tols.NodeMergeMm;
        double snapDist    = tols.UboltSnapMaxDistMm;

        // Pre-snap: indep가 sub-mm CSV 좌표 오차로 SplitByExistingNodes(NodeOnSegmentTolMm)에
        // 잡히지 않은 BOX UBOLT를 가장 가까운 pipe segment에 axial projection 후 split하여
        // indep를 그 endpoint로 교체. 레거시 UboltBoxConnectionModifier 와 동일한 동작.
        int preSnapped = PreSnapBoxUboltsToPipe(ctx, model, nodeById, boxUbolts, snapDist, mergeTol);

        // pre-snap 으로 pipe 분할 / RBE2 indep 가 바뀌었을 수 있으므로 인덱스 (재)구축
        var pipeOwnerByNid = BuildPipeOwnerIndex(model);
        var structById     = structureElems.ToDictionary(e => e.Id);
        var sectionById    = model.Sections.ToDictionary(s => s.Id);
        double extraMargin = tols.ExtendExtraMarginMm;
        double cellSize    = tols.SpatialCellSizeMm;
        double maxSearch   = tols.BoxUboltMaxSearchMm;
        var spatialHash    = new ElementSpatialHash(structureElems, nodeById, cellSize, extraMargin);
        var nodeHash       = new NodeSpatialHash(model.Nodes, cellSize);
        var nodeIndexById  = new Dictionary<int, int>(model.Nodes.Count);
        for (int i = 0; i < model.Nodes.Count; i++) nodeIndexById[model.Nodes[i].Id] = i;

        int connected = 0;
        int skipped = 0;

        foreach (var (_, index) in boxUbolts)
        {
            // pre-snap 으로 IndependentNodeId 가 교체됐을 수 있으므로 model.Rigids[index] 재조회
            var current = model.Rigids[index];
            if (!nodeById.TryGetValue(current.IndependentNodeId, out var uboltNode))
            {
                skipped++;
                continue;
            }

            var deps = FindBoxDependentNodes(ctx, model, spatialHash, nodeHash, nodeIndexById,
                structById, nodeById, sectionById, pipeOwnerByNid,
                current, uboltNode, mergeTol, extraMargin, maxSearch);

            if (deps.Count == 0)
            {
                skipped++;
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "BOX_UBOLT_UNRESOLVED",
                    $"BOX U-bolt N{current.IndependentNodeId} '{current.SourceName}' has no top/bottom/left/right structure candidate within search radius.",
                    nodeId: current.IndependentNodeId);
                continue;
            }

            model.Rigids[index] = new RigidElement(current.Id, current.IndependentNodeId, deps,
                current.Remark, current.SourceName, current.Cm, current.DetailRemark);
            ctx.AddTrace(TraceAction.ElementCreated, Name, elementId: current.Id,
                note: $"BOX UBOLT connected to {deps.Count} structure node(s)");
            connected++;
        }

        RigidElementNormalizer.MergeSharedDependentNodes(ctx, Name);

        if (preSnapped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "BOX_UBOLT_PRE_SNAPPED",
                $"{preSnapped} BOX U-bolt(s) snapped onto pipe centerline before connection.");
        if (connected > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "BOX_UBOLT_CONNECTED",
                $"{connected} BOX U-bolt(s) connected to structure.");
        if (skipped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "BOX_UBOLT_SKIPPED",
                $"{skipped} BOX U-bolt(s) left unresolved for validation.");

        return true;
    }

    /// <summary>
    /// BOX UBOLT의 indep가 어떤 pipe element의 endpoint도 아닐 때, UboltSnapMaxDistMm 반경 내
    /// 가장 가까운 pipe segment에 axial projection 한 뒤 그 점에 새 노드를 만들고 pipe 를 분할하여
    /// indep 를 그 endpoint로 교체한다. 이미 mergeTol 이내에 기존 pipe 노드가 있으면 split 없이 재사용.
    /// 반환: 새로 snap 된 BOX UBOLT 개수.
    /// </summary>
    private static int PreSnapBoxUboltsToPipe(
        StageContext ctx,
        FeModel model,
        Dictionary<int, Node> nodeById,
        List<(RigidElement Rigid, int Index)> boxUbolts,
        double snapDist,
        double mergeTol)
    {
        var pipeOwnerByNid = BuildPipeOwnerIndex(model);
        int snapped = 0;

        for (int k = 0; k < boxUbolts.Count; k++)
        {
            var (rbe, index) = boxUbolts[k];
            if (pipeOwnerByNid.ContainsKey(rbe.IndependentNodeId)) continue;
            if (!nodeById.TryGetValue(rbe.IndependentNodeId, out var uboltNode)) continue;

            BeamElement? bestPipe = null;
            Point3 bestProj = default;
            double bestDist = double.MaxValue;
            foreach (var e in model.Elements)
            {
                if (e.Category != EntityCategory.Pipe) continue;
                if (!nodeById.TryGetValue(e.StartNodeId, out var pA)) continue;
                if (!nodeById.TryGetValue(e.EndNodeId,   out var pB)) continue;
                var (proj, _, dist) = ProjectionUtils.ClosestPointOnSegment(uboltNode.Position, pA.Position, pB.Position);
                if (dist > snapDist) continue;
                if (dist < bestDist) { bestDist = dist; bestPipe = e; bestProj = proj; }
            }
            if (bestPipe is null) continue;

            int newIndepId;
            if (nodeById.TryGetValue(bestPipe.StartNodeId, out var nA2) &&
                nA2.Position.DistanceTo(bestProj) <= mergeTol)
            {
                newIndepId = bestPipe.StartNodeId;
            }
            else if (nodeById.TryGetValue(bestPipe.EndNodeId, out var nB2) &&
                     nB2.Position.DistanceTo(bestProj) <= mergeTol)
            {
                newIndepId = bestPipe.EndNodeId;
            }
            else
            {
                newIndepId = model.AllocNodeId();
                var newNode = new Node(newIndepId, bestProj);
                model.Nodes.Add(newNode);
                nodeById[newIndepId] = newNode;

                int childAId = model.AllocElemId();
                int childBId = model.AllocElemId();
                var childA = new BeamElement(childAId, bestPipe.StartNodeId, newIndepId,
                    bestPipe.PropertyId, bestPipe.Category, bestPipe.Orientation, bestPipe.SourceName, bestPipe.Id);
                var childB = new BeamElement(childBId, newIndepId, bestPipe.EndNodeId,
                    bestPipe.PropertyId, bestPipe.Category, bestPipe.Orientation, bestPipe.SourceName, bestPipe.Id);
                model.Elements.Remove(bestPipe);
                model.Elements.Add(childA);
                model.Elements.Add(childB);

                pipeOwnerByNid[newIndepId] = childA;
                ctx.AddTrace(TraceAction.NodeCreated, "BoxUboltConnection",
                    nodeId: newIndepId, relatedElementId: rbe.Id,
                    note: $"BOX UBOLT pre-snap node on pipe E{bestPipe.Id} (Δ={bestDist:F2}mm)");
                ctx.AddTrace(TraceAction.ElementSplit, "BoxUboltConnection",
                    elementId: childAId, relatedElementId: bestPipe.Id);
                ctx.AddTrace(TraceAction.ElementSplit, "BoxUboltConnection",
                    elementId: childBId, relatedElementId: bestPipe.Id);
            }

            var newRbe = new RigidElement(rbe.Id, newIndepId, rbe.DependentNodeIds,
                rbe.Remark, rbe.SourceName, rbe.Cm, rbe.DetailRemark);
            model.Rigids[index] = newRbe;
            boxUbolts[k] = (newRbe, index);
            ctx.AddTrace(TraceAction.NodeMoved, "BoxUboltConnection",
                elementId: rbe.Id, nodeId: newIndepId, relatedNodeId: rbe.IndependentNodeId,
                note: $"BOX UBOLT indep N{rbe.IndependentNodeId} → N{newIndepId} (Δ={bestDist:F2}mm to pipe E{bestPipe.Id})");
            snapped++;
        }

        return snapped;
    }

    private static List<int> FindBoxDependentNodes(
        StageContext ctx,
        FeModel model,
        ElementSpatialHash spatialHash,
        NodeSpatialHash nodeHash,
        Dictionary<int, int> nodeIndexById,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        IReadOnlyDictionary<int, BeamSection> sectionById,
        IReadOnlyDictionary<int, BeamElement> pipeOwnerByNid,
        RigidElement rbe,
        Node uboltNode,
        double mergeTol,
        double extraMargin,
        double maxSearch)
    {
        if (!pipeOwnerByNid.TryGetValue(rbe.IndependentNodeId, out var ownerPipe) ||
            !nodeById.TryGetValue(ownerPipe.StartNodeId, out var pA) ||
            !nodeById.TryGetValue(ownerPipe.EndNodeId, out var pB))
            return [];

        var axis = pB.Position - pA.Position;
        if (axis.Length < 1e-9) return [];
        axis = axis.Normalize();

        var globalUp = Math.Abs(axis.Z) > 0.99 ? Vector3.UnitY : Vector3.UnitZ;
        var right = Vector3.Cross(axis, globalUp).Normalize();
        var up = Vector3.Cross(right, axis).Normalize();
        double pipeRadius = GetSectionMaxDim(sectionById, ownerPipe.PropertyId);
        double radius = Math.Max(maxSearch, pipeRadius + extraMargin);
        double sideCrossLimit = pipeRadius + extraMargin;

        var buckets = new Dictionary<string, Candidate>();
        foreach (var c in EnumerateCandidates(spatialHash, structById, nodeById, sectionById, uboltNode.Position, radius))
        {
            var diff = c.Projection - uboltNode.Position;
            if (diff.Length < 1e-6) continue;

            double u = Vector3.Dot(diff, up);
            double r = Vector3.Dot(diff, right);
            string key = Math.Abs(u) >= Math.Abs(r)
                ? (u >= 0 ? "top" : "bottom")
                : (r >= 0 ? "right" : "left");

            // A BOX U-bolt side support must stay on the side enclosing the anchor.
            // Reject corner/diagonal projections that have escaped beyond the local box side span.
            double cross = key is "top" or "bottom" ? Math.Abs(r) : Math.Abs(u);
            if (cross > sideCrossLimit) continue;

            if (!buckets.TryGetValue(key, out var current) || c.Distance < current.Distance)
                buckets[key] = c;
        }

        if (buckets.Count == 0) return [];

        double avgShift = buckets.Values.Average(c => Vector3.Dot(c.Projection - uboltNode.Position, axis));
        if (Math.Abs(avgShift) > 0.01)
        {
            var aligned = uboltNode.Position + axis * avgShift;
            if (nodeIndexById.TryGetValue(uboltNode.Id, out int idx))
            {
                var moved = new Node(uboltNode.Id, aligned, uboltNode.Tags);
                model.Nodes[idx] = moved;
                nodeById[uboltNode.Id] = moved;
            }
        }

        return buckets.Values
            .OrderBy(c => c.Distance)
            .Select(c => AddOrReuseNode(ctx, model, nodeHash, nodeIndexById, nodeById, c.Projection, mergeTol, rbe.Id))
            .Distinct()
            .ToList();
    }

    private static Dictionary<int, BeamElement> BuildPipeOwnerIndex(FeModel model)
    {
        var result = new Dictionary<int, BeamElement>();
        foreach (var e in model.Elements)
        {
            if (e.Category != EntityCategory.Pipe) continue;
            // 첫 번째 pipe만 등록 — FirstOrDefault 의미 보존
            result.TryAdd(e.StartNodeId, e);
            result.TryAdd(e.EndNodeId, e);
        }
        return result;
    }

    private readonly record struct Candidate(int ElementId, Point3 Projection, double Distance, double MaxDim);

    private static IEnumerable<Candidate> EnumerateCandidates(
        ElementSpatialHash spatialHash,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        IReadOnlyDictionary<int, BeamSection> sectionById,
        Point3 pos,
        double radius)
    {
        var boxMin = new Point3(pos.X - radius, pos.Y - radius, pos.Z - radius);
        var boxMax = new Point3(pos.X + radius, pos.Y + radius, pos.Z + radius);

        foreach (int eid in spatialHash.QueryBox(boxMin, boxMax))
        {
            if (!structById.TryGetValue(eid, out var elem)) continue;
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId, out var nB)) continue;

            var (proj, _, dist) = ProjectionUtils.ClosestPointOnSegment(pos, nA.Position, nB.Position);
            if (dist > radius) continue;

            yield return new Candidate(eid, proj, dist, GetSectionMaxDim(sectionById, elem.PropertyId));
        }
    }

    private static int AddOrReuseNode(
        StageContext ctx,
        FeModel model,
        NodeSpatialHash nodeHash,
        Dictionary<int, int> nodeIndexById,
        Dictionary<int, Node> nodeById,
        Point3 pos,
        double mergeTol,
        int relatedRbeId)
    {
        // 후보 좌표 근방의 노드만 검사 — O(N) → 셀 당 평균 노드 수
        foreach (int candidateId in nodeHash.QueryNear(pos, mergeTol))
        {
            if (!nodeById.TryGetValue(candidateId, out var n)) continue;
            if (n.Position.DistanceTo(pos) <= mergeTol) return n.Id;
        }

        int id = model.AllocNodeId();
        var node = new Node(id, pos);
        model.Nodes.Add(node);
        nodeById[id] = node;
        nodeIndexById[id] = model.Nodes.Count - 1;
        nodeHash.Add(id, pos);   // 신규 노드를 공간 해시에 반영 — 같은 스테이지 후속 호출의 중복 노드 생성 방지
        ctx.AddTrace(TraceAction.NodeCreated, "BoxUboltConnection",
            nodeId: id, relatedElementId: relatedRbeId,
            note: "BOX UBOLT projection node");
        return id;
    }

    private static double GetSectionMaxDim(IReadOnlyDictionary<int, BeamSection> sectionById, int propertyId)
        => sectionById.TryGetValue(propertyId, out var sec) ? sec.MaxCrossSectionDim() : 0.0;
}
