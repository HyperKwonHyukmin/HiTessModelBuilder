using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

public sealed class UboltRbeStage : IPipelineStage
{
    public string Name => "UboltRbe";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        var uboltRbes = model.Rigids
            .Where(r => r.Remark == "UBOLT" && !IsBoxUbolt(r))
            .ToList();
        if (uboltRbes.Count == 0) return true;

        var structureElems = model.Elements
            .Where(e => e.Category == EntityCategory.Structure)
            .ToList();
        if (structureElems.Count == 0)
        {
            MarkAllAsBoundary(model, uboltRbes);
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "UBOLT_NO_STRUCTURE",
                $"{uboltRbes.Count} U-bolt(s) have no structure to snap to — marked as boundary.");
            return true;
        }

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var structById = structureElems.ToDictionary(e => e.Id);
        double cellSize = ctx.Options.Tolerances.SpatialCellSizeMm;
        double snapDist = ctx.Options.Tolerances.UboltSnapMaxDistMm;
        double mergeTol = ctx.Options.Tolerances.NodeMergeMm;
        double extraMargin = ctx.Options.Tolerances.ExtendExtraMarginMm;
        double hashInflate = snapDist * 2.0;
        var spatialHash = new ElementSpatialHash(structureElems, nodeById, cellSize, hashInflate);

        RemoveDuplicateUbolts(model, nodeById);

        int regularSnapped = 0;
        int fallback = 0;

        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rbe = model.Rigids[i];
            if (rbe.Remark != "UBOLT") continue;
            if (!nodeById.TryGetValue(rbe.IndependentNodeId, out var uboltNode)) continue;

            if (IsBoxUbolt(rbe)) continue;

            var result = TrySnapRegular(ctx, model, spatialHash, structById, nodeById, rbe,
                uboltNode, snapDist, mergeTol, extraMargin);
            if (result.Found)
            {
                model.Rigids[i] = new RigidElement(rbe.Id, result.IndependentNodeId,
                    [result.DependentNodeId], rbe.Remark, rbe.SourceName, rbe.Cm, rbe.DetailRemark);
                RemapPointMasses(model, rbe.IndependentNodeId, result.IndependentNodeId, rbe.SourceName);
                ctx.AddTrace(TraceAction.ElementCreated, Name,
                    elementId: rbe.Id, note: $"UBOLT snapped to E{result.TargetElementId}");
                regularSnapped++;
            }
            else
            {
                uboltNode.AddTag(NodeTags.Boundary);
                EmitFallbackDiagnostic(ctx, model, rbe, uboltNode, snapDist, extraMargin);
                ctx.AddTrace(TraceAction.NodeMoved, Name,
                    nodeId: rbe.IndependentNodeId, note: "UBOLT fallback — marked Boundary");
                fallback++;
            }
        }

        // Fallback U-bolt 또는 import 시점부터 dep가 비어있는 RBE2를 제거.
        // indep 노드는 이미 NodeTags.Boundary 표시되어 SPC1로 export됨 → RBE2 자체는 무효.
        int emptyRemoved = 0;
        for (int i = model.Rigids.Count - 1; i >= 0; i--)
        {
            var r = model.Rigids[i];
            if (r.Remark != "UBOLT") continue;
            if (IsBoxUbolt(r)) continue;
            if (r.DependentNodeIds.Count > 0) continue;
            ctx.AddTrace(TraceAction.ElementRemoved, Name,
                elementId: r.Id, nodeId: r.IndependentNodeId,
                note: "empty dep UBOLT removed (indep tagged Boundary for SPC1)");
            model.Rigids.RemoveAt(i);
            emptyRemoved++;
        }

        RigidElementNormalizer.MergeSharedDependentNodes(ctx, Name);
        int verticalized = VerticalizeSafeUboltAnchors(ctx);

        if (regularSnapped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_SNAPPED",
                $"{regularSnapped} U-bolt(s) snapped to structure.");
        if (verticalized > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_ANCHOR_VERTICALIZED",
                $"{verticalized} U-bolt anchor node(s) moved onto vertical support line.");
        if (fallback > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_FALLBACK_SUMMARY",
                $"{fallback} U-bolt(s) too far from structure — will export as SPC1.");
        if (emptyRemoved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_EMPTY_DEP_REMOVED",
                $"{emptyRemoved} U-bolt RBE2(s) with empty dep removed (indep retained as boundary).");

        return true;
    }

    public static int VerticalizeSafeUboltAnchors(StageContext ctx, string stageName = "UboltRbe")
    {
        var model = ctx.Model;
        var nodeIndexById = model.Nodes
            .Select((n, i) => (n.Id, Index: i))
            .ToDictionary(x => x.Id, x => x.Index);
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        int moved = 0;

        foreach (var rigid in model.Rigids)
        {
            if (rigid.Remark != "UBOLT" || IsBoxUbolt(rigid)) continue;
            if (rigid.DependentNodeIds.Count != 1) continue;

            int anchorId = rigid.IndependentNodeId;
            int supportId = rigid.DependentNodeIds[0];
            if (!nodeById.TryGetValue(anchorId, out var anchor) ||
                !nodeById.TryGetValue(supportId, out var support)) continue;

            if (!CanVerticalizeUboltAnchor(model, nodeById, anchor, support, out var target)) continue;
            if (!nodeIndexById.TryGetValue(anchorId, out int index)) continue;

            model.Nodes[index] = new Node(anchorId, target, anchor.Tags);
            nodeById[anchorId] = model.Nodes[index];
            ctx.AddTrace(TraceAction.NodeMoved, stageName,
                elementId: rigid.Id, nodeId: anchorId, relatedNodeId: supportId,
                note: "UBOLT anchor moved onto vertical support line");
            moved++;
        }

        return moved;
    }

    private static bool CanVerticalizeUboltAnchor(
        FeModel model,
        IReadOnlyDictionary<int, Node> nodeById,
        Node anchor,
        Node support,
        out Point3 target)
    {
        target = default;
        const double coordTol = 1e-6;
        const double nodeOnPathTol = 1.0;

        var a = anchor.Position;
        var s = support.Position;
        if (Math.Abs(a.Y - s.Y) > coordTol) return false;
        if (Math.Abs(a.X - s.X) <= coordTol) return false;
        if (Math.Abs(a.Z - s.Z) <= coordTol) return false;

        var pipeElems = model.Elements
            .Where(e => e.Category == EntityCategory.Pipe &&
                        (e.StartNodeId == anchor.Id || e.EndNodeId == anchor.Id))
            .ToList();
        if (pipeElems.Count < 2) return false;

        var otherNodes = new List<Node>();
        foreach (var elem in pipeElems)
        {
            int otherId = elem.StartNodeId == anchor.Id ? elem.EndNodeId : elem.StartNodeId;
            if (!nodeById.TryGetValue(otherId, out var other)) return false;
            if (Math.Abs(other.Position.Y - a.Y) > coordTol) return false;
            if (Math.Abs(other.Position.Z - a.Z) > coordTol) return false;
            otherNodes.Add(other);
        }

        double minX = otherNodes.Min(n => n.Position.X);
        double maxX = otherNodes.Max(n => n.Position.X);
        if (s.X <= minX + coordTol || s.X >= maxX - coordTol) return false;

        target = new Point3(s.X, a.Y, a.Z);
        foreach (var elem in pipeElems)
        {
            int otherId = elem.StartNodeId == anchor.Id ? elem.EndNodeId : elem.StartNodeId;
            if (!nodeById.TryGetValue(otherId, out var other)) return false;
            if (target.DistanceTo(other.Position) < nodeOnPathTol) return false;
        }

        double pathMinX = Math.Min(a.X, target.X) - coordTol;
        double pathMaxX = Math.Max(a.X, target.X) + coordTol;
        foreach (var node in nodeById.Values)
        {
            if (node.Id == anchor.Id || node.Id == support.Id) continue;
            if (Math.Abs(node.Position.Y - a.Y) > nodeOnPathTol) continue;
            if (Math.Abs(node.Position.Z - a.Z) > nodeOnPathTol) continue;
            if (node.Position.X > pathMinX && node.Position.X < pathMaxX)
                return false;
            if (node.Position.DistanceTo(target) < nodeOnPathTol)
                return false;
        }

        return true;
    }

    private static void EmitFallbackDiagnostic(StageContext ctx, FeModel model,
        RigidElement rbe, Node uboltNode, double snapDist, double extraMargin)
    {
        var ownerPipe = FindOwnerPipe(model, rbe.IndependentNodeId);
        double pipeRadius = ownerPipe is null ? 0.0 : GetSectionMaxDim(model, ownerPipe.PropertyId);
        double appliedRadius = Math.Max(snapDist * 2.0, pipeRadius * 2.0 + extraMargin);
        var p = uboltNode.Position;
        string pipeInfo = ownerPipe is null
            ? "no owner pipe"
            : $"on pipe E{ownerPipe.Id} (sec maxDim={pipeRadius:F0}mm)";
        ctx.AddDiagnostic(DiagnosticSeverity.Warning, "UBOLT_FALLBACK",
            $"U-bolt N{rbe.IndependentNodeId} '{rbe.SourceName}' at " +
            $"({p.X:F0},{p.Y:F0},{p.Z:F0}) — {pipeInfo}, no structure within " +
            $"{appliedRadius:F0}mm. SPC1 fallback applied.",
            nodeId: rbe.IndependentNodeId);
    }

    private sealed record SnapResult(
        bool Found,
        int IndependentNodeId = -1,
        int DependentNodeId = -1,
        int TargetElementId = -1);

    private static SnapResult TrySnapRegular(
        StageContext ctx,
        FeModel model,
        ElementSpatialHash spatialHash,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        RigidElement rbe,
        Node uboltNode,
        double snapDist,
        double mergeTol,
        double extraMargin)
    {
        var ownerPipe = FindOwnerPipe(model, rbe.IndependentNodeId);
        double pipeRadius = ownerPipe is null ? 0.0 : GetSectionMaxDim(model, ownerPipe.PropertyId);
        double phase1 = Math.Max(snapDist, pipeRadius + extraMargin);
        double phase2 = Math.Max(snapDist * 2.0, pipeRadius * 2.0 + extraMargin);

        var selected = FindClosestCandidate(spatialHash, structById, nodeById,
            uboltNode.Position, phase1, preferLargeSection: false, model);
        selected ??= FindClosestCandidate(spatialHash, structById, nodeById,
            uboltNode.Position, phase2, preferLargeSection: true, model);
        if (selected is null) return new SnapResult(false);

        int depNodeId = AddOrReuseNode(ctx, model, nodeById, selected.Value.Projection, mergeTol,
            note: $"UBOLT dep snap to E{selected.Value.ElementId}", relatedElementId: rbe.Id);
        Point3 indepPoint = uboltNode.Position;

        if (ownerPipe is not null &&
            nodeById.TryGetValue(ownerPipe.StartNodeId, out var pA) &&
            nodeById.TryGetValue(ownerPipe.EndNodeId, out var pB))
        {
            var (pipeProj, _, _) = ProjectionUtils.ClosestPointOnSegment(
                selected.Value.Projection, pA.Position, pB.Position);
            indepPoint = pipeProj;
        }

        int indepNodeId = AddOrReuseNode(ctx, model, nodeById, indepPoint, mergeTol,
            note: "UBOLT indep on owner pipe", relatedElementId: rbe.Id);
        return new SnapResult(true, indepNodeId, depNodeId, selected.Value.ElementId);
    }

    private readonly record struct Candidate(int ElementId, Point3 Projection, double Distance, double MaxDim);

    private static Candidate? FindClosestCandidate(
        ElementSpatialHash spatialHash,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        Point3 pos,
        double radius,
        bool preferLargeSection,
        FeModel model)
    {
        var candidates = EnumerateCandidates(spatialHash, structById, nodeById, pos, radius, model).ToList();
        if (candidates.Count == 0) return null;

        return preferLargeSection
            ? candidates.OrderByDescending(c => c.MaxDim).ThenBy(c => c.Distance).First()
            : candidates.OrderBy(c => c.Distance).First();
    }

    private static IEnumerable<Candidate> EnumerateCandidates(
        ElementSpatialHash spatialHash,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        Point3 pos,
        double radius,
        FeModel model)
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

            yield return new Candidate(eid, proj, dist, GetSectionMaxDim(model, elem.PropertyId));
        }
    }

    private static int AddOrReuseNode(
        StageContext ctx,
        FeModel model,
        Dictionary<int, Node> nodeById,
        Point3 pos,
        double mergeTol,
        string? note = null,
        int? relatedElementId = null)
    {
        var existing = nodeById.Values.FirstOrDefault(n => n.Position.DistanceTo(pos) <= mergeTol);
        if (existing is not null) return existing.Id;

        int id = model.AllocNodeId();
        var node = new Node(id, pos);
        model.Nodes.Add(node);
        nodeById[id] = node;
        ctx.AddTrace(TraceAction.NodeCreated, "UboltRbe",
            nodeId: id, relatedElementId: relatedElementId, note: note);
        return id;
    }

    private static BeamElement? FindOwnerPipe(FeModel model, int nodeId)
        => model.Elements.FirstOrDefault(e =>
            e.Category == EntityCategory.Pipe &&
            (e.StartNodeId == nodeId || e.EndNodeId == nodeId));

    private static double GetSectionMaxDim(FeModel model, int propertyId)
    {
        var section = model.Sections.Find(s => s.Id == propertyId);
        return section is null || section.Dims.Length == 0 ? 0.0 : section.Dims.Max();
    }

    private static bool IsBoxUbolt(RigidElement rbe)
        => string.Equals(rbe.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase);

    private static void RemoveDuplicateUbolts(FeModel model, Dictionary<int, Node> nodeById)
    {
        var seen = new HashSet<(long X, long Y, long Z)>();
        model.Rigids.RemoveAll(r =>
        {
            if (r.Remark != "UBOLT" || IsBoxUbolt(r)) return false;
            if (!nodeById.TryGetValue(r.IndependentNodeId, out var n)) return false;
            var key = (
                (long)Math.Round(n.Position.X * 1000.0),
                (long)Math.Round(n.Position.Y * 1000.0),
                (long)Math.Round(n.Position.Z * 1000.0));
            return !seen.Add(key);
        });
    }

    private static void RemapPointMasses(FeModel model, int oldNodeId, int newNodeId, string? sourceName)
    {
        if (oldNodeId == newNodeId) return;
        for (int i = 0; i < model.PointMasses.Count; i++)
        {
            var pm = model.PointMasses[i];
            if (pm.NodeId != oldNodeId) continue;
            if (sourceName is not null && pm.SourceName is not null && pm.SourceName != sourceName) continue;
            model.PointMasses[i] = new PointMass(pm.Id, newNodeId, pm.Mass, pm.SourceName);
        }
    }

    private static void MarkAllAsBoundary(FeModel model, IEnumerable<RigidElement> uboltRbes)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        foreach (var rbe in uboltRbes)
            if (nodeById.TryGetValue(rbe.IndependentNodeId, out var n))
                n.AddTag(NodeTags.Boundary);
    }
}
