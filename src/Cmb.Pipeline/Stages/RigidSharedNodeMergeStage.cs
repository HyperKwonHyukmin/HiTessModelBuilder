using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 최종 BDF에서 "한 노드가 서로 다른 2개 이상의 RBE2에 동시에 걸리는" 상황을 제거한다.
///
/// RigidProximityMergeStage(50mm 병합)는 후속 RBE 생성 단계(FreeEndRbe/FinalMeshProximity 등)
/// 보다 먼저 실행되어, 그 뒤에 새로 생긴 노드 공유 충돌은 잡지 못한다. 또한
/// RigidElementNormalizer.MergeSharedDependentNodes 는 "여러 RBE가 같은 dep 노드를 공유"하는
/// 경우만 본다 — 한 노드가 한쪽에서 dep, 다른 쪽에서 indep 인 cascade(예: 1093→275→161)는 못 잡는다.
///
/// 이 stage 는 파이프라인 끝단(RedundantBridgeRbeRemove 다음, FinalValidation 직전)에서
/// **노드 공유(indep/dep 불문) 기준**으로 RBE2 들을 Union-Find 클러스터링하여 클러스터당 1개로
/// 병합한다. 결과적으로 어떤 노드도 2개 이상의 RBE2 에 등장하지 않는다(클러스터 = 노드 disjoint).
/// 병합 규칙(master 선정·CM union·UBOLT/BOX 보존)은 RigidProximityMergeStage 와 동일하게 맞춘다.
/// </summary>
public sealed class RigidSharedNodeMergeStage : IPipelineStage
{
    public string Name => "RigidSharedNodeMerge";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();

        int n = model.Rigids.Count;
        if (n < 2) return true;

        // 1. 각 RBE의 노드 집합 — Indep + Dep 모두
        var rbeNodes = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            var r = model.Rigids[i];
            var list = new List<int>(r.DependentNodeIds.Count + 1) { r.IndependentNodeId };
            list.AddRange(r.DependentNodeIds);
            rbeNodes[i] = list;
        }

        // 2. nodeId → 그 노드를 참조하는 RBE 인덱스 목록
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

        // 3. 같은 노드를 공유하는 RBE는 Union (거리 검사 없음 — 하드 제약: 한 노드 한 RBE)
        var uf = new UnionFind(Enumerable.Range(0, n).ToList());
        foreach (var owners in nodeToRbeIdx.Values)
        {
            for (int k = 1; k < owners.Count; k++)
                if (uf.Find(owners[0]) != uf.Find(owners[k]))
                    uf.Union(owners[0], owners[k]);
        }

        // 4. 클러스터별 인덱스 그룹화
        var clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = uf.Find(i);
            if (!clusters.TryGetValue(root, out var group)) { group = []; clusters[root] = group; }
            group.Add(i);
        }

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

            // Master 선정: 축 방향 FREE_END_RBE 우선 → GM(dep) 개수 최다 → tiebreak: IndependentNodeId 최소
            // (뒤 두 규칙은 RigidProximityMerge 와 동일). 축 방향 RBE 의 indep 는 자유단이 얹힌 받침 부재
            // 노드라, 이를 master 로 두어야 병합 후에도 자유단이 받침 부재에서 뻗어 나간다 — ID 최소 규칙이면
            // 옆 부재 노드가 master 가 되어 자유단이 다시 옆 부재에 붙은 모양이 된다(3496-35210-A508372 N291).
            int masterIdx = indices
                .OrderByDescending(idx => model.Rigids[idx].DetailRemark == FreeEndRbeStage.AxialDetailRemark)
                .ThenByDescending(idx => model.Rigids[idx].DependentNodeIds.Count)
                .ThenBy(idx => model.Rigids[idx].IndependentNodeId)
                .First();
            var master = model.Rigids[masterIdx];

            var merged = new HashSet<int>();
            var cmChars = new HashSet<char>();
            bool anyUbolt = false;
            bool anyBoxUbolt = false;
            foreach (int idx in indices)
            {
                var r = model.Rigids[idx];
                merged.Add(r.IndependentNodeId);
                foreach (int d in r.DependentNodeIds) merged.Add(d);
                foreach (char c in r.Cm.Where(char.IsDigit)) cmChars.Add(c);
                if (r.Remark == "UBOLT") anyUbolt = true;
                if (string.Equals(r.DetailRemark, "BOX", StringComparison.OrdinalIgnoreCase))
                    anyBoxUbolt = true;
            }
            merged.Remove(master.IndependentNodeId);

            string finalRemark = anyUbolt ? "UBOLT" : master.Remark;
            string? finalDetailRemark = anyBoxUbolt ? "BOX" : master.DetailRemark;
            string finalCm = cmChars.Count == 0
                ? master.Cm
                : new string(cmChars.OrderBy(c => c).ToArray());

            newRigids.Add(new RigidElement(
                master.Id, master.IndependentNodeId, merged,
                finalRemark, master.SourceName, finalCm, finalDetailRemark));

            var consumedIds = indices.Where(idx => idx != masterIdx).Select(idx => model.Rigids[idx].Id).ToList();
            ctx.AddTrace(TraceAction.ElementPreserved, Name, elementId: master.Id,
                note: $"absorbed {consumedIds.Count} node-sharing RBE2(s): {string.Join(",", consumedIds)}");
            foreach (int consumedId in consumedIds)
                ctx.AddTrace(TraceAction.ElementRemoved, Name,
                    elementId: consumedId, relatedElementId: master.Id,
                    note: $"merged into RBE2 {master.Id} (shared node — one node must not be in 2 RBE2)");

            clustersMerged++;
            rigidsConsumed += consumedIds.Count;
        }

        if (clustersMerged > 0)
        {
            model.Rigids.Clear();
            model.Rigids.AddRange(newRigids);
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "RIGID_SHARED_NODE_MERGED",
                $"{clustersMerged} RBE2 cluster(s) sharing a node merged into one ({rigidsConsumed} rigids consumed) — enforces one-node-one-RBE2.");
        }

        return true;
    }
}
