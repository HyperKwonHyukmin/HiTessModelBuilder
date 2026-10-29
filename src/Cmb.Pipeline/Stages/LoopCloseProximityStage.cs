using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 같은 그룹(connected component)에 속하지만 위상적으로 멀리(빔/RBE 그래프 minHops 이상) 떨어진
/// 두 구조 부재가 공간상 가까이(단면 최대 Dim + margin 이내) 붙어 있으면, 그 두 부재의
/// <b>가장 가까운 노드쌍을 단 1개의 RBE2 로 연결</b>한다(상하 적층 교차의 접합 보강).
///
/// 배경: 기존 근접 연결 stage(GroupConnect/FinalMeshProximityConnect)는 "다른 그룹"만 잇고
/// 같은 컴포넌트면 skip 한다. 그래서 먼 경로로 같은 그룹에 묶였지만 국부적으로는 떨어져 있는 부재가
/// 연결되지 않는다.
///
/// 연결 규칙 — <b>상호 최근접(Mutual Nearest Neighbor)</b>:
///  · 각 구조 노드 S 의 "가장 가까운 적격 상대 노드" T 를 구한다(투영 없이 기존 노드끼리).
///    적격 = 같은 그룹 · 위상상 ≥minHops · 거리 ≤ max(단면Dim)+margin · 인접 부재 거의 평행 아님.
///  · S↔T 가 서로 최근접일 때만(nearest[S]=T 이고 nearest[T]=S) 단일-dependent RBE2 1개를 만든다.
/// 이렇게 하면 한 부재쌍은 가장 가까운 노드쌍 1곳에서만 연결되어, 같은 쌍의 양방향 중복이나
/// 여러 dependent 묶음, 먼 투영점 연결이 생기지 않는다. Remark = "LOOP_CLOSE_PROXIMITY".
/// 기존 노드끼리 잇기 때문에 별도 투영 노드/타겟 분할이 필요 없다.
///
/// FreeEndRbe 가 축 방향 우선으로 받침 부재에 붙인 자유단(FREE_END_RBE + DetailRemark "AXIAL" 의
/// dependent)은 source/target 모두에서 뺀다. 그 자유단은 옆 부재와 위상상 멀어져 여기서 다시 묶이고,
/// 이어지는 RBE 병합으로 옆 부재·받침 부재·자유단이 한 RBE 로 뭉쳐 연결 의도가 무너진다
/// (3496-35210-A508372 N291 ↔ 옆 앵글 N166). 일반 FREE_END_RBE 까지 빼면 10개 실측 세트에서
/// 기존 loop-close 수십 건이 사라져 범위를 축 방향 표식으로 좁혔다.
/// </summary>
public sealed class LoopCloseProximityStage : IPipelineStage
{
    public string Name => "LoopCloseProximity";
    public const string LoopCloseRemark = "LOOP_CLOSE_PROXIMITY";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        var tol = ctx.Options.Tolerances;
        model.SyncIdCounters();
        if (model.Elements.Count == 0) return true;

        int minHops = Math.Max(2, tol.LoopCloseMinTopoHops);

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var elemById = model.Elements.ToDictionary(e => e.Id);
        var sectionById = model.Sections.ToDictionary(s => s.Id);

        // 위상 그래프(노드 인접): CBEAM + RBE — 홉 거리 판정용
        var adjacency = BuildNodeAdjacency(model);

        // 같은 컴포넌트 판정용 UnionFind (CBEAM + RBE)
        var uf = new UnionFind(model.Nodes.Select(n => n.Id));
        foreach (var e in model.Elements) uf.Union(e.StartNodeId, e.EndNodeId);
        foreach (var r in model.Rigids)
            foreach (int dep in r.DependentNodeIds)
                uf.Union(r.IndependentNodeId, dep);

        // 노드별 인접 구조 부재 목록 (방향/Dim 계산용) + 구조 노드 집합
        var nodeToStructElems = new Dictionary<int, List<int>>();
        var structureNodeIds = new HashSet<int>();
        foreach (var e in model.Elements)
        {
            if (e.Category != EntityCategory.Structure) continue;
            structureNodeIds.Add(e.StartNodeId);
            structureNodeIds.Add(e.EndNodeId);
            AddIncident(nodeToStructElems, e.StartNodeId, e.Id);
            AddIncident(nodeToStructElems, e.EndNodeId, e.Id);
        }
        if (structureNodeIds.Count == 0) return true;

