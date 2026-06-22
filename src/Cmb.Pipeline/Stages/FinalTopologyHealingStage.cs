using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// FinalMeshSize 이후 새로 생성된 요소가 기존 노드를 지나가며 생기는 연결성 누락을 보정합니다.
/// 끝점 근처 hit는 짧은 요소를 만들지 않고 endpoint를 기존 노드로 병합하고,
/// 나머지 내부 hit는 기존 SplitByExistingNodes 로 분할합니다.
/// </summary>
public sealed class FinalTopologyHealingStage : IPipelineStage
{
    public string Name => "FinalTopologyHealing";

    private const double ParamTol = 1e-9;
    private const double MinSegLen = 1e-6;

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        int merged = MergeEndpointsNearExistingInlineNodes(ctx);
        new SplitByExistingNodesStage().Execute(ctx);

        if (merged > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FINAL_TOPOLOGY_HEALED",
                $"{merged} endpoint node(s) merged into existing inline node(s) after final mesh sizing.");

        return true;
    }

    private static int MergeEndpointsNearExistingInlineNodes(StageContext ctx)
    {
        var model = ctx.Model;
        double onSegmentTol = ctx.Options.Tolerances.NodeOnSegmentTolMm;
        double endpointTol = ctx.Options.Tolerances.ShortElemMinMm;
        double cell = ctx.Options.Tolerances.SpatialCellSizeMm;

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var elemById = model.Elements.ToDictionary(e => e.Id);
        var elemHash = new ElementSpatialHash(model.Elements, nodeById, cell, onSegmentTol);
        var candidates = new Dictionary<int, (int keepNodeId, double gap)>();

        foreach (var node in model.Nodes)
        {
            var pos = node.Position;
            var boxMin = new Point3(pos.X - onSegmentTol, pos.Y - onSegmentTol, pos.Z - onSegmentTol);
            var boxMax = new Point3(pos.X + onSegmentTol, pos.Y + onSegmentTol, pos.Z + onSegmentTol);

            foreach (int eid in elemHash.QueryBox(boxMin, boxMax))
            {
                if (!elemById.TryGetValue(eid, out var elem)) continue;
                if (elem.StartNodeId == node.Id || elem.EndNodeId == node.Id) continue;
                if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
                if (!nodeById.TryGetValue(elem.EndNodeId, out var nB)) continue;

                double segLen = nA.Position.DistanceTo(nB.Position);
                if (segLen < MinSegLen) continue;

                var (_, t, dist) = ProjectionUtils.ClosestPointOnSegment(pos, nA.Position, nB.Position);
                if (dist > onSegmentTol) continue;
                if (t <= ParamTol || t >= 1.0 - ParamTol) continue;

                double fromStart = t * segLen;
                double fromEnd = (1.0 - t) * segLen;

                if (fromStart < endpointTol)
                    AddCandidate(candidates, elem.StartNodeId, node.Id, fromStart);
                if (fromEnd < endpointTol)
                    AddCandidate(candidates, elem.EndNodeId, node.Id, fromEnd);
            }
        }

        if (candidates.Count == 0) return 0;

        var rawNodeRemap = candidates
            .Where(kv => kv.Key != kv.Value.keepNodeId)
            .ToDictionary(kv => kv.Key, kv => kv.Value.keepNodeId);

        var nodeRemap = BuildAcyclicNodeRemap(rawNodeRemap);

        if (nodeRemap.Count == 0) return 0;

        RemapElements(ctx, nodeRemap);
        RemapRigids(model, nodeRemap);
        RemapPointMasses(model, nodeRemap);
        RemoveMergedNodes(ctx, nodeRemap);

        return nodeRemap.Count;
    }

    private static void AddCandidate(
        Dictionary<int, (int keepNodeId, double gap)> candidates,
        int endpointNodeId,
        int inlineNodeId,
        double gap)
    {
        if (!candidates.TryGetValue(endpointNodeId, out var existing) || gap < existing.gap)
            candidates[endpointNodeId] = (inlineNodeId, gap);
    }

    private static Dictionary<int, int> BuildAcyclicNodeRemap(Dictionary<int, int> rawNodeRemap)
    {
        if (rawNodeRemap.Count == 0) return [];

        var adjacency = new Dictionary<int, HashSet<int>>();
        foreach (var (from, to) in rawNodeRemap)
        {
            AddEdge(from, to);
        }

        var result = new Dictionary<int, int>();
        var visited = new HashSet<int>();
        foreach (int start in adjacency.Keys.OrderBy(id => id))
        {
            if (!visited.Add(start)) continue;

            var component = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                component.Add(node);
                foreach (int next in adjacency[node])
                {
                    if (visited.Add(next))
                        stack.Push(next);
                }
            }

            var terminalKeeps = component
                .Where(id => !rawNodeRemap.ContainsKey(id))
                .OrderBy(id => id)
                .ToList();
            int keep = terminalKeeps.Count > 0
                ? terminalKeeps[0]
                : component.Min();

            foreach (int node in component.OrderBy(id => id))
            {
                if (node != keep)
                    result[node] = keep;
            }
        }

        return result;

        void AddEdge(int a, int b)
        {
            if (!adjacency.TryGetValue(a, out var aSet))
            {
                aSet = [];
                adjacency[a] = aSet;
            }
            if (!adjacency.TryGetValue(b, out var bSet))
            {
                bSet = [];
                adjacency[b] = bSet;
            }
            aSet.Add(b);
            bSet.Add(a);
        }
    }

    private static void RemapElements(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model = ctx.Model;
        var toRemove = new HashSet<int>();
        var toCreate = new List<(int parentId, BeamElement created)>();

        foreach (var elem in model.Elements)
        {
            int start = Resolve(nodeRemap, elem.StartNodeId);
            int end = Resolve(nodeRemap, elem.EndNodeId);

            if (start == end)
            {
                toRemove.Add(elem.Id);
                ctx.AddTrace(TraceAction.ElementRemoved, "FinalTopologyHealing",
                    elementId: elem.Id, note: "degenerate after endpoint topology merge");
            }
            else if (start != elem.StartNodeId || end != elem.EndNodeId)
            {
                int childId = model.AllocElemId();
                toRemove.Add(elem.Id);
                toCreate.Add((elem.Id, new BeamElement(childId, start, end,
                    elem.PropertyId, elem.Category, elem.Orientation,
                    elem.SourceName, elem.ParentElementId ?? elem.Id)));
                ctx.AddTrace(TraceAction.ElementRemoved, "FinalTopologyHealing",
                    elementId: elem.Id, note: "endpoint topology merge changed element endpoints");
            }
        }

        if (toRemove.Count > 0)
            model.Elements.RemoveAll(e => toRemove.Contains(e.Id));

        foreach (var (parentId, created) in toCreate)
        {
            model.Elements.Add(created);
            ctx.AddTrace(TraceAction.ElementCreated, "FinalTopologyHealing",
                elementId: created.Id, relatedElementId: parentId,
                note: "recreated after endpoint topology merge");
        }
    }

    private static void RemapRigids(FeModel model, Dictionary<int, int> nodeRemap)
    {
        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rigid = model.Rigids[i];
            int indep = Resolve(nodeRemap, rigid.IndependentNodeId);
            var deps = rigid.DependentNodeIds
                .Select(d => Resolve(nodeRemap, d))
                .Where(d => d != indep)
                .Distinct()
                .ToList();

            model.Rigids[i] = new RigidElement(rigid.Id, indep, deps,
                rigid.Remark, rigid.SourceName, rigid.Cm, rigid.DetailRemark);
        }

        model.Rigids.RemoveAll(r => r.DependentNodeIds.Count == 0);
    }

    private static void RemapPointMasses(FeModel model, Dictionary<int, int> nodeRemap)
    {
        for (int i = 0; i < model.PointMasses.Count; i++)
        {
            var pm = model.PointMasses[i];
            int nodeId = Resolve(nodeRemap, pm.NodeId);
            if (nodeId != pm.NodeId)
                model.PointMasses[i] = new PointMass(pm.Id, nodeId, pm.Mass, pm.SourceName);
        }
    }

    private static void RemoveMergedNodes(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model = ctx.Model;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);

        foreach (var (removedId, keepId) in nodeRemap)
        {
            if (nodeById.TryGetValue(removedId, out var removed) &&
                nodeById.TryGetValue(keepId, out var kept))
            {
                if (removed.Tags != NodeTags.None)
                    kept.AddTag(removed.Tags);
                kept.AddTag(NodeTags.Merged);
            }

            ctx.AddTrace(TraceAction.NodeMerged, "FinalTopologyHealing",
                nodeId: removedId, relatedNodeId: keepId,
                note: "endpoint merged into existing inline node after final mesh sizing");
        }

        model.Nodes.RemoveAll(n => nodeRemap.ContainsKey(n.Id));
    }

    private static int Resolve(Dictionary<int, int> nodeRemap, int nodeId)
    {
        var seen = new HashSet<int>();
        while (nodeRemap.TryGetValue(nodeId, out int next) && seen.Add(nodeId))
            nodeId = next;
        return nodeId;
    }
}
