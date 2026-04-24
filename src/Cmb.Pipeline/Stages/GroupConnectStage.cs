using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

public sealed class GroupConnectStage : IPipelineStage
{
    public string Name => "GroupConnect";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();
        if (model.Elements.Count == 0) return true;

        // Assign elements to connectivity groups via UnionFind on node IDs
        var allNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .Distinct()
            .ToList();
        if (allNodeIds.Count == 0) return true;

        var uf = new UnionFind(allNodeIds);
        foreach (var e in model.Elements) uf.Union(e.StartNodeId, e.EndNodeId);

        var elemsByRoot = model.Elements
            .GroupBy(e => uf.Find(e.StartNodeId))
            .OrderByDescending(g => g.Count())
            .ToList();

        if (elemsByRoot.Count <= 1) return true;

        var masterElems   = elemsByRoot[0].ToList();
        var nodeById      = model.Nodes.ToDictionary(n => n.Id);
        double snapTol    = ctx.Options.Tolerances.GroupConnectSnapTolMm;
        double mergeTol   = ctx.Options.Tolerances.NodeMergeMm;
        int connectedCount = 0;

        foreach (var slaveGroup in elemsByRoot.Skip(1))
        {
            var slaveElems = slaveGroup.ToList();

            // Pipe-only groups are connected by UboltRbeStage — skip here
            if (slaveElems.All(e => e.Category == EntityCategory.Pipe)) continue;

            // Compute node degrees within this slave group
            var degrees = new Dictionary<int, int>();
            foreach (var e in slaveElems)
            {
                degrees[e.StartNodeId] = degrees.GetValueOrDefault(e.StartNodeId) + 1;
                degrees[e.EndNodeId]   = degrees.GetValueOrDefault(e.EndNodeId)   + 1;
            }
            var freeEnds = degrees.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
            if (freeEnds.Count == 0) freeEnds = degrees.Keys.ToList(); // closed group — try any node

            // Find best (free-end node, master projection) pair
            double bestDist   = double.MaxValue;
            int bestFreeNode  = -1;
            Point3 bestProj   = default;

            foreach (int fnid in freeEnds)
            {
                if (!nodeById.TryGetValue(fnid, out var freeNode)) continue;
                foreach (var masterElem in masterElems)
                {
                    if (!nodeById.TryGetValue(masterElem.StartNodeId, out var mA)) continue;
                    if (!nodeById.TryGetValue(masterElem.EndNodeId,   out var mB)) continue;
                    var (proj, _, dist) = ProjectionUtils.ClosestPointOnSegment(
                        freeNode.Position, mA.Position, mB.Position);
                    if (dist < bestDist) { bestFreeNode = fnid; bestProj = proj; bestDist = dist; }
                }
            }

            if (bestFreeNode == -1 || bestDist > snapTol)
            {
                int slaveRootNode = degrees.Keys.Min();
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "GROUP_NOT_CONNECTED",
                    $"Disconnected group (node {slaveRootNode}, {slaveElems.Count} elem(s)) is {bestDist:F1} mm from master — too far to snap (limit {snapTol:F1} mm).");
                continue;
            }

            // Reuse existing master node if projection is within merge tolerance
            var existing = nodeById.Values
                .FirstOrDefault(n => n.Position.DistanceTo(bestProj) <= mergeTol);
            int snapNodeId;
            if (existing is not null)
            {
                snapNodeId = existing.Id;
            }
            else
            {
                snapNodeId = model.AllocNodeId();
                var snapNode = new Node(snapNodeId, bestProj);
                model.Nodes.Add(snapNode);
                nodeById[snapNodeId] = snapNode;
            }

            // Remap free-end → snap node in all slave elements
            var slaveElemIds = slaveElems.Select(e => e.Id).ToHashSet();
            for (int i = 0; i < model.Elements.Count; i++)
            {
                var e = model.Elements[i];
                if (!slaveElemIds.Contains(e.Id)) continue;
                if (e.StartNodeId != bestFreeNode && e.EndNodeId != bestFreeNode) continue;

                int start = e.StartNodeId == bestFreeNode ? snapNodeId : e.StartNodeId;
                int end   = e.EndNodeId   == bestFreeNode ? snapNodeId : e.EndNodeId;
                model.Elements[i] = new BeamElement(e.Id, start, end, e.PropertyId,
                    e.Category, e.Orientation, e.SourceName, e.ParentElementId);
            }

            // Remove old free-end node (only if not referenced by any other element)
            bool stillReferenced = model.Elements.Any(e =>
                e.StartNodeId == bestFreeNode || e.EndNodeId == bestFreeNode);
            if (!stillReferenced)
                model.Nodes.RemoveAll(n => n.Id == bestFreeNode);

            connectedCount++;
        }

        if (connectedCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "GROUPS_CONNECTED",
                $"{connectedCount} slave group(s) snapped to master.");

        return true;
    }
}
