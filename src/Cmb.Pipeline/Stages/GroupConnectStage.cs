using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 분리된 slave 그룹을 master 그룹에 연결한다.
/// Meta-group(근접 + 방향 일치 slave 통합) → 연결 → RBE 폴백(거리 초과 시, FreeEndRbeStage).
/// 간격이 <see cref="Tolerances.GroupConnectTranslateMaxMm"/> 이하(CAD 오차)면 그룹을 병진 이동해 붙이고,
/// 그보다 크면 그룹을 제자리에 둔 채 master 위 점과 RBE2 로 잇는다 — 플랜지 위에 얹힌 부재처럼
/// 실제로 떨어져 있는 편심을 보존하고, 그룹 전체를 옮겨 COG·러그 위치가 바뀌는 것을 막는다.
/// </summary>
public sealed class GroupConnectStage : IPipelineStage
{
    public const string GroupConnectRemark = "GROUP_CONNECT_RBE";

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

        // 이 stage 가 만든 연결 RBE2 도 같은 그룹으로 본다 — 안 그러면 다음 수렴 반복에서 같은 slave 를 다시 잇는다.
        var connectRigids = model.Rigids.Where(r => r.Remark == GroupConnectRemark).ToList();
        var allNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .Concat(connectRigids.SelectMany(r => r.DependentNodeIds.Append(r.IndependentNodeId)))
            .Distinct().ToList();
        if (allNodeIds.Count == 0) return true;

        var uf = new UnionFind(allNodeIds);
        foreach (var e in model.Elements) uf.Union(e.StartNodeId, e.EndNodeId);
        foreach (var r in connectRigids)
            foreach (int dep in r.DependentNodeIds) uf.Union(r.IndependentNodeId, dep);

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

