using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 분리된 slave 그룹을 master 그룹 쪽으로 병진 이동하여 연결한다.
/// Meta-group(근접 + 방향 일치 slave 통합) → 그룹 전체 병진 → RBE 폴백(거리 초과 시).
/// </summary>
public sealed class GroupConnectStage : IPipelineStage
{
    public string Name => "GroupConnect";
    public int LastTranslatedCount { get; private set; }
    public int LastSkippedCount { get; private set; }

    public bool Execute(StageContext ctx)
    {
        LastTranslatedCount = 0;
        LastSkippedCount = 0;

        var model     = ctx.Model;
        var tol       = ctx.Options.Tolerances;
        model.SyncIdCounters();

        if (model.Elements.Count == 0) return true;

        // ── 1. 연결 그룹 추출 ─────────────────────────────────────────────

        var allNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .Distinct().ToList();
        if (allNodeIds.Count == 0) return true;

        var uf = new UnionFind(allNodeIds);
        foreach (var e in model.Elements) uf.Union(e.StartNodeId, e.EndNodeId);

        var groups = uf.GetGroups();
        if (groups.Count <= 1) return true;

        // 각 그룹 루트 → 소속 요소
        var elemsByRoot = new Dictionary<int, List<BeamElement>>();
        foreach (var e in model.Elements)
        {
            int root = uf.Find(e.StartNodeId);
            if (!elemsByRoot.TryGetValue(root, out var list)) { list = []; elemsByRoot[root] = list; }
            list.Add(e);
        }

        var sortedRoots = elemsByRoot.OrderByDescending(kv => kv.Value.Count).ToList();
        var masterElems    = sortedRoots[0].Value;
        var masterElemSet  = new HashSet<int>(masterElems.Select(e => e.Id));
        var nodeById       = model.Nodes.ToDictionary(n => n.Id);
        var sectionById    = model.Sections.ToDictionary(s => s.Id);
        var pointMassNodes = new HashSet<int>(model.PointMasses.Select(pm => pm.NodeId));
        var rigidNodes     = new HashSet<int>(
            model.Rigids.SelectMany(r => r.DependentNodeIds.Append(r.IndependentNodeId)));

        // ── 2. Slave 필터: any Pipe 포함 그룹 제외 ───────────────────────

        var filteredSlaves = sortedRoots.Skip(1)
            .Where(kv => kv.Value.All(e => e.Category != EntityCategory.Pipe))
            .Select(kv => kv.Value)
            .ToList();

        if (filteredSlaves.Count == 0) return true;

        // ── 3. 노이즈 제거: 단일 요소 고립 그룹 중 작은 찌꺼기 삭제 ─────────────
        // HiTess ElementGroupTranslationModifier 동일 로직
        // 길이<NoiseLengthMm AND maxDim<NoiseMaxDimMm AND PointMass/RBE 없음 → 영구 삭제

        double noiseLenTol = tol.GroupConnectNoiseLengthMm;
        double noiseDimTol = tol.GroupConnectNoiseMaxDimMm;
        var cleanedSlaves = new List<List<BeamElement>>(filteredSlaves.Count);
        var noiseElemIds  = new HashSet<int>();
        var noiseNodeIds  = new HashSet<int>();

        foreach (var group in filteredSlaves)
        {
            if (group.Count == 1)
            {
                var e      = group[0];
                var pA     = nodeById[e.StartNodeId].Position;
                var pB     = nodeById[e.EndNodeId].Position;
                double len = pA.DistanceTo(pB);
                double maxDim = sectionById.TryGetValue(e.PropertyId, out var sec)
                    ? sec.MaxCrossSectionDim() : 0.0;
                bool hasPointMass = pointMassNodes.Contains(e.StartNodeId) || pointMassNodes.Contains(e.EndNodeId);
                bool hasRigid     = rigidNodes.Contains(e.StartNodeId)     || rigidNodes.Contains(e.EndNodeId);

                if (len < noiseLenTol && maxDim < noiseDimTol && !hasPointMass && !hasRigid)
                {
                    noiseElemIds.Add(e.Id);
                    noiseNodeIds.Add(e.StartNodeId);
                    noiseNodeIds.Add(e.EndNodeId);
                    continue;
                }
            }
            cleanedSlaves.Add(group);
        }

        if (noiseElemIds.Count > 0)
        {
            model.Elements.RemoveAll(x => noiseElemIds.Contains(x.Id));
            // 다른 곳에서 참조되지 않는 노드만 제거
            var stillRef = new HashSet<int>(model.Elements.SelectMany(x => new[] { x.StartNodeId, x.EndNodeId }));
            stillRef.UnionWith(pointMassNodes);
            stillRef.UnionWith(rigidNodes);
            model.Nodes.RemoveAll(n => noiseNodeIds.Contains(n.Id) && !stillRef.Contains(n.Id));
            foreach (int nid in noiseNodeIds) nodeById.Remove(nid);

            ctx.AddDiagnostic(DiagnosticSeverity.Info, "NOISE_REMOVED",
                $"{noiseElemIds.Count} isolated single-element noise fragment(s) deleted before group translation.");
        }

        if (cleanedSlaves.Count == 0) return true;

        // ── 4. Beam topology degree map ───────────────────────────────────
        // Group translation is the primary topology repair. Existing RBE2s
        // must not hide beam free-ends here; otherwise groups that should be
        // pulled into the master are deferred and later connected by RBE.

        var degrees = BuildElementNodeDegree(model);
        var preservedFreeEndPairs = FindPreservedStructureFreeEndPairs(
            model, nodeById, sectionById, degrees, tol);

        // ── 5. Candidate vector 계산 (meta-group 구성용) ──────────────────

        var slaveIntents = cleanedSlaves
            .Select(g => (Elements: g, Vec: CalculateCandidateVector(model, g, masterElemSet, nodeById, sectionById, degrees, tol)))
            .ToList();

        // ── 5. Meta-group: 인접 + 방향 일치 slave 통합 ───────────────────

        var metaUf = new UnionFind(Enumerable.Range(0, slaveIntents.Count).ToList());

        for (int i = 0; i < slaveIntents.Count; i++)
        for (int j = i + 1; j < slaveIntents.Count; j++)
        {
            double d = GetDistanceBetweenGroups(nodeById,
                slaveIntents[i].Elements, slaveIntents[j].Elements);
            if (d > tol.GroupConnectLocalTolMm) continue;

            // zero vector(연결 후보 없음) 인 경우 meta-group 불가
            if (slaveIntents[i].Vec == Vector3.Zero || slaveIntents[j].Vec == Vector3.Zero) continue;
            if (Vector3.Dot(slaveIntents[i].Vec, slaveIntents[j].Vec) > 0)
                metaUf.Union(i, j);
        }

        var metaGroups = metaUf.GetGroups()
            .Select(indices => indices.SelectMany(idx => slaveIntents[idx].Elements).ToList())
            .ToList();

        // ── 6. 각 meta-group 처리 (병진 이동 전용) ────────────────────────

        int translatedCount = 0;
        int skippedCount    = 0;

        // 노드 인덱스 룩업용 — 이동마다 O(1) 위치 갱신
        var nodeIndexById = new Dictionary<int, int>(model.Nodes.Count);
        for (int i = 0; i < model.Nodes.Count; i++) nodeIndexById[model.Nodes[i].Id] = i;

        foreach (var metaGroup in metaGroups)
        {
            var (vector, freeNodeId, masterElem, dist) =
                CalculateBestMove(model, metaGroup, masterElemSet, nodeById, sectionById, degrees, tol);

            if (masterElem is null || dist <= 1e-6) continue;

            double maxCross = sectionById.TryGetValue(masterElem.PropertyId, out var sec)
                ? sec.MaxCrossSectionDim() : 0.0;
            double threshold = maxCross + tol.GroupConnectExtraMarginMm;

            if (dist <= threshold)
            {
                // 그룹 전체 병진 이동
                var groupNodeIds = GetAllNodeIdsInGroup(metaGroup);
                foreach (int nid in groupNodeIds)
                {
                    if (!nodeById.TryGetValue(nid, out var n)) continue;
                    var newPos = new Point3(
                        n.Position.X + vector.X,
                        n.Position.Y + vector.Y,
                        n.Position.Z + vector.Z);
                    var moved = new Node(n.Id, newPos, n.Tags);
                    if (nodeIndexById.TryGetValue(nid, out int idx))
                    {
                        model.Nodes[idx] = moved;
                        nodeById[nid] = moved;
                    }
                }
                ctx.AddTrace(TraceAction.NodeMoved, Name,
                    nodeId: freeNodeId, note: $"group translated {dist:F1}mm to master");

                // 이동 후 free-end가 다른 노드와 합치되면 명시적 merge — NodeSpatialHash 사용
                var movedFreeNode = nodeById[freeNodeId];
                var localHash = new NodeSpatialHash(model.Nodes, tol.SpatialCellSizeMm);
                int mergeTargetId = -1;
                foreach (int candidateId in localHash.QueryNear(movedFreeNode.Position, tol.NodeMergeMm))
                {
                    if (candidateId == freeNodeId) continue;
                    if (!nodeById.TryGetValue(candidateId, out var candidate)) continue;
                    if (candidate.Position.DistanceTo(movedFreeNode.Position) <= tol.NodeMergeMm)
                    {
                        mergeTargetId = candidateId;
                        break;
                    }
                }
                if (mergeTargetId > 0)
                {
                    RemapAndRemoveNode(ctx, nodeById, freeNodeId, mergeTargetId);
                    // nodeIndexById는 RemapAndRemoveNode 내부에서 노드 순서가 바뀌므로 재구성
                    nodeIndexById.Clear();
                    for (int i = 0; i < model.Nodes.Count; i++) nodeIndexById[model.Nodes[i].Id] = i;
                }

                translatedCount++;
            }
            else
            {
                ctx.AddDiagnostic(DiagnosticSeverity.Info, "GROUP_CONNECT_SKIP",
                    $"Slave group (node {freeNodeId}) is {dist:F1} mm from master (limit {threshold:F1} mm) — deferred to FreeEndRbeStage.");
                skippedCount++;
            }
        }

        int preservedPairRbes = AddPreservedFreeEndPairRigids(ctx, preservedFreeEndPairs, degrees);
        PruneStalePreservedFreeEndRigids(ctx, Name);
        RigidElementNormalizer.MergeSharedDependentNodes(ctx, Name);

        if (translatedCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "GROUPS_CONNECTED",
                $"{translatedCount} slave group(s) translated to master.");
        if (preservedPairRbes > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "GROUP_PRESERVED_FREE_END_RBE",
                $"{preservedPairRbes} pre-translation structure free-end pair(s) preserved by RBE2.");

        LastTranslatedCount = translatedCount;
        LastSkippedCount = skippedCount;

        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>slave 그룹의 normalized candidate translation vector. 후보 없으면 Zero.</summary>
    private static Vector3 CalculateCandidateVector(
        FeModel model, List<BeamElement> group, HashSet<int> masterElemIds,
        Dictionary<int, Node> nodeById,
        Dictionary<int, BeamSection> sectionById,
        Dictionary<int, int> degrees,
        Tolerances tol)
    {
        var (vec, _, target, _) = CalculateBestMove(model, group, masterElemIds, nodeById, sectionById, degrees, tol);
        if (target is null) return Vector3.Zero;
        double len = Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z);
        return len < 1e-10 ? Vector3.Zero : new Vector3(vec.X / len, vec.Y / len, vec.Z / len);
    }

