// M-1 리팩터: Program.cs 에서 분리한 SPC 시드/그룹 커버리지/UBOLT 프리커넥트. 동작 불변.
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Analysis;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using Microsoft.Extensions.Logging;

internal static class SpcSeeding
{
    internal static IReadOnlyList<Diagnostic> PreconnectInitialUbolts(
        FeModel model,
        RunOptions options,
        ILogger logger)
    {
        logger.LogInformation("Initial UBOLT RBE preconnect starting");
    
        var uboltCtx = new StageContext(model, options, logger);
        new UboltRbeStage().Execute(uboltCtx);
    
        if (model.Rigids.Any(r => r.Remark == "UBOLT" && r.DependentNodeIds.Count > 0))
        {
            new SplitByExistingNodesStage().Execute(new StageContext(model, options, logger));
            int verticalized = UboltRbeStage.VerticalizeSafeUboltAnchors(uboltCtx);
            if (verticalized > 0)
                uboltCtx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_ANCHOR_VERTICALIZED",
                    $"{verticalized} U-bolt anchor node(s) moved onto vertical support line after pipe split.");
        }
    
        logger.LogInformation("Initial UBOLT RBE preconnect completed");
        return uboltCtx.Diagnostics;
    }
    
    internal static List<int> EnsureSpcPerGroup(
        FeModel model,
        IEnumerable<int> seedSpcIds,
        HashSet<int> rbeDepSet,
        out int addedCount)
    {
        var connectivity = ConnectivityAnalyzer.Analyze(model);
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
    
        var struElemNodes = new HashSet<int>();
        var elemNodes = new HashSet<int>();
        foreach (var e in model.Elements)
        {
            elemNodes.Add(e.StartNodeId);
            elemNodes.Add(e.EndNodeId);
            if (e.Category == EntityCategory.Structure)
            {
                struElemNodes.Add(e.StartNodeId);
                struElemNodes.Add(e.EndNodeId);
            }
        }
    
        var spcSet = seedSpcIds.ToHashSet();
        addedCount = 0;
        foreach (var group in connectivity.Groups)
        {
            if (group.NodeIds.Any(spcSet.Contains)) continue;
    
            // 우선순위: Structure 노드 minZ → 임의 element-연결 노드 minZ → 그룹 내 minZ
            Node? pick = group.NodeIds
                .Where(id => !rbeDepSet.Contains(id) && struElemNodes.Contains(id) && nodeById.ContainsKey(id))
                .Select(id => nodeById[id])
                .OrderBy(n => n.Position.Z).FirstOrDefault()
                ?? group.NodeIds
                    .Where(id => !rbeDepSet.Contains(id) && elemNodes.Contains(id) && nodeById.ContainsKey(id))
                    .Select(id => nodeById[id])
                    .OrderBy(n => n.Position.Z).FirstOrDefault()
                ?? group.NodeIds
                    .Where(id => !rbeDepSet.Contains(id) && nodeById.ContainsKey(id))
                    .Select(id => nodeById[id])
                    .OrderBy(n => n.Position.Z).FirstOrDefault();
    
            if (pick is not null)
            {
                spcSet.Add(pick.Id);
                addedCount++;
            }
        }
        return spcSet.ToList();
    }
    
    // 같은 높이로 보는 허용치 — 이보다 크게 아래로 이어지는 구조 부재가 있으면 '바닥'이 아니다.
    internal const double SpcLevelTolMm = 10.0;

    // 구조 요소의 이 비율 이상을 가진 구조 덩어리는 자기 바닥에 고정점이 있어야 한다.
    internal const double SpcMajorComponentRatio = 0.2;

    /// <summary>
    /// 모델 바닥 지지점(SPC1 123456 대상)을 고른다.
    /// ① 모델 구조 최저 Z + tolMm 띠 안의 <b>구조</b> 절점 중, 같은 높이(±SpcLevelTolMm)로 이어진 절점
    ///    묶음이 더 아래로 내려가는 구조 부재를 하나도 갖지 않을 때만 고른다 — 기둥 발·바닥 프레임만 남는다.
    /// ② 큰 구조 덩어리(구조 요소의 20% 이상)에 ①의 고정점이 하나도 없으면 그 덩어리 자신의 바닥에
    ///    같은 규칙을 적용한다. 모델 최저점이 아래층의 작은 배관 받침대이면 주 구조가 배관에만 매달려
    ///    준-메커니즘(변위 수 m~수십 m)이 되던 문제(07/09 샘플)를 막는다.
    /// 과거 규칙(띠 안의 모든 절점)은 바닥 근처 배관·장비 절점과 띠 안 중간 높이 가새까지 지면에 묶어
    /// 하중이 구조를 거치지 않고 빠졌다(03 샘플: 고정 380개 중 배관 148개).
    /// 구조 절점이 없거나 규칙이 아무것도 못 고르면 과거 규칙으로 되돌아간다.
    /// </summary>
    internal static IEnumerable<int> ResolveModelBottomNodes(
        FeModel model,
        double tolMm,
        HashSet<int>? excludedNodeIds = null)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var adj = new Dictionary<int, List<int>>();
        int structElemCount = 0;
        foreach (var e in model.Elements)
        {
            if (e.Category == EntityCategory.Pipe) continue;
            if (!nodeById.ContainsKey(e.StartNodeId) || !nodeById.ContainsKey(e.EndNodeId)) continue;
            (adj.TryGetValue(e.StartNodeId, out var la) ? la : adj[e.StartNodeId] = []).Add(e.EndNodeId);
            (adj.TryGetValue(e.EndNodeId, out var lb) ? lb : adj[e.EndNodeId] = []).Add(e.StartNodeId);
            structElemCount++;
        }

        var structural = adj.Keys
            .Where(id => excludedNodeIds is null || !excludedNodeIds.Contains(id))
            .ToList();
        if (structural.Count > 0)
        {
            var bottoms = SelectLevelBottoms(structural, adj, nodeById, tolMm);

            // ② 큰 구조 덩어리마다 자기 바닥 고정점 보장
            var chosen = bottoms.ToHashSet();
            foreach (var comp in StructureComponents(adj))
            {
                int compElems = comp.Sum(id => adj[id].Count) / 2;
                if (compElems < structElemCount * SpcMajorComponentRatio) continue;
                if (comp.Any(chosen.Contains)) continue;
                var compNodes = comp
                    .Where(id => excludedNodeIds is null || !excludedNodeIds.Contains(id))
                    .ToList();
                if (compNodes.Count == 0) continue;
                foreach (int id in SelectLevelBottoms(compNodes, adj, nodeById, tolMm))
                    if (chosen.Add(id)) bottoms.Add(id);
            }

            if (bottoms.Count > 0) return bottoms.OrderBy(id => id).ToList();
        }

        // 폴백: 과거 규칙 — 모델 최저 Z 띠 안의 모든 절점
        var candidates = excludedNodeIds is null
            ? model.Nodes
            : model.Nodes.Where(n => !excludedNodeIds.Contains(n.Id)).ToList();
        if (candidates.Count == 0) return [];

        double minZ = candidates.Min(n => n.Position.Z);
        return candidates
            .Where(n => n.Position.Z <= minZ + tolMm)
            .Select(n => n.Id)
            .ToList();
    }

    // nodes 중 최저 Z + tolMm 띠 안에서, 더 아래로 이어지는 구조 부재가 없는 높이 묶음의 절점만 고른다.
    private static List<int> SelectLevelBottoms(
        IReadOnlyCollection<int> nodes,
        Dictionary<int, List<int>> adj,
        Dictionary<int, Node> nodeById,
        double tolMm)
    {
        double minZ = nodes.Min(id => nodeById[id].Position.Z);
        var band = nodes.Where(id => nodeById[id].Position.Z <= minZ + tolMm).ToHashSet();

        // 같은 높이 묶음(level) — band 안에서 거의 수평인 구조 부재로 이어진 절점들
        var level = new Dictionary<int, int>();
        int levelCount = 0;
        foreach (int seed in band.OrderBy(id => id))
        {
            if (level.ContainsKey(seed)) continue;
            var stack = new Stack<int>([seed]);
            level[seed] = levelCount;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                double zu = nodeById[u].Position.Z;
                foreach (int v in adj[u])
                {
                    if (!band.Contains(v) || level.ContainsKey(v)) continue;
                    if (Math.Abs(nodeById[v].Position.Z - zu) > SpcLevelTolMm) continue;
                    level[v] = levelCount;
                    stack.Push(v);
                }
            }
            levelCount++;
        }

        // 더 아래로 내려가는 구조 부재를 가진 level 은 바닥이 아니다(가새·중간 보·기둥 중간)
        var supportedFromBelow = new HashSet<int>();
        foreach (int u in band)
        {
            double zu = nodeById[u].Position.Z;
            if (adj[u].Any(v => nodeById[v].Position.Z < zu - SpcLevelTolMm))
                supportedFromBelow.Add(level[u]);
        }

        return band
            .Where(id => !supportedFromBelow.Contains(level[id]))
            .OrderBy(id => id)
            .ToList();
    }

    // 구조 부재(CBEAM, 배관 제외)만으로 이어진 연결 덩어리
    private static List<List<int>> StructureComponents(Dictionary<int, List<int>> adj)
    {
        var seen = new HashSet<int>();
        var comps = new List<List<int>>();
        foreach (int seed in adj.Keys.OrderBy(id => id))
        {
            if (!seen.Add(seed)) continue;
            var comp = new List<int>();
            var stack = new Stack<int>([seed]);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                comp.Add(u);
                foreach (int v in adj[u])
                    if (seen.Add(v)) stack.Push(v);
            }
            comps.Add(comp);
        }
        return comps;
    }
}
