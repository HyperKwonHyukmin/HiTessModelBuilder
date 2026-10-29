using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 50mm 이내의 인접 RBE2들을 단일 RBE2로 병합. HiTess RigidProximityMergeModifier 이식.
/// 두 인접 U-bolt RBE2(GN=pipe / GM=stru)가 클러스터로 묶이면서 결과적으로
/// pipe-pipe 강체 연결 효과를 형성.
/// </summary>
public sealed class RigidProximityMergeStage : IPipelineStage
{
    public string Name => "RigidProximityMerge";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        if (model.Rigids.Count < 2) return true;

        double tol      = ctx.Options.Tolerances.RigidProximityMergeMm;
        double cellSize = ctx.Options.Tolerances.SpatialCellSizeMm;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);

        // 1. 각 RBE의 노드 집합 — Indep + Dep 모두 포함
        int n = model.Rigids.Count;
        var rbeNodes = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            var r = model.Rigids[i];
            var list = new List<int>(r.DependentNodeIds.Count + 1) { r.IndependentNodeId };
            list.AddRange(r.DependentNodeIds);
            rbeNodes[i] = list;
        }

        // 2. Union-Find 클러스터링 — 공간 해시로 후보 페어만 검사
        var uf = new UnionFind(Enumerable.Range(0, n).ToList());

        // nodeId → 그 노드를 참조하는 RBE 인덱스 목록 (한 노드를 여러 RBE가 공유 가능)
        var nodeToRbeIdx = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            foreach (int nid in rbeNodes[i])
            {
                if (!nodeToRbeIdx.TryGetValue(nid, out var owners))
                {
                    owners = [];
                    nodeToRbeIdx[nid] = owners;
                }
                owners.Add(i);
            }
        }

        // 모든 RBE 노드를 NodeSpatialHash에 등록
        var rbeNodeList = nodeToRbeIdx.Keys
            .Where(id => nodeById.ContainsKey(id))
            .Select(id => nodeById[id])
            .ToList();
        var nodeHash = new NodeSpatialHash(rbeNodeList, cellSize);

        double tolSq = tol * tol;
        for (int i = 0; i < n; i++)
        {
            foreach (int nid in rbeNodes[i])
            {
                if (!nodeById.TryGetValue(nid, out var nA)) continue;

                // 같은 노드를 공유하는 RBE는 즉시 union (거리 검사 불필요)
                if (nodeToRbeIdx.TryGetValue(nid, out var sharedOwners))
                {
                    foreach (int j in sharedOwners)
                    {
                        if (j != i && uf.Find(i) != uf.Find(j))
                            uf.Union(i, j);
                    }
                }

                // tol 반경 내 다른 노드 → 그 노드를 참조하는 RBE와 union
                foreach (int candNid in nodeHash.QueryNear(nA.Position, tol))
                {
                    if (candNid == nid) continue;
                    if (!nodeById.TryGetValue(candNid, out var nB)) continue;
                    double dx = nA.Position.X - nB.Position.X;
                    double dy = nA.Position.Y - nB.Position.Y;
                    double dz = nA.Position.Z - nB.Position.Z;
                    if (dx * dx + dy * dy + dz * dz > tolSq) continue;

                    if (!nodeToRbeIdx.TryGetValue(candNid, out var owners)) continue;
                    foreach (int j in owners)
                    {
                        if (j != i && uf.Find(i) != uf.Find(j))
                            uf.Union(i, j);
                    }
                }
            }
        }

        // 3. 클러스터별 인덱스 그룹화
        var clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = uf.Find(i);
            if (!clusters.TryGetValue(root, out var group)) { group = []; clusters[root] = group; }
            group.Add(i);
        }

        // 4. 단일 RBE 클러스터는 변경 없음. 2+ RBE 클러스터만 병합 처리.
        var newRigids = new List<RigidElement>(model.Rigids.Count);
        int clustersMerged = 0;
        int rigidsConsumed = 0;

        foreach (var (_, indices) in clusters)
        {
            if (indices.Count == 1)
            {
                newRigids.Add(model.Rigids[indices[0]]);
                continue;
            }

            // Master 선정: GM 개수 최다 → tiebreak: IndependentNodeId 최소
            int masterIdx = indices
                .OrderByDescending(idx => model.Rigids[idx].DependentNodeIds.Count)
                .ThenBy(idx => model.Rigids[idx].IndependentNodeId)
                .First();
            var master = model.Rigids[masterIdx];

            // 병합된 노드 집합 = 모든 RBE의 (Indep + Dep) 합집합 - master.Indep
            var merged = new HashSet<int>();
            var cmChars = new HashSet<char>();
            var distinctSources = new HashSet<string>(StringComparer.Ordinal);
            bool anyUbolt = false;
            bool anyBoxUbolt = false;
            foreach (int idx in indices)
            {
                var r = model.Rigids[idx];
                merged.Add(r.IndependentNodeId);
                foreach (int d in r.DependentNodeIds) merged.Add(d);
                foreach (char c in r.Cm.Where(char.IsDigit)) cmChars.Add(c);
                if (!string.IsNullOrWhiteSpace(r.SourceName)) distinctSources.Add(r.SourceName!);
                if (r.Remark == "UBOLT") anyUbolt = true;
                if (string.Equals(r.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase))
                    anyBoxUbolt = true;
            }
            merged.Remove(master.IndependentNodeId);

            // Remark: 클러스터 내 어느 하나라도 UBOLT면 UBOLT 유지, 아니면 master remark
            string finalRemark = anyUbolt ? "UBOLT" : master.Remark;

            // DetailRemark: 클러스터에 BOX UBOLT가 하나라도 있으면 "BOX" 보존 —
            // master가 BOX가 아니어도 IsBoxUbolt 판별이 후속 스테이지에서 깨지지 않도록 함
            string? finalDetailRemark = anyBoxUbolt ? "BOX" : master.DetailRemark;

            // cm union — 부분 자유도 RBE가 섞인 클러스터에서 자유도 손실 방지
            string finalCm = cmChars.Count == 0
                ? master.Cm
                : new string(cmChars.OrderBy(c => c).ToArray());

            var mergedRbe = new RigidElement(
                master.Id,
                master.IndependentNodeId,
                merged,
                finalRemark,
                master.SourceName,
                finalCm,
                finalDetailRemark);

            newRigids.Add(mergedRbe);

            var consumedIds = indices.Where(idx => idx != masterIdx).Select(idx => model.Rigids[idx].Id).ToList();
            ctx.AddTrace(TraceAction.ElementPreserved, Name, elementId: master.Id,
                note: $"absorbed {consumedIds.Count} adjacent RBE2(s) within {tol:F0}mm: {string.Join(",", consumedIds)}");
            foreach (int consumedId in consumedIds)
            {
                ctx.AddTrace(TraceAction.ElementRemoved, Name,
                    elementId: consumedId, relatedElementId: master.Id,
                    note: $"merged into RBE2 {master.Id}");
            }

            // F-3 감사: 거리 기반 병합이 서로 다른 소스(예: 다른 배관의 U-bolt)를 하나로 묶었을 수 있다.
            // 클러스터가 2개 이상 서로 다른 소스명을 포함하면 StageSummary(진단)에 남겨 검토 가능하게 한다.
            if (distinctSources.Count >= 2)
                ctx.AddDiagnostic(DiagnosticSeverity.Info, "RIGID_PROXIMITY_MERGE_AUDIT",
                    $"근접 병합 클러스터(master RBE2 {master.Id})가 서로 다른 소스 {distinctSources.Count}개를 하나로 묶음: " +
                    $"[{string.Join(", ", distinctSources.OrderBy(s => s, StringComparer.Ordinal))}] " +
                    $"(rigids {master.Id},{string.Join(",", consumedIds)} within {tol:F0}mm). " +
                    "서로 다른 부재의 강체가 거리 기준으로 합쳐졌을 수 있으니 확인 필요.");

            clustersMerged++;
            rigidsConsumed += consumedIds.Count;
        }

        if (clustersMerged > 0)
        {
            model.Rigids.Clear();
            model.Rigids.AddRange(newRigids);
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "RIGID_PROXIMITY_MERGED",
                $"{clustersMerged} RBE2 cluster(s) merged ({rigidsConsumed} rigids consumed) within {tol:F0}mm.");
        }

        return true;
    }

}
