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

        var tol           = ctx.Options.Tolerances;
        double distTol    = tol.CollinearMergeDistanceMm;
        double lateralTol = tol.CollinearMergeLateralMm;
        double inlineRigidLateralTol = tol.InlineRigidEndpointSnapLateralMm;
        double cosTol     = Math.Cos(tol.CollinearMergeAngleDeg * Math.PI / 180.0);
        double cellSize   = tol.SpatialCellSizeMm;

        var nodeById    = model.Nodes.ToDictionary(n => n.Id);
        var elemById    = model.Elements.ToDictionary(e => e.Id);
        var sectionById = model.Sections.ToDictionary(s => s.Id);
        var elemHash    = new ElementSpatialHash(model.Elements, nodeById, cellSize, distTol);

        // Rigid 노드만 별도 spatial hash로 인덱싱하여 endpoint 근방만 검사
        var rigidNodeIds = new HashSet<int>(
            model.Rigids.SelectMany(r => r.DependentNodeIds.Append(r.IndependentNodeId)));
        var rigidNodes = model.Nodes.Where(n => rigidNodeIds.Contains(n.Id)).ToList();
        var rigidHash  = new NodeSpatialHash(rigidNodes, cellSize);
        var inlineDependentNodeIds = model.Rigids
            .Where(IsInlineRigid)
            .SelectMany(r => r.DependentNodeIds)
            .ToHashSet();
        var beamNodeDegree = BuildBeamNodeDegree(model);

        var pairs = new HashSet<(int a, int b)>();

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
                if (!elemById.TryGetValue(eid2, out var e2)) continue;
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

            // Inline-rigid(VALV/TRAP/FILT/EXP) dep 노드는 파이프 단면 내부 어디에 있든 기하학적으로
            // "파이프 위" 이므로 lateral 임계를 max(InlineRigidEndpointSnapLateralMm, 단면 최대치수 + 1mm) 로 확장.
            double inlineLateralEffective = inlineRigidLateralTol;
            if (e1.Category == EntityCategory.Pipe &&
                sectionById.TryGetValue(e1.PropertyId, out var sec1))
            {
                inlineLateralEffective = Math.Max(inlineLateralEffective, sec1.MaxCrossSectionDim() + 1.0);
            }

            // Rigid 노드 페어링: endpoint 근방의 rigid 노드만 검사
            foreach (int rid in rigidHash.QueryNear(s1.Position, distTol))
            {
                if (!nodeById.TryGetValue(rid, out var rigidNode)) continue;
                TryPair(pairs, s1, rigidNode, dir1, distTol, lateralTol);
                TryInlineRigidEndpointPair(pairs, e1, s1, rigidNode, dir1,
                    inlineDependentNodeIds, beamNodeDegree, distTol, inlineLateralEffective);
            }
            foreach (int rid in rigidHash.QueryNear(t1.Position, distTol))
            {
                if (!nodeById.TryGetValue(rid, out var rigidNode)) continue;
                TryPair(pairs, t1, rigidNode, dir1, distTol, lateralTol);
                TryInlineRigidEndpointPair(pairs, e1, t1, rigidNode, dir1,
                    inlineDependentNodeIds, beamNodeDegree, distTol, inlineLateralEffective);
            }
        }

        if (pairs.Count == 0) return true;

        // 가까운 쌍부터 묶되, 한 덩어리의 크기(AABB 대각선)가 distTol 을 넘으면 묶지 않는다.
        // 쌍마다 50mm 이내여도 Union-Find 가 사슬로 이어 붙이면(40mm 간격 가로재 여러 개) 덩어리가
        // 50mm 를 넘어 한 점으로 붕괴하고 접합 위치가 수십 mm 씩 옮겨진다.
        var uf = new UnionFind(nodeById.Keys);
        var boxes = new Dictionary<int, (Point3 Min, Point3 Max)>();
        (Point3 Min, Point3 Max) BoxOf(int root)
        {
            if (boxes.TryGetValue(root, out var b)) return b;
            var p = nodeById[root].Position;
            return (p, p);
        }
        foreach (var (a, b) in pairs
                     .OrderBy(pr => nodeById[pr.a].Position.DistanceTo(nodeById[pr.b].Position))
                     .ThenBy(pr => pr.a).ThenBy(pr => pr.b))
        {
            int ra = uf.Find(a), rb = uf.Find(b);
            if (ra == rb) continue;
            var (minA, maxA) = BoxOf(ra);
            var (minB, maxB) = BoxOf(rb);
            var min = new Point3(Math.Min(minA.X, minB.X), Math.Min(minA.Y, minB.Y), Math.Min(minA.Z, minB.Z));
            var max = new Point3(Math.Max(maxA.X, maxB.X), Math.Max(maxA.Y, maxB.Y), Math.Max(maxA.Z, maxB.Z));
            if (min.DistanceTo(max) > distTol) continue;
            uf.Union(a, b);
            boxes[uf.Find(a)] = (min, max);
        }

        var clusters = uf.GetGroups().Where(g => g.Count >= 2).ToList();
        var nodeRemap = new Dictionary<int, int>();
        foreach (var group in clusters)
        {
            int keepId = group[0]; // 이미 오름차순 → 최소 ID
            for (int k = 1; k < group.Count; k++)
                nodeRemap[group[k]] = keepId;
        }

        if (nodeRemap.Count == 0) return true;

        RemapElements(ctx, nodeRemap);
        RemapRigids(model, nodeRemap);
        RemapPointMasses(model, nodeRemap);
        TransferTagsAndRemoveNodes(ctx, nodeRemap);

        double avgClusterSize = (double)nodeRemap.Count / clusters.Count + 1.0;
        ctx.AddDiagnostic(DiagnosticSeverity.Info, "COLLINEAR_NODES_MERGED",
            $"{nodeRemap.Count} node(s) merged across {clusters.Count} cluster(s) " +
            $"(avg {avgClusterSize:F2} nodes/cluster).");

        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void TryPair(
        HashSet<(int, int)> pairs,
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

    private static void TryInlineRigidEndpointPair(
        HashSet<(int, int)> pairs,
        BeamElement element,
        Node endpoint,
        Node rigidNode,
        Vector3 axisDir,
        HashSet<int> inlineDependentNodeIds,
        Dictionary<int, int> beamNodeDegree,
        double distTol,
        double lateralTol)
    {
        if (element.Category != EntityCategory.Pipe) return;
        if (endpoint.Id == rigidNode.Id) return;
        if (!inlineDependentNodeIds.Contains(rigidNode.Id)) return;
        if (beamNodeDegree.GetValueOrDefault(endpoint.Id) != 1) return;

        var diff = rigidNode.Position - endpoint.Position;
        if (diff.LengthSquared > distTol * distTol) return;

        double along   = Vector3.Dot(diff, axisDir);
        var projected  = axisDir * along;
        double lateral = (diff - projected).Length;
        if (lateral > lateralTol) return;

        pairs.Add((Math.Min(endpoint.Id, rigidNode.Id), Math.Max(endpoint.Id, rigidNode.Id)));
    }

    private static Dictionary<int, int> BuildBeamNodeDegree(FeModel model)
    {
        var degree = new Dictionary<int, int>();
        foreach (var e in model.Elements)
        {
            degree[e.StartNodeId] = degree.GetValueOrDefault(e.StartNodeId) + 1;
            degree[e.EndNodeId]   = degree.GetValueOrDefault(e.EndNodeId) + 1;
        }
        return degree;
    }

    private static bool IsInlineRigid(RigidElement rigid)
        => rigid.Remark is "VALV" or "TRAP" or "FILT" or "EXP";

    private static void RemapElements(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model       = ctx.Model;
        var toRemoveSet = new HashSet<int>();
        var toCreate    = new List<(int ParentId, BeamElement Created)>();

        for (int i = 0; i < model.Elements.Count; i++)
        {
            var elem  = model.Elements[i];
            int start = nodeRemap.GetValueOrDefault(elem.StartNodeId, elem.StartNodeId);
            int end   = nodeRemap.GetValueOrDefault(elem.EndNodeId,   elem.EndNodeId);

            if (start == end)
            {
                toRemoveSet.Add(elem.Id);
                ctx.AddTrace(TraceAction.ElementRemoved, "CollinearNodeMerge",
                    elementId: elem.Id, note: "degenerate after collinear node merge");
            }
            else if (start != elem.StartNodeId || end != elem.EndNodeId)
            {
                int childId = model.AllocElemId();
                toRemoveSet.Add(elem.Id);
                toCreate.Add((elem.Id, new BeamElement(childId, start, end, elem.PropertyId,
                    elem.Category, elem.Orientation, elem.SourceName, elem.Id)));
                ctx.AddTrace(TraceAction.ElementRemoved, "CollinearNodeMerge",
                    elementId: elem.Id, note: "collinear node merge changed element endpoints");
            }
        }

        if (toRemoveSet.Count > 0)
            model.Elements.RemoveAll(e => toRemoveSet.Contains(e.Id));

        foreach (var (parentId, created) in toCreate)
        {
            model.Elements.Add(created);
            ctx.AddTrace(TraceAction.ElementCreated, "CollinearNodeMerge",
                elementId: created.Id, relatedElementId: parentId,
                note: "recreated after collinear node merge");
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

            // dep가 모두 indep로 merge되어 비면 RBE2 자체를 제거 (BDF에서 무효).
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

    private static void TransferTagsAndRemoveNodes(StageContext ctx, Dictionary<int, int> nodeRemap)
    {
        var model    = ctx.Model;
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

            ctx.AddTrace(TraceAction.NodeMerged, "CollinearNodeMerge",
                nodeId: removedId, relatedNodeId: keepId,
                note: "co-axial endpoint merge");
        }

        model.Nodes.RemoveAll(n => nodeRemap.ContainsKey(n.Id));
    }
}