        // master = 구조 요소가 가장 많은 그룹(동률은 전체 요소 수·최소 노드 ID). 배관이 많은 모듈에서
        // 배관 네트워크가 요소 수 최대가 되어 master 로 뽑히면 구조 그룹이 배관 쪽으로 옮겨져 융합된다.
        var sortedRoots = elemsByRoot
            .OrderByDescending(kv => kv.Value.Count(e => e.Category != EntityCategory.Pipe))
            .ThenByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.Min(e => Math.Min(e.StartNodeId, e.EndNodeId)))
            .ToList();
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

            if (dist <= threshold && dist > tol.GroupConnectTranslateMaxMm &&
                TryConnectByRigid(ctx, nodeById, freeNodeId, masterElem, nodeById[freeNodeId].Position + vector, dist))
            {
                translatedCount++;
            }
            else if (dist <= threshold)
            {
                // 그룹 전체 병진 이동 (CAD 오차 수준 간격, 또는 RBE 로 잇지 못한 경우)
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
        {
            var axial = TryAxialAlternative(model, group, masterElemIds, nodeById, sectionById, tol,
                bestAcceptedSrc, bestAcceptedTarget!, bestAcceptedDist);
            if (axial is { Defer: true })
                return (default, -1, null, double.MaxValue);   // 받침 부재가 아직 master 가 아님 → 다음 반복
            if (axial is { } a)
                return (a.Vec, bestAcceptedSrc, a.Target, a.Dist);
            return (bestAcceptedVec, bestAcceptedSrc, bestAcceptedTarget, bestAcceptedDist);
        }

        return nearestSrc == -1
            ? (default, -1, null, double.MaxValue)
            : (nearestVec, nearestSrc, nearestTarget, nearestDist);
    }

    /// <summary>
    /// FreeEndRbeStage 의 축 방향 우선 규칙과 같다. 자유단 부재가 향하는 방향(원뿔 cos ≥ FreeEndRbeAxialConeCos)에
    /// 받침 부재가 있고, ① 최근접보다 FreeEndRbeAxialPreferenceMm 이내로만 멀고 ② 최근접 부재에 이미 닿아 있지
    /// 않으며(거리 > 그 단면 최대치수/2) ③ 자유단이 받침 단면 위에 얹혀 있으면(거리 ≤ 단면/2 + 여유) 받침 부재로 바꾼다.
    /// (3496-35210-A508372 N291: 기둥 하단이 옆 앵글 76mm 가 아니라 아래 H빔 87.5mm 에 붙어야 한다)
    /// 받침 부재 후보는 slave 밖의 모든 구조 부재에서 찾는다. 받침 부재가 아직 master 가 아니면(같은 반복에서
    /// 함께 연결될 다른 slave) Defer 를 돌려 이번 반복에 옆 부재로 잘못 잇지 않고 다음 반복으로 미룬다.
    /// </summary>
    private static (Vector3 Vec, BeamElement Target, double Dist, bool Defer)? TryAxialAlternative(
        FeModel model, List<BeamElement> group, HashSet<int> masterElemIds,
        Dictionary<int, Node> nodeById, Dictionary<int, BeamSection> sectionById, Tolerances tol,
        int freeNodeId, BeamElement nearestTarget, double nearestDist)
    {
        if (tol.FreeEndRbeAxialPreferenceMm <= 0) return null;
        if (!nodeById.TryGetValue(freeNodeId, out var freeNode)) return null;

        var own = group.FirstOrDefault(e => e.StartNodeId == freeNodeId || e.EndNodeId == freeNodeId);
        if (own is null) return null;
        int otherId = own.StartNodeId == freeNodeId ? own.EndNodeId : own.StartNodeId;
        if (!nodeById.TryGetValue(otherId, out var otherNode)) return null;
        var outward = freeNode.Position - otherNode.Position;
        if (outward.LengthSquared < 1e-12) return null;
        var dir = outward.Normalize();

        double nearestDim = GetSectionMaxDim(sectionById, nearestTarget.PropertyId);
        if (nearestDist <= nearestDim * 0.5) return null;   // 옆 부재에 이미 닿아 있음 → 그대로

        var groupElemIds = group.Select(g => g.Id).ToHashSet();
        (Vector3 Vec, BeamElement Target, double Dist, bool Defer)? best = null;
        foreach (var e in model.Elements)
        {
            if (e.Id == nearestTarget.Id || groupElemIds.Contains(e.Id)) continue;
            if (e.Category == EntityCategory.Pipe) continue;
            if (!nodeById.TryGetValue(e.StartNodeId, out var mA)) continue;
            if (!nodeById.TryGetValue(e.EndNodeId,   out var mB)) continue;

            var (proj, _, d) = ProjectionUtils.ClosestPointOnSegment(freeNode.Position, mA.Position, mB.Position);
            if (d < 1e-6 || d > nearestDist + tol.FreeEndRbeAxialPreferenceMm) continue;
            var vec = proj - freeNode.Position;
            if (Vector3.Dot(vec, dir) / d < tol.FreeEndRbeAxialConeCos) continue;   // 향하는 방향이 아님
            double dim = GetSectionMaxDim(sectionById, e.PropertyId);
            if (d > dim * 0.5 + tol.FreeEndRbeExtraMarginMm) continue;               // 받침 위에 얹혀 있지 않음
            if (best is null || d < best.Value.Dist) best = (vec, e, d, !masterElemIds.Contains(e.Id));
        }
        return best;
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
    /// slave 자유단을 옮기지 않고 master 부재 위 점(target)과 RBE2 로 잇는다.
    /// target 이 master 끝점과 NodeMergeMm 이내면 그 절점을 쓰고, 아니면 새 절점을 만든다
    /// (master 분할은 뒤따르는 SplitByExistingNodesStage 가 한다).
    /// </summary>
    private static bool TryConnectByRigid(
        StageContext ctx, Dictionary<int, Node> nodeById,
        int freeNodeId, BeamElement masterElem, Point3 target, double dist)
    {
        var model = ctx.Model;
        double mergeTol = ctx.Options.Tolerances.NodeMergeMm;

        int targetId = -1;
        foreach (int endId in new[] { masterElem.StartNodeId, masterElem.EndNodeId })
        {
            if (nodeById.TryGetValue(endId, out var end) && end.Position.DistanceTo(target) <= mergeTol)
            {
                targetId = endId;
                break;
            }
        }
        if (targetId < 0)
        {
            targetId = model.AllocNodeId();
            var node = new Node(targetId, target);
            model.Nodes.Add(node);
            nodeById[targetId] = node;
            ctx.AddTrace(TraceAction.NodeCreated, "GroupConnect", nodeId: targetId,
                relatedElementId: masterElem.Id, note: "group-connect RBE anchor on master element");
        }
        if (targetId == freeNodeId) return false;

        // 한 절점이 두 RBE2 의 종속이면 정리 단계가 둘을 합쳐 이 표식이 사라지고, 다음 수렴 반복에서
        // 같은 slave 를 또 잇는다(FATAL 2101 위험도). 자유단이 이미 종속이면 역할을 뒤집고,
        // 양쪽 다 종속이면 RBE 를 만들지 않는다(호출자가 병진 이동으로 대체).
        var dependents = model.Rigids.SelectMany(r => r.DependentNodeIds).ToHashSet();
        int indep = targetId, dep = freeNodeId;
        if (dependents.Contains(freeNodeId))
        {
            if (dependents.Contains(targetId)) return false;
            (indep, dep) = (freeNodeId, targetId);
        }

        int rbeId = model.AllocElemId();
        model.Rigids.Add(new RigidElement(rbeId, indep, [dep], GroupConnectRemark));
        ctx.AddTrace(TraceAction.ElementCreated, "GroupConnect", elementId: rbeId, nodeId: freeNodeId,
            relatedElementId: masterElem.Id,
            note: $"group connected by RBE2 {dist:F1}mm to master (no translation)");
        return true;
    }

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