        // 이미 받침 부재에 연결된 자유단 — loop-close 후보에서 제외.
        // FreeEndRbe 가 축 방향 받침에 붙인 자유단(AXIAL), GroupConnect 가 RBE2 로 master 에 붙인 그룹 끝.
        // (3496-35210-A508372 N291: 아래 H빔에 붙인 기둥 하단이 옆 앵글과 다시 묶여 의도가 무너지던 문제)
        var freeEndResolved = model.Rigids
            .Where(r => r.DetailRemark == FreeEndRbeStage.AxialDetailRemark)
            .SelectMany(r => r.DependentNodeIds)
            .Concat(model.Rigids
                .Where(r => r.Remark == GroupConnectStage.GroupConnectRemark)
                .SelectMany(r => r.DependentNodeIds.Append(r.IndependentNodeId)))
            .ToHashSet();

        // 구조 노드별 대표 방향 / 최대 단면 Dim 선계산(없으면 후보에서 제외)
        var geom = new Dictionary<int, (Vector3 Dir, double Dim)>();
        foreach (int nid in structureNodeIds)
            if (TryGetSourceGeometry(nid, nodeToStructElems, elemById, nodeById, sectionById,
                    out var dir, out double dim))
                geom[nid] = (dir, dim);

        var nodeHash = new NodeSpatialHash(model.Nodes, tol.SpatialCellSizeMm);
        double maxSearch = tol.FreeEndRbeMaxSearchMm + tol.FinalMeshProximityExtraMarginMm;

        // 1) 각 구조 노드의 "가장 가까운 적격 상대 노드"를 구한다(투영 없이 기존 노드끼리).
        var nearest = new Dictionary<int, (int Node, double Dist)>();
        foreach (int s in structureNodeIds.OrderBy(id => id))
        {
            if (!geom.TryGetValue(s, out var sg)) continue;
            if (freeEndResolved.Contains(s)) continue;
            var sPos = nodeById[s].Position;
            int sRoot = uf.Find(s);

            double bestDist = double.MaxValue;
            int bestNode = -1;
            foreach (int t in nodeHash.QueryNear(sPos, maxSearch))
            {
                if (t == s) continue;
                if (!geom.TryGetValue(t, out var tg)) continue;        // 구조 노드만
                if (freeEndResolved.Contains(t)) continue;             // 축 방향 자유단 RBE 로 해결된 노드
                if (uf.Find(t) != sRoot) continue;                     // 같은 그룹만
                if (IsWithinHops(adjacency, s, t, t, minHops)) continue; // 위상상 가까우면(같은 부재 등) 제외

                double d = sPos.DistanceTo(nodeById[t].Position);
                double allowed = Math.Max(sg.Dim, tg.Dim) + tol.FinalMeshProximityExtraMarginMm;
                if (d > allowed) continue;                             // 상하 범위(max단면Dim+margin) 밖

                double denom = sg.Dir.Length * tg.Dir.Length;
                if (denom > 1e-12)
                {
                    double cos = Math.Abs(Vector3.Dot(sg.Dir, tg.Dir) / denom);
                    if (cos > 0.985) continue;                         // 거의 평행한 부재끼리는 제외(교차/T-접합만)
                }

                if (d < bestDist - 1e-9 || (Math.Abs(d - bestDist) <= 1e-9 && t < bestNode))
                {
                    bestDist = d;
                    bestNode = t;
                }
            }
            if (bestNode != -1) nearest[s] = (bestNode, bestDist);
        }

        // 2) 상호 최근접(MNN) 쌍만 단일-dependent RBE2 1개로 연결한다.
        var rbeEdges = BuildRbeEdges(model);
        var elementEdges = model.Elements.Select(e => EdgeKey(e.StartNodeId, e.EndNodeId)).ToHashSet();
        int created = 0, skippedExistingEdge = 0;
        var emitted = new HashSet<(int, int)>();

