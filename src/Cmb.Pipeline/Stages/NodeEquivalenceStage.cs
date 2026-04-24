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
        RemoveNodes(ctx, nodeRemap, tol);

        ctx.AddDiagnostic(DiagnosticSeverity.Info, "NODES_MERGED",
            $"{nodeRemap.Count} node(s) merged within {tol} mm tolerance.");

        return true;
    }

    private static void RemapElements(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model = ctx.Model;
        var toRemove = new List<int>();
        var toReplace = new List<(int Index, BeamElement Updated)>();

        for (int i = 0; i < model.Elements.Count; i++)
        {
            var elem = model.Elements[i];
            int start = nodeRemap.GetValueOrDefault(elem.StartNodeId, elem.StartNodeId);
            int end   = nodeRemap.GetValueOrDefault(elem.EndNodeId,   elem.EndNodeId);

            if (start == end)
            {
                toRemove.Add(elem.Id);
                ctx.AddTrace(TraceAction.ElementRemoved, "NodeEquivalence",
                    elementId: elem.Id, note: "degenerate after node merge");
            }
            else if (start != elem.StartNodeId || end != elem.EndNodeId)
            {
                toReplace.Add((i, new BeamElement(elem.Id, start, end, elem.PropertyId,
                    elem.Category, elem.Orientation, elem.SourceName, elem.ParentElementId)));
            }
        }

        foreach (int id in toRemove)
            model.Elements.RemoveAll(e => e.Id == id);

        // Apply replacements after removals (indices may have shifted — use FindIndex)
        foreach (var (_, updated) in toReplace)
        {
            int idx = model.Elements.FindIndex(e => e.Id == updated.Id);
            if (idx >= 0) model.Elements[idx] = updated;
        }
    }

    private static void RemapRigids(FeModel model, Dictionary<int, int> nodeRemap)
    {
        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rigid = model.Rigids[i];
            int indep = nodeRemap.GetValueOrDefault(rigid.IndependentNodeId, rigid.IndependentNodeId);
            var deps  = rigid.DependentNodeIds
                .Select(d => nodeRemap.GetValueOrDefault(d, d))
                .Where(d => d != indep)
                .Distinct()
                .ToList();

            if (indep != rigid.IndependentNodeId || !deps.SequenceEqual(rigid.DependentNodeIds))
                model.Rigids[i] = new RigidElement(rigid.Id, indep, deps, rigid.Remark, rigid.SourceName);
        }
    }

    private static void RemoveNodes(StageContext ctx, Dictionary<int, int> nodeRemap, double tol)
    {
        var model = ctx.Model;
        foreach (var (removedId, keepId) in nodeRemap)
        {
            // Transfer tags (e.g. NodeTags.Weld) from removed node to kept node
            var removed = model.Nodes.Find(n => n.Id == removedId);
            var kept    = model.Nodes.Find(n => n.Id == keepId);
            if (removed is not null && kept is not null && removed.Tags != NodeTags.None)
                kept.AddTag(removed.Tags);

            ctx.AddTrace(TraceAction.NodeMerged, "NodeEquivalence",
                nodeId: removedId, relatedNodeId: keepId,
                note: $"merged within {tol} mm");
            model.Nodes.RemoveAll(n => n.Id == removedId);
        }
    }
}
