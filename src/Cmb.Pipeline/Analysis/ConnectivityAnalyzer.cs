using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Analysis;

public static class ConnectivityAnalyzer
{
    /// <summary>
    /// 모델의 BeamElement + RigidElement 연결 그래프를 분석하여 연결 그룹 통계를 반환합니다.
    /// RBE2(Rigid)로 연결된 노드도 같은 그룹으로 병합합니다.
    /// </summary>
    public static ConnectivityDto Analyze(FeModel model)
    {
        if (model.Elements.Count == 0)
            return new ConnectivityDto { GroupCount = 0 };

        // BEAM + Rigid 양쪽에서 참조되는 노드 ID 수집
        var beamNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId });

        var rigidNodeIds = model.Rigids
            .SelectMany(r => r.DependentNodeIds.Prepend(r.IndependentNodeId));

        var allConnectedNodeIds = beamNodeIds
            .Concat(rigidNodeIds)
            .Distinct()
            .ToList();

        var uf = new UnionFind(allConnectedNodeIds);

        // BEAM 연결
        foreach (var e in model.Elements)
            uf.Union(e.StartNodeId, e.EndNodeId);

        // RBE2 연결 — Rigid로 연결된 그룹을 병합
        foreach (var r in model.Rigids)
            foreach (var dep in r.DependentNodeIds)
                uf.Union(r.IndependentNodeId, dep);

        // 루트별 노드 목록
        var nodesByRoot = allConnectedNodeIds
            .GroupBy(id => uf.Find(id))
            .ToDictionary(g => g.Key, g => g.ToList());

        // 루트별 BEAM 요소 목록
        var elemsByRoot = model.Elements
            .GroupBy(e => uf.Find(e.StartNodeId))
            .ToDictionary(g => g.Key, g => g.ToList());

        var groups = nodesByRoot.Keys
            .OrderByDescending(r => elemsByRoot.GetValueOrDefault(r)?.Count ?? 0)
            .ThenBy(r => nodesByRoot[r].Min())   // 동률 시 최소 노드 ID로 결정적 정렬 — build/apply 재분석 id 일치 보장
            .Select((root, i) =>
            {
                var nodeList = nodesByRoot[root];
                var elemList = elemsByRoot.GetValueOrDefault(root) ?? [];
                return new ConnectivityGroupDto
                {
                    Id           = i,
                    NodeCount    = nodeList.Count,
                    ElementCount = elemList.Count,
                    NodeIds      = nodeList,
                    ElementIds   = elemList.Select(e => e.Id).ToList(),
                };
            })
            .ToList();

        var largest = groups.Count > 0 ? groups[0] : null;

        var referencedNodeIds = new HashSet<int>(allConnectedNodeIds);
        int isolatedNodeCount = model.Nodes.Count(n => !referencedNodeIds.Contains(n.Id));

        return new ConnectivityDto
        {
            GroupCount               = groups.Count,
            LargestGroupNodeCount    = largest?.NodeCount    ?? 0,
            LargestGroupElementCount = largest?.ElementCount ?? 0,
            LargestGroupNodeRatio    = model.Nodes.Count > 0
                ? Math.Round((double)(largest?.NodeCount ?? 0) / model.Nodes.Count, 4)
                : 0.0,
            IsolatedNodeCount        = isolatedNodeCount,
            Groups                   = groups,
        };
    }
}
