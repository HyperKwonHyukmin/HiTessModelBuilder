using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

public sealed class NodeEquivalenceStage : IPipelineStage
{
    public string Name => "NodeEquivalence";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();
        double tol = ctx.Options.Tolerances.NodeMergeMm;
        double tolSq = tol * tol;

        // Sweep-and-prune: sort by X, break inner loop when X gap > tol
        var sorted = model.Nodes.OrderBy(n => n.Position.X).ToList();
        if (sorted.Count < 2) return true;

        var uf = new UnionFind(sorted.Select(n => n.Id));

        for (int i = 0; i < sorted.Count; i++)
        {
            var pi = sorted[i].Position;
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (sorted[j].Position.X - pi.X > tol) break;
                if ((sorted[j].Position - pi).LengthSquared <= tolSq)
                    uf.Union(sorted[i].Id, sorted[j].Id);
            }
        }

        // Build remap: removed node → kept node (min ID per group)
        var nodeRemap = new Dictionary<int, int>();
        foreach (var group in uf.GetGroups())
        {
            if (group.Count < 2) continue;
            int keepId = group[0]; // groups are sorted ascending → min ID first
            for (int k = 1; k < group.Count; k++)
                nodeRemap[group[k]] = keepId;
        }

        if (nodeRemap.Count == 0) return true;

        RemapElements(ctx, nodeRemap);
        RemapRigids(model, nodeRemap);
        RemapPointMasses(model, nodeRemap);
        RemoveNodes(ctx, nodeRemap, tol);

        ctx.AddDiagnostic(DiagnosticSeverity.Info, "NODES_MERGED",
            $"{nodeRemap.Count} node(s) merged within {tol} mm tolerance.");

        return true;
    }

    private static void RemapElements(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model = ctx.Model;
        var toRemoveSet = new HashSet<int>();
        var toCreate = new List<(int ParentId, BeamElement Created)>();

        for (int i = 0; i < model.Elements.Count; i++)
        {
            var elem = model.Elements[i];
            int start = nodeRemap.GetValueOrDefault(elem.StartNodeId, elem.StartNodeId);
            int end   = nodeRemap.GetValueOrDefault(elem.EndNodeId,   elem.EndNodeId);

            if (start == end)
            {
                toRemoveSet.Add(elem.Id);
                ctx.AddTrace(TraceAction.ElementRemoved, "NodeEquivalence",
                    elementId: elem.Id, note: "degenerate after node merge");
            }
            else if (start != elem.StartNodeId || end != elem.EndNodeId)
            {
                int childId = model.AllocElemId();
                toRemoveSet.Add(elem.Id);
                toCreate.Add((elem.Id, new BeamElement(childId, start, end, elem.PropertyId,
                    elem.Category, elem.Orientation, elem.SourceName, elem.Id)));
                ctx.AddTrace(TraceAction.ElementRemoved, "NodeEquivalence",
                    elementId: elem.Id, note: "node merge changed element endpoints");
            }
        }

        if (toRemoveSet.Count > 0)
            model.Elements.RemoveAll(e => toRemoveSet.Contains(e.Id));

        foreach (var (parentId, created) in toCreate)
        {
            model.Elements.Add(created);
            ctx.AddTrace(TraceAction.ElementCreated, "NodeEquivalence",
                elementId: created.Id, relatedElementId: parentId,
                note: "recreated after node merge");
        }
    }

    private static void RemapRigids(FeModel model, Dictionary<int, int> nodeRemap)
    {
        var toRemove = new List<int>();
        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rigid = model.Rigids[i];
            int indep = nodeRemap.GetValueOrDefault(rigid.IndependentNodeId, rigid.IndependentNodeId);
            var deps  = rigid.DependentNodeIds
                .Select(d => nodeRemap.GetValueOrDefault(d, d))
                .Where(d => d != indep)
                .Distinct()
                .ToList();

            if (deps.Count == 0 && !IsDeferredBoxUbolt(rigid))
            {
                toRemove.Add(rigid.Id);
                continue;
            }

            if (indep != rigid.IndependentNodeId || !deps.SequenceEqual(rigid.DependentNodeIds))
                model.Rigids[i] = new RigidElement(rigid.Id, indep, deps,
                    rigid.Remark, rigid.SourceName, rigid.Cm, rigid.DetailRemark);
        }

        if (toRemove.Count > 0)
        {
            var removeSet = toRemove.ToHashSet();
            model.Rigids.RemoveAll(r => removeSet.Contains(r.Id));
        }
    }

    private static bool IsDeferredBoxUbolt(RigidElement rigid)
        => rigid.Remark == "UBOLT" &&
           string.Equals(rigid.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase);

    private static void RemapPointMasses(FeModel model, Dictionary<int, int> nodeRemap)
    {
        for (int i = 0; i < model.PointMasses.Count; i++)
        {
            var pm = model.PointMasses[i];
            if (nodeRemap.TryGetValue(pm.NodeId, out int newId))
                model.PointMasses[i] = new PointMass(pm.Id, newId, pm.Mass, pm.SourceName);
        }
    }

    private static void RemoveNodes(StageContext ctx, Dictionary<int, int> nodeRemap, double tol)
    {
        var model = ctx.Model;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);

        foreach (var (removedId, keepId) in nodeRemap)
        {
            // Transfer tags (e.g. NodeTags.Weld) from removed node to kept node
            if (nodeById.TryGetValue(removedId, out var removed) &&
                nodeById.TryGetValue(keepId, out var kept))
            {
                if (removed.Tags != NodeTags.None)
                    kept.AddTag(removed.Tags);
                kept.AddTag(NodeTags.Merged);
            }

            ctx.AddTrace(TraceAction.NodeMerged, "NodeEquivalence",
                nodeId: removedId, relatedNodeId: keepId,
                note: $"merged within {tol} mm");
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "DUPLICATE_NODE",
                $"Node {removedId} duplicates node {keepId} within {tol} mm tolerance and was merged.",
                nodeId: removedId);
        }

        model.Nodes.RemoveAll(n => nodeRemap.ContainsKey(n.Id));
    }
}
