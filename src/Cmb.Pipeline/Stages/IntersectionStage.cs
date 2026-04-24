using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

public sealed class IntersectionStage : IPipelineStage
{
    public string Name => "Intersection";

    // Merge split points closer than this along the segment to prevent zero-length fragments
    private const double MergeTolAlong = 0.05; // mm
    private const double MinSegLen     = 1e-6;  // mm

    public bool Execute(StageContext ctx)
    {
        ctx.Model.SyncIdCounters();
        int totalSplits = 0;
        for (int iter = 0; iter < ctx.Options.Tolerances.MaxConvergenceIterations; iter++)
        {
            int splits = RunOnePass(ctx);
            totalSplits += splits;
            if (splits == 0) break;
        }

        if (totalSplits > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "INTERSECTIONS_SPLIT",
                $"{totalSplits} element(s) split at intersection points.");

        return true;
    }

    private static int RunOnePass(StageContext ctx)
    {
        var model   = ctx.Model;
        double tol  = ctx.Options.Tolerances.IntersectionSnapMm;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);

        var spatialHash = new ElementSpatialHash(model.Elements, nodeById,
            ctx.Options.Tolerances.SpatialCellSizeMm, tol);

        var splitMap = new Dictionary<int, List<(int nodeId, double u)>>();
        var visited  = new HashSet<(int, int)>();
        var posMap   = new Dictionary<(long, long, long), int>(); // dedup intersection nodes

        foreach (int eid in model.Elements.Select(e => e.Id).ToList())
        {
            var elem = model.Elements.Find(e => e.Id == eid);
            if (elem is null) continue;
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

            foreach (int otherId in spatialHash.QueryCandidates(eid))
            {
                var other = model.Elements.Find(e => e.Id == otherId);
                if (other is null) continue;

                int lo = Math.Min(eid, otherId), hi = Math.Max(eid, otherId);
                if (!visited.Add((lo, hi))) continue;

                if (!nodeById.TryGetValue(other.StartNodeId, out var nC)) continue;
                if (!nodeById.TryGetValue(other.EndNodeId,   out var nD)) continue;

                // Skip if already sharing an endpoint (already connected)
                if (elem.StartNodeId == other.StartNodeId || elem.StartNodeId == other.EndNodeId ||
                    elem.EndNodeId   == other.StartNodeId || elem.EndNodeId   == other.EndNodeId)
                    continue;

                if (ProjectionUtils.IsNearlyParallel(nA.Position, nB.Position, nC.Position, nD.Position))
                    continue;

                var (P, Q, s, t, dist) = ProjectionUtils.SegmentToSegmentClosestPoints(
                    nA.Position, nB.Position, nC.Position, nD.Position);

                if (dist > tol) continue;

                // Intersection midpoint — deduplicate by quantized position
                var X = P + (Q - P) * 0.5;
                var key = Quant(X);
                if (!posMap.TryGetValue(key, out int nid))
                {
                    nid = model.AllocNodeId();
                    var newNode = new Node(nid, X);
                    model.Nodes.Add(newNode);
                    nodeById[nid] = newNode;
                    posMap[key] = nid;
                }

                AddSplit(splitMap, eid,     nid, s);
                AddSplit(splitMap, otherId, nid, t);
            }
        }

        if (splitMap.Count == 0) return 0;

        return ApplySplit(ctx, splitMap, nodeById);
    }

    private static int ApplySplit(
        StageContext ctx,
        Dictionary<int, List<(int nodeId, double u)>> splitMap,
        Dictionary<int, Node> nodeById)
    {
        var model      = ctx.Model;
        int splitCount = 0;

        foreach (var (eid, rawHits) in splitMap)
        {
            var elem = model.Elements.Find(e => e.Id == eid);
            if (elem is null) continue;
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

            double segLen = nA.Position.DistanceTo(nB.Position);

            var hits = rawHits
                .OrderBy(h => h.u)
                .ToList();
            hits = MergeCloseByU(hits, MergeTolAlong, segLen);

            // Build node chain — skip hits coinciding with endpoints
            var seenNodes = new HashSet<int> { elem.StartNodeId };
            var chain     = new List<int>    { elem.StartNodeId };
            foreach (var h in hits)
            {
                if (h.nodeId == elem.StartNodeId || h.nodeId == elem.EndNodeId) continue;
                if (seenNodes.Add(h.nodeId)) chain.Add(h.nodeId);
            }
            chain.Add(elem.EndNodeId);

            // Build valid segments
            var segs = new List<(int n1, int n2)>();
            for (int i = 0; i < chain.Count - 1; i++)
            {
                int ca = chain[i], cb = chain[i + 1];
                if (ca == cb) continue;
                if (!nodeById.TryGetValue(ca, out var pa) || !nodeById.TryGetValue(cb, out var pb)) continue;
                if (pa.Position.DistanceTo(pb.Position) < MinSegLen) continue;
                segs.Add((ca, cb));
            }

            if (segs.Count <= 1) continue; // no actual split

            model.Elements.RemoveAll(e => e.Id == eid);

            // First segment reuses original ID
            model.Elements.Add(new BeamElement(eid, segs[0].n1, segs[0].n2,
                elem.PropertyId, elem.Category, elem.Orientation,
                elem.SourceName, elem.ParentElementId));
            ctx.AddTrace(TraceAction.ElementSplit, "Intersection",
                elementId: eid, relatedElementId: elem.ParentElementId ?? eid,
                note: $"split into {segs.Count} segments");

            for (int i = 1; i < segs.Count; i++)
            {
                int childId = model.AllocElemId();
                model.Elements.Add(new BeamElement(childId, segs[i].n1, segs[i].n2,
                    elem.PropertyId, elem.Category, elem.Orientation,
                    elem.SourceName, eid));
                ctx.AddTrace(TraceAction.ElementSplit, "Intersection",
                    elementId: childId, relatedElementId: eid,
                    note: $"split into {segs.Count} segments");
            }

            splitCount++;
        }

        return splitCount;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<(int nodeId, double u)> MergeCloseByU(
        List<(int nodeId, double u)> hits, double mergeTolAlong, double segLen)
    {
        if (hits.Count <= 1 || segLen < 1e-18) return hits;
        var merged = new List<(int nodeId, double u)> { hits[0] };
        var cur    = hits[0];
        for (int i = 1; i < hits.Count; i++)
        {
            if (Math.Abs(hits[i].u - cur.u) * segLen > mergeTolAlong)
            {
                cur = hits[i];
                merged.Add(cur);
            }
        }
        return merged;
    }

    private static void AddSplit(Dictionary<int, List<(int, double)>> map, int eid, int nodeId, double u)
    {
        if (!map.TryGetValue(eid, out var list)) { list = []; map[eid] = list; }
        list.Add((nodeId, u));
    }

    private static (long, long, long) Quant(Point3 p)
        => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000), (long)Math.Round(p.Z * 1000));

}