        foreach (int s in nearest.Keys.OrderBy(id => id))
        {
            int t = nearest[s].Node;
            if (!nearest.TryGetValue(t, out var back) || back.Node != s) continue;   // 상호 최근접만
            var edge = EdgeKey(s, t);
            if (!emitted.Add(edge)) continue;                                        // (s,t)==(t,s) 1회
            if (elementEdges.Contains(edge) || rbeEdges.Contains(edge)) { skippedExistingEdge++; continue; }

            int gn = Math.Min(s, t);
            int gm = Math.Max(s, t);
            int rbeId = model.AllocElemId();
            model.Rigids.Add(new RigidElement(rbeId, gn, [gm], LoopCloseRemark));
            rbeEdges.Add(edge);
            ctx.AddTrace(TraceAction.ElementCreated, Name, elementId: rbeId,
                nodeId: gn, relatedNodeId: gm,
                note: $"loop-close MNN N{gn}↔N{gm} ({nearest[s].Dist:F1}mm, same group / far topo ≥{minHops} hops)");
            created++;
        }

        if (created > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "LOOP_CLOSE_RBE_CREATED",
                $"{created} loop-close RBE2(s) created via mutual-nearest node pairs " +
                $"(spatially near, topologically ≥{minHops} hops within the same group).");
        if (skippedExistingEdge > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "LOOP_CLOSE_EDGE_SKIPPED",
                $"{skippedExistingEdge} loop-close link(s) skipped — element/RBE edge already exists.");

        return true;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AddIncident(Dictionary<int, List<int>> map, int nodeId, int elemId)
    {
        if (!map.TryGetValue(nodeId, out var list)) { list = []; map[nodeId] = list; }
        list.Add(elemId);
    }

    /// <summary>node 의 인접 구조 부재로부터 대표 방향과 최대 단면 Dim 을 구한다.</summary>
    private static bool TryGetSourceGeometry(
        int nodeId,
        Dictionary<int, List<int>> nodeToStructElems,
        Dictionary<int, BeamElement> elemById,
        Dictionary<int, Node> nodeById,
        Dictionary<int, BeamSection> sectionById,
        out Vector3 direction,
        out double maxDim)
    {
        direction = default;
        maxDim = 0.0;
        bool found = false;

        if (!nodeToStructElems.TryGetValue(nodeId, out var elems)) return false;
        foreach (int eid in elems)
        {
            if (!elemById.TryGetValue(eid, out var elem)) continue;
            if (sectionById.TryGetValue(elem.PropertyId, out var sec))
                maxDim = Math.Max(maxDim, sec.MaxCrossSectionDim());

            if (!found &&
                nodeById.TryGetValue(elem.StartNodeId, out var a) &&
                nodeById.TryGetValue(elem.EndNodeId, out var b))
            {
                var span = b.Position - a.Position;
                if (span.Length > 1e-9) { direction = span; found = true; }
            }
        }
        return found;
    }

    /// <summary>source 에서 tA/tB 중 하나가 (minHops-1) 홉 이내로 도달 가능하면 true(=위상상 너무 가까움).</summary>
    private static bool IsWithinHops(Dictionary<int, List<int>> adj, int source, int tA, int tB, int minHops)
    {
        if (source == tA || source == tB) return true;
        int depthLimit = minHops - 1;
        if (depthLimit < 1) return false;

        var visited = new HashSet<int> { source };
        var frontier = new List<int> { source };
        for (int depth = 1; depth <= depthLimit; depth++)
        {
            var next = new List<int>();
            foreach (int u in frontier)
            {
                if (!adj.TryGetValue(u, out var neighbors)) continue;
                foreach (int v in neighbors)
                {
                    if (!visited.Add(v)) continue;
                    if (v == tA || v == tB) return true;
                    next.Add(v);
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        return false;
    }

    private static Dictionary<int, List<int>> BuildNodeAdjacency(FeModel model)
    {
        var adj = new Dictionary<int, List<int>>();
        void Edge(int a, int b)
        {
            if (a == b) return;
            if (!adj.TryGetValue(a, out var la)) { la = []; adj[a] = la; }
            if (!adj.TryGetValue(b, out var lb)) { lb = []; adj[b] = lb; }
            la.Add(b);
            lb.Add(a);
        }
        foreach (var e in model.Elements) Edge(e.StartNodeId, e.EndNodeId);
        foreach (var r in model.Rigids)
            foreach (int dep in r.DependentNodeIds)
                Edge(r.IndependentNodeId, dep);
        return adj;
    }

    private static HashSet<(int, int)> BuildRbeEdges(FeModel model)
    {
        var edges = new HashSet<(int, int)>();
        foreach (var r in model.Rigids)
            foreach (int dep in r.DependentNodeIds)
                edges.Add(EdgeKey(r.IndependentNodeId, dep));
        return edges;
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
}