    /// <summary>slave meta-group에서 master로의 최적 이동 벡터 계산.</summary>
    private static (Vector3 vec, int freeNodeId, BeamElement? masterElem, double dist)
        CalculateBestMove(
            FeModel model, List<BeamElement> group, HashSet<int> masterElemIds,
            Dictionary<int, Node> nodeById,
            Dictionary<int, BeamSection> sectionById,
            Dictionary<int, int> degrees,
            Tolerances tol)
    {
        var freeNodes = GetAllNodeIdsInGroup(group)
            .Where(nid => degrees.GetValueOrDefault(nid) == 1)
            .ToList();

        // closed group — no degree-1 nodes, try all group nodes
        if (freeNodes.Count == 0)
            freeNodes = GetAllNodeIdsInGroup(group).ToList();

        double   bestAcceptedDist   = double.MaxValue;
        Vector3  bestAcceptedVec    = default;
        int      bestAcceptedSrc    = -1;
        BeamElement? bestAcceptedTarget = null;

        double   nearestDist   = double.MaxValue;
        Vector3  nearestVec    = default;
        int      nearestSrc    = -1;
        BeamElement? nearestTarget = null;

        foreach (int fnid in freeNodes)
        {
            if (!nodeById.TryGetValue(fnid, out var freeNode)) continue;
            foreach (var e in model.Elements)
            {
                if (!masterElemIds.Contains(e.Id)) continue;
                if (!nodeById.TryGetValue(e.StartNodeId, out var mA)) continue;
                if (!nodeById.TryGetValue(e.EndNodeId,   out var mB)) continue;

                var (proj, _, d) = ProjectionUtils.ClosestPointOnSegment(
                    freeNode.Position, mA.Position, mB.Position);

                var vec = new Vector3(
                    proj.X - freeNode.Position.X,
                    proj.Y - freeNode.Position.Y,
                    proj.Z - freeNode.Position.Z);

                if (d < nearestDist)
                {
                    nearestDist   = d;
                    nearestVec    = vec;
                    nearestSrc    = fnid;
                    nearestTarget = e;
                }

                double maxCross = sectionById.TryGetValue(e.PropertyId, out var sec)
                    ? sec.MaxCrossSectionDim() : 0.0;
                double threshold = maxCross + tol.GroupConnectExtraMarginMm;
                if (d <= threshold && d < bestAcceptedDist)
                {
                    bestAcceptedDist   = d;
                    bestAcceptedVec    = vec;
                    bestAcceptedSrc    = fnid;
                    bestAcceptedTarget = e;
                }
            }
        }

        if (bestAcceptedSrc != -1)
            return (bestAcceptedVec, bestAcceptedSrc, bestAcceptedTarget, bestAcceptedDist);

        return nearestSrc == -1
            ? (default, -1, null, double.MaxValue)
            : (nearestVec, nearestSrc, nearestTarget, nearestDist);
    }

