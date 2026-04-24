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
        var uboltRbes = model.Rigids.Where(r => r.Remark == "UBOLT").ToList();
        if (uboltRbes.Count == 0) return true;

        var structureElems = model.Elements
            .Where(e => e.Category == EntityCategory.Structure)
            .ToList();
        if (structureElems.Count == 0)
        {
            // No structure to snap to — mark all as boundary
            MarkAllAsBoundary(model, uboltRbes);
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "UBOLT_NO_STRUCTURE",
                $"{uboltRbes.Count} U-bolt(s) have no structure to snap to — marked as boundary.");
            return true;
        }

        var nodeById     = model.Nodes.ToDictionary(n => n.Id);
        var structById   = structureElems.ToDictionary(e => e.Id);
        double snapDist  = ctx.Options.Tolerances.UboltSnapMaxDistMm;
        double mergeTol  = ctx.Options.Tolerances.NodeMergeMm;
        double cellSize  = ctx.Options.Tolerances.SpatialCellSizeMm;

        // Build spatial hash inflated to cover 2× snap radius for phase-2 queries
        var spatialHash = new ElementSpatialHash(structureElems, nodeById, cellSize, snapDist * 2);

        int snapped     = 0;
        int fallback    = 0;

        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rbe = model.Rigids[i];
            if (rbe.Remark != "UBOLT") continue;
            if (!nodeById.TryGetValue(rbe.IndependentNodeId, out var uboltNode)) continue;

            // Phase 1: snap within UboltSnapMaxDistMm
            var (snapId, found) = TrySnap(model, spatialHash, structById, nodeById,
                uboltNode.Position, snapDist, mergeTol);

            // Phase 2: widen to 2× if phase 1 missed
            if (!found)
                (snapId, found) = TrySnap(model, spatialHash, structById, nodeById,
                    uboltNode.Position, snapDist * 2, mergeTol);

            if (found)
            {
                model.Rigids[i] = new RigidElement(rbe.Id, rbe.IndependentNodeId,
                    [snapId], rbe.Remark, rbe.SourceName);
                ctx.AddTrace(TraceAction.ElementCreated, Name,
                    elementId: rbe.Id, note: $"UBOLT snapped to node {snapId}");
                snapped++;
            }
            else
            {
                uboltNode.AddTag(NodeTags.Boundary);
                ctx.AddTrace(TraceAction.NodeMoved, Name,
                    nodeId: rbe.IndependentNodeId, note: "UBOLT fallback — marked Boundary");
                fallback++;
            }
        }

        if (snapped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_SNAPPED",
                $"{snapped} U-bolt(s) snapped to structure.");

        if (fallback > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "UBOLT_FALLBACK",
                $"{fallback} U-bolt(s) too far from structure — will export as SPC1.");

        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (int snapNodeId, bool found) TrySnap(
        FeModel model,
        ElementSpatialHash spatialHash,
        IReadOnlyDictionary<int, BeamElement> structById,
        Dictionary<int, Node> nodeById,
        Point3 pos,
        double radius,
        double mergeTol)
    {
        var boxMin = new Point3(pos.X - radius, pos.Y - radius, pos.Z - radius);
        var boxMax = new Point3(pos.X + radius, pos.Y + radius, pos.Z + radius);

        double bestDist = double.MaxValue;
        Point3 bestProj = default;

        foreach (int eid in spatialHash.QueryBox(boxMin, boxMax))
        {
            if (!structById.TryGetValue(eid, out var elem)) continue;
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

            var (proj, _, dist) = ProjectionUtils.ClosestPointOnSegment(pos, nA.Position, nB.Position);
            if (dist < bestDist) { bestDist = dist; bestProj = proj; }
        }

        if (bestDist > radius)
            return (-1, false);

        // Reuse existing node within mergeTol of projection
        var existing = nodeById.Values.FirstOrDefault(n => n.Position.DistanceTo(bestProj) <= mergeTol);
        int snapId;
        if (existing is not null)
        {
            snapId = existing.Id;
        }
        else
        {
            snapId = model.AllocNodeId();
            var snapNode = new Node(snapId, bestProj);
            model.Nodes.Add(snapNode);
            nodeById[snapId] = snapNode;
        }

        return (snapId, true);
    }

    private static void MarkAllAsBoundary(FeModel model, IEnumerable<RigidElement> uboltRbes)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        foreach (var rbe in uboltRbes)
            if (nodeById.TryGetValue(rbe.IndependentNodeId, out var n))
                n.AddTag(NodeTags.Boundary);
    }
}
