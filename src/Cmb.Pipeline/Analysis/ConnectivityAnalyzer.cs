using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Analysis;

public static class ConnectivityAnalyzer
{
    /// <summary>
    /// 모델의 BeamElement 연결 그래프를 분석하여 연결 그룹 통계를 반환합니다.
    /// Rigid는 연결성에 포함하지 않으므로 구조 빔 연결성을 명확히 추적할 수 있습니다.
    /// </summary>
    public static ConnectivityDto Analyze(FeModel model)
    {
        if (model.Elements.Count == 0)
            return new ConnectivityDto { GroupCount = 0 };

        var elementNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .Distinct()
            .ToList();

        var uf = new UnionFind(elementNodeIds);
        foreach (var e in model.Elements)
            uf.Union(e.StartNodeId, e.EndNodeId);

        // 각 루트별 노드 수 / 요소 수 집계
        var nodesByRoot = elementNodeIds
            .GroupBy(id => uf.Find(id))
            .ToDictionary(g => g.Key, g => g.Count());

        var elemsByRoot = model.Elements
            .GroupBy(e => uf.Find(e.StartNodeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var groups = nodesByRoot.Keys
            .OrderByDescending(r => elemsByRoot.GetValueOrDefault(r, 0))
            .Select((root, i) => new ConnectivityGroupDto
            {
                Id           = i + 1,
                NodeCount    = nodesByRoot[root],
                ElementCount = elemsByRoot.GetValueOrDefault(root, 0),
            })
            .ToList();

        int totalElemNodeCount = elementNodeIds.Count;
        var largest = groups.Count > 0 ? groups[0] : null;

        var referencedNodeIds = new HashSet<int>(elementNodeIds);
        int isolatedNodeCount = model.Nodes.Count(n => !referencedNodeIds.Contains(n.Id));

        return new ConnectivityDto
        {
            GroupCount               = groups.Count,
            LargestGroupNodeCount    = largest?.NodeCount    ?? 0,
            LargestGroupElementCount = largest?.ElementCount ?? 0,
            LargestGroupNodeRatio    = totalElemNodeCount > 0
                ? Math.Round((double)(largest?.NodeCount ?? 0) / totalElemNodeCount, 4)
                : 0.0,
            IsolatedNodeCount        = isolatedNodeCount,
            Groups                   = groups,
        };
    }
}