    private static double GetDistanceBetweenGroups(
        Dictionary<int, Node> nodeById,
        List<BeamElement> g1, List<BeamElement> g2)
    {
        var ids1 = GetAllNodeIdsInGroup(g1);
        var ids2 = GetAllNodeIdsInGroup(g2);
        double min = double.MaxValue;
        foreach (int a in ids1)
        {
            if (!nodeById.TryGetValue(a, out var na)) continue;
            foreach (int b in ids2)
            {
                if (!nodeById.TryGetValue(b, out var nb)) continue;
                double d = na.Position.DistanceTo(nb.Position);
                if (d < min) min = d;
            }
        }
        return min;
    }

    private static HashSet<int> GetAllNodeIdsInGroup(List<BeamElement> elems)
        => [..elems.SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })];

    private static Dictionary<int, int> BuildElementNodeDegree(FeModel model)
    {
        var deg = new Dictionary<int, int>();
        foreach (var e in model.Elements)
        {
            deg[e.StartNodeId] = deg.GetValueOrDefault(e.StartNodeId) + 1;
            deg[e.EndNodeId]   = deg.GetValueOrDefault(e.EndNodeId)   + 1;
        }
        return deg;
    }

    private readonly record struct PreservedFreeEndPair(int A, int B);

    private static List<PreservedFreeEndPair> FindPreservedStructureFreeEndPairs(
        FeModel model,
        Dictionary<int, Node> nodeById,
        Dictionary<int, BeamSection> sectionById,
        Dictionary<int, int> degrees,
        Tolerances tol)
    {
        var attached = new Dictionary<int, BeamElement>();
        foreach (var e in model.Elements.Where(e => e.Category == EntityCategory.Structure))
        {
            if (degrees.GetValueOrDefault(e.StartNodeId) == 1)
                attached[e.StartNodeId] = e;
            if (degrees.GetValueOrDefault(e.EndNodeId) == 1)
                attached[e.EndNodeId] = e;
        }

        if (attached.Count < 2) return [];

        var candidates = new List<(PreservedFreeEndPair Pair, double Dist)>();
        var ids = attached.Keys.OrderBy(id => id).ToList();

        for (int i = 0; i < ids.Count; i++)
        for (int j = i + 1; j < ids.Count; j++)
        {
            int aId = ids[i];
            int bId = ids[j];
            if (!nodeById.TryGetValue(aId, out var a) ||
                !nodeById.TryGetValue(bId, out var b)) continue;

            var eA = attached[aId];
            var eB = attached[bId];
            if (!TryGetElementDirection(eA, nodeById, out var dirA) ||
                !TryGetElementDirection(eB, nodeById, out var dirB)) continue;
            if (Math.Abs(Vector3.Dot(dirA, dirB)) < 0.985) continue;

            double dist = a.Position.DistanceTo(b.Position);
            double allowed = Math.Max(GetSectionMaxDim(sectionById, eA.PropertyId),
                                      GetSectionMaxDim(sectionById, eB.PropertyId))
                             + tol.GroupConnectExtraMarginMm;
            if (dist > allowed) continue;

            candidates.Add((new PreservedFreeEndPair(aId, bId), dist));
        }

        var usedNodes = new HashSet<int>();
        var selected = new List<PreservedFreeEndPair>();
        foreach (var candidate in candidates.OrderBy(c => c.Dist))
        {
            if (!usedNodes.Add(candidate.Pair.A)) continue;
            if (!usedNodes.Add(candidate.Pair.B))
            {
                usedNodes.Remove(candidate.Pair.A);
                continue;
            }
            selected.Add(candidate.Pair);
        }

        return selected;
    }

    private static int AddPreservedFreeEndPairRigids(
        StageContext ctx,
        IReadOnlyList<PreservedFreeEndPair> pairs,
        IReadOnlyDictionary<int, int> degrees)
    {
        if (pairs.Count == 0) return 0;

        var model = ctx.Model;
        var nodeIds = model.Nodes.Select(n => n.Id).ToHashSet();
        var rbeEdges = new HashSet<(int, int)>();
        foreach (var r in model.Rigids)
        foreach (int dep in r.DependentNodeIds)
            rbeEdges.Add(EdgeKey(r.IndependentNodeId, dep));

        int created = 0;
        model.SyncIdCounters();

        foreach (var pair in pairs)
        {
            if (!nodeIds.Contains(pair.A) || !nodeIds.Contains(pair.B)) continue;
            if (degrees.GetValueOrDefault(pair.A) != 1 ||
                degrees.GetValueOrDefault(pair.B) != 1) continue;

            var edge = EdgeKey(pair.A, pair.B);
            if (rbeEdges.Contains(edge)) continue;

            int rbeId = model.AllocElemId();
            model.Rigids.Add(new RigidElement(rbeId, pair.A, [pair.B], "PRESERVED_FREE_END"));
            rbeEdges.Add(edge);
            ctx.AddTrace(TraceAction.ElementCreated, "GroupConnect", elementId: rbeId,
                nodeId: pair.A, relatedNodeId: pair.B,
                note: "pre-translation free-end pair preserved by RBE2");
            created++;
        }

        return created;
    }

    public static int PruneStalePreservedFreeEndRigids(StageContext ctx, string stageName)
    {
        var model = ctx.Model;
        var degrees = BuildElementNodeDegree(model);
        int removed = 0;

        for (int i = model.Rigids.Count - 1; i >= 0; i--)
        {
            var rigid = model.Rigids[i];
            if (rigid.Remark != "PRESERVED_FREE_END") continue;
            if (rigid.DependentNodeIds.Count != 1 ||
                degrees.GetValueOrDefault(rigid.IndependentNodeId) != 1 ||
                degrees.GetValueOrDefault(rigid.DependentNodeIds[0]) != 1)
            {
                model.Rigids.RemoveAt(i);
                ctx.AddTrace(TraceAction.ElementRemoved, stageName,
                    elementId: rigid.Id, nodeId: rigid.IndependentNodeId,
                    relatedNodeId: rigid.DependentNodeIds.FirstOrDefault(),
                    note: "stale preserved free-end RBE removed; endpoint is no longer element-degree 1");
                removed++;
            }
        }

        if (removed > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "PRESERVED_FREE_END_RBE_PRUNED",
                $"{removed} stale preserved free-end RBE2(s) removed.");

        return removed;
    }

    private static bool TryGetElementDirection(
        BeamElement e,
        Dictionary<int, Node> nodeById,
        out Vector3 dir)
    {
        dir = default;
        if (!nodeById.TryGetValue(e.StartNodeId, out var a) ||
            !nodeById.TryGetValue(e.EndNodeId, out var b)) return false;

        var span = b.Position - a.Position;
        if (span.LengthSquared < 1e-18) return false;
        dir = span.Normalize();
        return true;
    }

    private static double GetSectionMaxDim(Dictionary<int, BeamSection> sectionById, int propertyId)
        => sectionById.TryGetValue(propertyId, out var sec) ? sec.MaxCrossSectionDim() : 0.0;

    private static (int, int) EdgeKey(int a, int b)
        => a < b ? (a, b) : (b, a);

    /// <summary>
    /// slave 요소들에서 oldId → newId로 remap하고, oldId 노드를 모델에서 제거한다.
    /// 다른 요소에서도 참조되지 않을 때만 제거.
    /// </summary>
    private static void RemapAndRemoveNode(
        StageContext ctx, Dictionary<int, Node> nodeById, int oldId, int newId)
    {
        var model       = ctx.Model;
        var toRemoveSet = new HashSet<int>();
        var toCreate    = new List<(int ParentId, BeamElement Created)>();

        for (int i = 0; i < model.Elements.Count; i++)
        {
            var e = model.Elements[i];
            if (e.StartNodeId != oldId && e.EndNodeId != oldId) continue;
            int s = e.StartNodeId == oldId ? newId : e.StartNodeId;
            int n = e.EndNodeId   == oldId ? newId : e.EndNodeId;
            toRemoveSet.Add(e.Id);
            ctx.AddTrace(TraceAction.ElementRemoved, "GroupConnect",
                elementId: e.Id, note: "node merge changed element endpoints");
            if (s != n)
            {
                int childId = model.AllocElemId();
                toCreate.Add((e.Id, new BeamElement(childId, s, n, e.PropertyId,
                    e.Category, e.Orientation, e.SourceName, e.Id)));
            }
        }

        if (toRemoveSet.Count > 0)
            model.Elements.RemoveAll(e => toRemoveSet.Contains(e.Id));
        foreach (var (parentId, created) in toCreate)
        {
            model.Elements.Add(created);
            ctx.AddTrace(TraceAction.ElementCreated, "GroupConnect",
                elementId: created.Id, relatedElementId: parentId,
                note: "recreated after group connect node merge");
        }

        RemapRigids(model, oldId, newId);
        RemapPointMasses(model, oldId, newId);

        bool stillReferenced = model.Elements.Any(e =>
                                   e.StartNodeId == oldId || e.EndNodeId == oldId)
                               || model.Rigids.Any(r =>
                                   r.IndependentNodeId == oldId || r.DependentNodeIds.Contains(oldId))
                               || model.PointMasses.Any(pm => pm.NodeId == oldId);
        if (!stillReferenced)
        {
            model.Nodes.RemoveAll(n => n.Id == oldId);
            nodeById.Remove(oldId);
        }
    }

    private static void RemapRigids(FeModel model, int oldId, int newId)
    {
        for (int i = 0; i < model.Rigids.Count; i++)
        {
            var rigid = model.Rigids[i];
            int indep = rigid.IndependentNodeId == oldId ? newId : rigid.IndependentNodeId;
            var deps = rigid.DependentNodeIds
                .Select(d => d == oldId ? newId : d)
                .Where(d => d != indep)
                .Distinct()
                .ToList();

            if (indep != rigid.IndependentNodeId || !deps.SequenceEqual(rigid.DependentNodeIds))
                model.Rigids[i] = new RigidElement(rigid.Id, indep, deps,
                    rigid.Remark, rigid.SourceName, rigid.Cm, rigid.DetailRemark);
        }
    }

    private static void RemapPointMasses(FeModel model, int oldId, int newId)
    {
        for (int i = 0; i < model.PointMasses.Count; i++)
        {
            var pm = model.PointMasses[i];
            if (pm.NodeId == oldId)
                model.PointMasses[i] = new PointMass(pm.Id, newId, pm.Mass, pm.SourceName);
        }
    }
}
