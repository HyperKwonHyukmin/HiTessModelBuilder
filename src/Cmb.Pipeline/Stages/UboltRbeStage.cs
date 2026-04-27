using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

public sealed class UboltRbeStage : IPipelineStage
{
    private const double BoxSearchDistanceMm = 2000.0;

    public string Name => "UboltRbe";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        var uboltRbes = model.Rigids.Where(r => r.Remark == "UBOLT").ToList();
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
        double hashInflate = Math.Max(BoxSearchDistanceMm, snapDist * 2.0);
        var spatialHash = new ElementSpatialHash(structureElems, nodeById, cellSize, hashInflate);

        RemoveDuplicateUbolts(model, nodeById);

        int regularSnapped = 0;
        int boxSnapped = 0;
        int fallback = 0;

        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rbe = model.Rigids[i];
            if (rbe.Remark != "UBOLT") continue;
            if (!nodeById.TryGetValue(rbe.IndependentNodeId, out var uboltNode)) continue;

            if (IsBoxUbolt(rbe))
            {
                var deps = TrySnapBox(model, spatialHash, structById, nodeById, rbe, uboltNode,
                    mergeTol, extraMargin);
                if (deps.Count > 0)
                {
                    model.Rigids[i] = new RigidElement(rbe.Id, rbe.IndependentNodeId, deps,
                        rbe.Remark, rbe.SourceName, rbe.Cm, rbe.DetailRemark);
                    ctx.AddTrace(TraceAction.ElementCreated, Name, elementId: rbe.Id,
                        note: $"BOX UBOLT snapped to {deps.Count} structure node(s)");
                    boxSnapped++;
                }
                else
                {
                    uboltNode.AddTag(NodeTags.Boundary);
                    fallback++;
                }
                continue;
            }

            var result = TrySnapRegular(model, spatialHash, structById, nodeById, rbe,
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
                ctx.AddTrace(TraceAction.NodeMoved, Name,
                    nodeId: rbe.IndependentNodeId, note: "UBOLT fallback — marked Boundary");
                fallback++;
            }
        }

        RigidElementNormalizer.MergeSharedDependentNodes(ctx, Name);

        if (regularSnapped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_SNAPPED",
                $"{regularSnapped} U-bolt(s) snapped to structure.");
        if (boxSnapped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_BOX_SNAPPED",
                $"{boxSnapped} BOX U-bolt(s) snapped to structure.");
        if (fallback > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "UBOLT_FALLBACK",
                $"{fallback} U-bolt(s) too far from structure — will export as SPC1.");

        return true;
    }

    private sealed record SnapResult(
        bool Found,
        int IndependentNodeId = -1,
        int DependentNodeId = -1,
        int TargetElementId = -1);

    private static SnapResult TrySnapRegular(
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

        int depNodeId = AddOrReuseNode(model, nodeById, selected.Value.Projection, mergeTol);
        Point3 indepPoint = uboltNode.Position;

        if (ownerPipe is not null &&
            nodeById.TryGetValue(ownerPipe.StartNodeId, out var pA) &&
            nodeById.TryGetValue(ownerPipe.EndNodeId, out var pB))
        {
            var (pipeProj, _, _) = ProjectionUtils.ClosestPointOnSegment(
                selected.Value.Projection, pA.Position, pB.Position);
            indepPoint = pipeProj;
        }

        int indepNodeId = AddOrReuseNode(model, nodeById, indepPoint, mergeTol);
        return new SnapResult(true, indepNodeId, depNodeId, selected.Value.ElementId);
    }

    private static List<int> TrySnapBox(
        FeModel model,
        ElementSpatialHash spatialHash,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        RigidElement rbe,
        Node uboltNode,
        double mergeTol,
        double extraMargin)
    {
        var ownerPipe = FindOwnerPipe(model, rbe.IndependentNodeId);
        if (ownerPipe is null ||
            !nodeById.TryGetValue(ownerPipe.StartNodeId, out var pA) ||
            !nodeById.TryGetValue(ownerPipe.EndNodeId, out var pB))
            return [];

        var axis = (pB.Position - pA.Position);
        if (axis.Length < 1e-9) return [];
        axis = axis.Normalize();

        var globalUp = Math.Abs(axis.Z) > 0.99 ? Vector3.UnitY : Vector3.UnitZ;
        var right = Vector3.Cross(axis, globalUp).Normalize();
        var up = Vector3.Cross(right, axis).Normalize();
        double radius = Math.Max(BoxSearchDistanceMm, GetSectionMaxDim(model, ownerPipe.PropertyId) + extraMargin);

        var buckets = new Dictionary<string, Candidate>();
        foreach (var c in EnumerateCandidates(spatialHash, structById, nodeById, uboltNode.Position, radius, model))
        {
            var diff = c.Projection - uboltNode.Position;
            if (diff.Length < 1e-6) continue;

            double u = Vector3.Dot(diff, up);
            double r = Vector3.Dot(diff, right);
            string key = Math.Abs(u) >= Math.Abs(r)
                ? (u >= 0 ? "top" : "bottom")
                : (r >= 0 ? "right" : "left");

            if (!buckets.TryGetValue(key, out var current) || c.Distance < current.Distance)
                buckets[key] = c;
        }

        if (buckets.Count == 0) return [];

        double avgShift = buckets.Values.Average(c => Vector3.Dot(c.Projection - uboltNode.Position, axis));
        if (Math.Abs(avgShift) > 0.01)
        {
            var aligned = uboltNode.Position + axis * avgShift;
            int idx = model.Nodes.FindIndex(n => n.Id == uboltNode.Id);
            if (idx >= 0)
            {
                var moved = new Node(uboltNode.Id, aligned, uboltNode.Tags);
                model.Nodes[idx] = moved;
                nodeById[uboltNode.Id] = moved;
            }
        }

        return buckets.Values
            .OrderBy(c => c.Distance)
            .Select(c => AddOrReuseNode(model, nodeById, c.Projection, mergeTol))
            .Distinct()
            .ToList();
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

    private static int AddOrReuseNode(FeModel model, Dictionary<int, Node> nodeById, Point3 pos, double mergeTol)
    {
        var existing = nodeById.Values.FirstOrDefault(n => n.Position.DistanceTo(pos) <= mergeTol);
        if (existing is not null) return existing.Id;

        int id = model.AllocNodeId();
        var node = new Node(id, pos);
        model.Nodes.Add(node);
        nodeById[id] = node;
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
            if (r.Remark != "UBOLT") return false;
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
