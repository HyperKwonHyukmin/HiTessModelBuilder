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

    public bool Execute(StageContext ctx)
    {
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

        // ── 2. Slave 필터: any Pipe 포함 그룹 제외 ───────────────────────

        var filteredSlaves = sortedRoots.Skip(1)
            .Where(kv => kv.Value.All(e => e.Category != EntityCategory.Pipe))
            .Select(kv => kv.Value)
            .ToList();

        if (filteredSlaves.Count == 0) return true;

        // ── 3. 전역 node degree map ────────────────────────────────────────

        var degrees = BuildNodeDegree(model);

        // ── 4. Candidate vector 계산 (meta-group 구성용) ──────────────────

        var slaveIntents = filteredSlaves
            .Select(g => (Elements: g, Vec: CalculateCandidateVector(model, g, masterElemSet, nodeById, degrees)))
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

        // ── 6. 각 meta-group 처리 ─────────────────────────────────────────

        int translatedCount = 0;
        int rbeCount        = 0;

        foreach (var metaGroup in metaGroups)
        {
            var (vector, freeNodeId, masterElem, dist) =
                CalculateBestMove(model, metaGroup, masterElemSet, nodeById, degrees);

            if (masterElem is null || dist <= 1e-6) continue;

            double maxCross   = GetMaxCrossSectionDim(model, masterElem.PropertyId);
            double threshold  = maxCross + tol.GroupConnectExtraMarginMm;

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
                    int idx = model.Nodes.FindIndex(x => x.Id == nid);
                    if (idx >= 0) { model.Nodes[idx] = moved; nodeById[nid] = moved; }
                }
                ctx.AddTrace(TraceAction.NodeMoved, Name,
                    nodeId: freeNodeId, note: $"group translated {dist:F1}mm to master");

                // 이동 후 free-end가 master endpoint와 합치되면 명시적 merge
                var movedFreeNode = nodeById[freeNodeId];
                var mergeTarget   = nodeById.Values
                    .FirstOrDefault(n => n.Id != freeNodeId &&
                                        n.Position.DistanceTo(movedFreeNode.Position) <= tol.NodeMergeMm);
                if (mergeTarget is not null)
                    RemapAndRemoveNode(model, nodeById, freeNodeId, mergeTarget.Id);

                translatedCount++;
            }
            else
            {
                // RBE 폴백: snap node 생성 + RigidElement 추가
                var freeNode = nodeById[freeNodeId];
                var proj     = new Point3(
                    freeNode.Position.X + vector.X,
                    freeNode.Position.Y + vector.Y,
                    freeNode.Position.Z + vector.Z);

                var snapNode = nodeById.Values
                    .FirstOrDefault(n => n.Position.DistanceTo(proj) <= tol.NodeMergeMm);
                if (snapNode is null)
                {
                    int snapId = model.AllocNodeId();
                    snapNode = new Node(snapId, proj);
                    model.Nodes.Add(snapNode);
                    nodeById[snapId] = snapNode;
                }

                int rbeId = model.AllocElemId();
                model.Rigids.Add(new RigidElement(rbeId, snapNode.Id, [freeNodeId], "GROUP_CONNECT"));
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "GROUP_CONNECT_RBE",
                    $"Slave group (node {freeNodeId}) is {dist:F1} mm from master (limit {threshold:F1} mm) — linked via RBE.");
                rbeCount++;
            }
        }

        RigidElementNormalizer.MergeSharedDependentNodes(ctx, Name);

        if (translatedCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "GROUPS_CONNECTED",
                $"{translatedCount} slave group(s) translated to master.");
        if (rbeCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "GROUPS_CONNECTED_RBE",
                $"{rbeCount} slave group(s) connected to master via RBE2 fallback.");

        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>slave 그룹의 normalized candidate translation vector. 후보 없으면 Zero.</summary>
    private static Vector3 CalculateCandidateVector(
        FeModel model, List<BeamElement> group, HashSet<int> masterElemIds,
        Dictionary<int, Node> nodeById, Dictionary<int, int> degrees)
    {
        var (vec, _, target, _) = CalculateBestMove(model, group, masterElemIds, nodeById, degrees);
        if (target is null) return Vector3.Zero;
        double len = Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z);
        return len < 1e-10 ? Vector3.Zero : new Vector3(vec.X / len, vec.Y / len, vec.Z / len);
    }

    /// <summary>slave meta-group에서 master로의 최적 이동 벡터 계산.</summary>
    private static (Vector3 vec, int freeNodeId, BeamElement? masterElem, double dist)
        CalculateBestMove(
            FeModel model, List<BeamElement> group, HashSet<int> masterElemIds,
            Dictionary<int, Node> nodeById, Dictionary<int, int> degrees)
    {
        var freeNodes = GetAllNodeIdsInGroup(group)
            .Where(nid => degrees.GetValueOrDefault(nid) == 1)
            .ToList();

        // closed group — no degree-1 nodes, try all group nodes
        if (freeNodes.Count == 0)
            freeNodes = GetAllNodeIdsInGroup(group).ToList();

        double   bestDist   = double.MaxValue;
        Vector3  bestVec    = default;
        int      bestSrc    = -1;
        BeamElement? bestTarget = null;

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

                if (d < bestDist)
                {
                    bestDist   = d;
                    bestVec    = new Vector3(
                        proj.X - freeNode.Position.X,
                        proj.Y - freeNode.Position.Y,
                        proj.Z - freeNode.Position.Z);
                    bestSrc    = fnid;
                    bestTarget = e;
                }
            }
        }

        return bestSrc == -1
            ? (default, -1, null, double.MaxValue)
            : (bestVec, bestSrc, bestTarget, bestDist);
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

    private static double GetMaxCrossSectionDim(FeModel model, int propertyId)
    {
        var section = model.Sections.Find(s => s.Id == propertyId);
        return section is not null && section.Dims.Length > 0 ? section.Dims.Max() : 0.0;
    }

    private static HashSet<int> GetAllNodeIdsInGroup(List<BeamElement> elems)
        => [..elems.SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })];

    private static Dictionary<int, int> BuildNodeDegree(FeModel model)
    {
        var deg = new Dictionary<int, int>();
        foreach (var e in model.Elements)
        {
            deg[e.StartNodeId] = deg.GetValueOrDefault(e.StartNodeId) + 1;
            deg[e.EndNodeId]   = deg.GetValueOrDefault(e.EndNodeId)   + 1;
        }
        foreach (var r in model.Rigids)
        {
            deg[r.IndependentNodeId] = deg.GetValueOrDefault(r.IndependentNodeId) + 1;
            foreach (int did in r.DependentNodeIds)
                deg[did] = deg.GetValueOrDefault(did) + 1;
        }
        return deg;
    }

    /// <summary>
    /// slave 요소들에서 oldId → newId로 remap하고, oldId 노드를 모델에서 제거한다.
    /// 다른 요소에서도 참조되지 않을 때만 제거.
    /// </summary>
    private static void RemapAndRemoveNode(
        FeModel model, Dictionary<int, Node> nodeById, int oldId, int newId)
    {
        for (int i = 0; i < model.Elements.Count; i++)
        {
            var e = model.Elements[i];
            if (e.StartNodeId != oldId && e.EndNodeId != oldId) continue;
            int s = e.StartNodeId == oldId ? newId : e.StartNodeId;
            int n = e.EndNodeId   == oldId ? newId : e.EndNodeId;
            model.Elements[i] = new BeamElement(e.Id, s, n, e.PropertyId,
                e.Category, e.Orientation, e.SourceName, e.ParentElementId);
        }
        bool stillReferenced = model.Elements.Any(e =>
            e.StartNodeId == oldId || e.EndNodeId == oldId);
        if (!stillReferenced)
        {
            model.Nodes.RemoveAll(n => n.Id == oldId);
            nodeById.Remove(oldId);
        }
    }
}
