using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 거의 동일한 축 방향(CollinearMergeAngleDeg 이내)을 가진 두 요소의 끝점이
/// CollinearMergeDistanceMm 이내이고 축 수직 편차(lateral)가 CollinearMergeLateralMm 미만이면
/// 두 끝점 노드를 하나로 병합합니다.
/// HiTess ElementCollinearNodeMergeModifier에 대응합니다.
/// </summary>
public sealed class CollinearNodeMergeStage : IPipelineStage
{
    public string Name => "CollinearNodeMerge";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        if (model.Elements.Count < 2) return true;

        var tol       = ctx.Options.Tolerances;
        double distTol    = tol.CollinearMergeDistanceMm;
        double lateralTol = tol.CollinearMergeLateralMm;
        double cosTol     = Math.Cos(tol.CollinearMergeAngleDeg * Math.PI / 180.0);
        double cellSize   = tol.SpatialCellSizeMm;

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var elemHash = new ElementSpatialHash(model.Elements, nodeById, cellSize, distTol);

        var pairs = new List<(int a, int b)>();

        foreach (var e1 in model.Elements)
        {
            if (!nodeById.TryGetValue(e1.StartNodeId, out var s1)) continue;
            if (!nodeById.TryGetValue(e1.EndNodeId,   out var t1)) continue;

            var span1 = t1.Position - s1.Position;
            if (span1.LengthSquared < 1e-18) continue;
            var dir1 = span1.Normalize();

            foreach (int eid2 in elemHash.QueryCandidates(e1.Id))
            {
                if (eid2 <= e1.Id) continue;

                var e2 = model.Elements.Find(x => x.Id == eid2);
                if (e2 is null) continue;
                if (e2.Category != e1.Category) continue;

                if (!nodeById.TryGetValue(e2.StartNodeId, out var s2)) continue;
                if (!nodeById.TryGetValue(e2.EndNodeId,   out var t2)) continue;

                var span2 = t2.Position - s2.Position;
                if (span2.LengthSquared < 1e-18) continue;
                var dir2 = span2.Normalize();

                if (Math.Abs(Vector3.Dot(dir1, dir2)) < cosTol) continue;

                TryPair(pairs, s1, s2, dir1, distTol, lateralTol);
                TryPair(pairs, s1, t2, dir1, distTol, lateralTol);
                TryPair(pairs, t1, s2, dir1, distTol, lateralTol);
                TryPair(pairs, t1, t2, dir1, distTol, lateralTol);
            }

            // 각 끝점과 Rigid 독립/종속 노드 병합 체크
            foreach (var r in model.Rigids)
            {
                TryPairWithRigidNode(pairs, s1, r.IndependentNodeId, nodeById, dir1, distTol, lateralTol);
                TryPairWithRigidNode(pairs, t1, r.IndependentNodeId, nodeById, dir1, distTol, lateralTol);
                foreach (int depId in r.DependentNodeIds)
                {
                    TryPairWithRigidNode(pairs, s1, depId, nodeById, dir1, distTol, lateralTol);
                    TryPairWithRigidNode(pairs, t1, depId, nodeById, dir1, distTol, lateralTol);
                }
            }
        }

        if (pairs.Count == 0) return true;

        var uf = new UnionFind(nodeById.Keys);
        foreach (var (a, b) in pairs) uf.Union(a, b);

        var nodeRemap = new Dictionary<int, int>();
        foreach (var group in uf.GetGroups())
        {
            if (group.Count < 2) continue;
            int keepId = group[0]; // 이미 오름차순 → 최소 ID
            for (int k = 1; k < group.Count; k++)
                nodeRemap[group[k]] = keepId;
        }

        if (nodeRemap.Count == 0) return true;

        RemapElements(ctx, nodeRemap);
        RemapRigids(model, nodeRemap);
        RemapPointMasses(model, nodeRemap);
        TransferTagsAndRemoveNodes(ctx, nodeRemap);

        ctx.AddDiagnostic(DiagnosticSeverity.Info, "COLLINEAR_NODES_MERGED",
            $"{nodeRemap.Count} node(s) merged along co-axial endpoints.");

        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void TryPair(
        List<(int, int)> pairs,
        Node a, Node b,
        Vector3 axisDir, double distTol, double lateralTol)
    {
        if (a.Id == b.Id) return;
        var diff = b.Position - a.Position;
        if (diff.LengthSquared > distTol * distTol) return;

        double along   = Vector3.Dot(diff, axisDir);
        var projected  = axisDir * along;
        double lateral = (diff - projected).Length;
        if (lateral >= lateralTol) return;

        pairs.Add((Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id)));
    }

    private static void TryPairWithRigidNode(
        List<(int, int)> pairs,
        Node elemEndpoint, int rigidNodeId,
        Dictionary<int, Node> nodeById,
        Vector3 axisDir, double distTol, double lateralTol)
    {
        if (!nodeById.TryGetValue(rigidNodeId, out var rigidNode)) return;
        TryPair(pairs, elemEndpoint, rigidNode, axisDir, distTol, lateralTol);
    }

    private static void RemapElements(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model     = ctx.Model;
        var toRemove  = new List<int>();
        var toReplace = new List<(int, BeamElement)>();

        for (int i = 0; i < model.Elements.Count; i++)
        {
            var elem  = model.Elements[i];
            int start = nodeRemap.GetValueOrDefault(elem.StartNodeId, elem.StartNodeId);
            int end   = nodeRemap.GetValueOrDefault(elem.EndNodeId,   elem.EndNodeId);

            if (start == end)
            {
                toRemove.Add(elem.Id);
                ctx.AddTrace(TraceAction.ElementRemoved, "CollinearNodeMerge",
                    elementId: elem.Id, note: "degenerate after collinear node merge");
            }
            else if (start != elem.StartNodeId || end != elem.EndNodeId)
            {
                toReplace.Add((i, new BeamElement(elem.Id, start, end, elem.PropertyId,
                    elem.Category, elem.Orientation, elem.SourceName, elem.ParentElementId)));
            }
        }

        foreach (int id in toRemove)
            model.Elements.RemoveAll(e => e.Id == id);

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

    private static void RemapPointMasses(FeModel model, Dictionary<int, int> nodeRemap)
    {
        for (int i = 0; i < model.PointMasses.Count; i++)
        {
            var pm = model.PointMasses[i];
            if (nodeRemap.TryGetValue(pm.NodeId, out int newId))
                model.PointMasses[i] = new PointMass(pm.Id, newId, pm.Mass, pm.SourceName);
        }
    }

    private static void TransferTagsAndRemoveNodes(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model = ctx.Model;
        foreach (var (removedId, keepId) in nodeRemap)
        {
            var removed = model.Nodes.Find(n => n.Id == removedId);
            var kept    = model.Nodes.Find(n => n.Id == keepId);
            if (removed is not null && kept is not null && removed.Tags != NodeTags.None)
                kept.AddTag(removed.Tags);

            ctx.AddTrace(TraceAction.NodeMerged, "CollinearNodeMerge",
                nodeId: removedId, relatedNodeId: keepId,
                note: "co-axial endpoint merge");
            model.Nodes.RemoveAll(n => n.Id == removedId);
        }
    }
}
