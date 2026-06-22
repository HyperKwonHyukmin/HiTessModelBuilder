using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

internal static class RigidElementNormalizer
{
    public static int RemoveCircularDependencies(StageContext ctx, string stageName)
    {
        var removed = 0;

        while (TryFindDependencyCycle(ctx.Model.Rigids, out var cycle))
        {
            var removeId = cycle
                .Select(e => e.RigidId)
                .Distinct()
                .OrderByDescending(id => id)
                .First();
            var removedRigid = ctx.Model.Rigids.FirstOrDefault(r => r.Id == removeId);
            if (removedRigid is null) break;

            ctx.Model.Rigids.Remove(removedRigid);
            removed++;

            string nodes = string.Join(" -> ", cycle
                .Select(e => e.FromNodeId)
                .Append(cycle[^1].ToNodeId)
                .Select(id => $"N{id}"));
            string rigids = string.Join(",", cycle.Select(e => e.RigidId));
            ctx.AddTrace(TraceAction.ElementRemoved, stageName, elementId: removeId,
                note: $"removed circular RBE2 dependency ({nodes}) from RBE2 {rigids}");
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "RBE2_CIRCULAR_DEPENDENCY_REMOVED",
                $"Removed RBE2 {removeId} to break circular dependency ({nodes}) involving RBE2 {rigids}.",
                elementId: removeId,
                nodeId: removedRigid.IndependentNodeId);
        }

        return removed;
    }

    public static int MergeSharedDependentNodes(StageContext ctx, string stageName)
    {
        var model = ctx.Model;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var depToRigidIds = new Dictionary<int, List<int>>();

        foreach (var r in model.Rigids)
        {
            foreach (int dep in r.DependentNodeIds)
            {
                if (!depToRigidIds.TryGetValue(dep, out var ids))
                {
                    ids = [];
                    depToRigidIds[dep] = ids;
                }
                ids.Add(r.Id);
            }
        }

        int merged = 0;
        foreach (var (sharedDep, rigidIds) in depToRigidIds.Where(kv => kv.Value.Count >= 2).ToList())
        {
            var conflicts = model.Rigids.Where(r => rigidIds.Contains(r.Id)).ToList();
            if (conflicts.Count < 2) continue;

            var newDeps = new List<int>();
            var cmChars = new HashSet<char>();
            bool anyUbolt = false;
            int uboltMaster = -1;
            string remark = "MERGED_SHARED_DEP";
            string? sourceName = null;
            string? detailRemark = null;

            foreach (var r in conflicts)
            {
                newDeps.Add(r.IndependentNodeId);
                newDeps.AddRange(r.DependentNodeIds.Where(d => d != sharedDep));
                foreach (char c in r.Cm.Where(char.IsDigit))
                    cmChars.Add(c);

                if (r.Remark == "UBOLT")
                {
                    anyUbolt = true;
                    if (uboltMaster < 0) uboltMaster = r.IndependentNodeId;
                }

                sourceName ??= r.SourceName;
                detailRemark ??= r.DetailRemark;
            }

            string cm = cmChars.Count == 0
                ? "123456"
                : new string(cmChars.OrderBy(c => c).ToArray());

            // 여러 비-BOX U-bolt 가 같은 구조 노드(sharedDep)를 공유하면(파이프들이 한 구조 노드에 클램프),
            // 파이프를 master 로 prune 해 하나만 남기지 말고 — 구조 노드를 independent 로, 모든 U-bolt(파이프)
            // 노드를 dependent 로 통합한다(누락 없이). 단일 U-bolt(공유 없음)는 애초에 이 병합에 들어오지 않는다.
            bool allNonBoxUbolt = conflicts.All(r => r.Remark == "UBOLT" && !IsBoxUbolt(r.Remark, r.DetailRemark));

            int finalMaster = sharedDep;
            int massMaster = newDeps.FirstOrDefault(n => model.PointMasses.Any(pm => pm.NodeId == n));
            bool uboltSharedStructure = false;
            if (massMaster > 0)
            {
                finalMaster = massMaster;
                newDeps.RemoveAll(n => n == massMaster);
                newDeps.Add(sharedDep);
            }
            else if (allNonBoxUbolt)
            {
                finalMaster = sharedDep;        // 공유 구조 노드 = independent
                remark = "UBOLT";
                uboltSharedStructure = true;    // 아래 단일-dep prune 생략(다중 파이프 dep 보존)
            }
            else if (anyUbolt && uboltMaster > 0)
            {
                finalMaster = uboltMaster;
                newDeps.RemoveAll(n => n == uboltMaster);
                newDeps.Add(sharedDep);
                remark = "UBOLT";
            }

            newDeps = newDeps
                .Where(n => n != finalMaster)
                .Distinct()
                .ToList();
            if (newDeps.Count == 0) continue;

            // 비-BOX-UBOLT RBE2는 dep가 1개만 허용 — 가장 가까운 1개만 남기고 나머지 제거.
            // 단, BOX UBOLT(4방향)와 "U-bolt 공유 구조 노드 통합"(다중 파이프 dep)은 보존.
            int prunedCount = 0;
            if (newDeps.Count > 1 && !uboltSharedStructure && !IsBoxUbolt(remark, detailRemark)
                && nodeById.TryGetValue(finalMaster, out var masterNode))
            {
                int nearest = newDeps
                    .OrderBy(d => nodeById.TryGetValue(d, out var nd)
                        ? nd.Position.DistanceTo(masterNode.Position)
                        : double.MaxValue)
                    .First();
                prunedCount = newDeps.Count - 1;
                newDeps = [nearest];
            }

            model.Rigids.RemoveAll(r => conflicts.Any(c => c.Id == r.Id));
            int newId = model.AllocElemId();
            model.Rigids.Add(new RigidElement(newId, finalMaster, newDeps, remark, sourceName, cm, detailRemark));
            ctx.AddTrace(TraceAction.ElementCreated, stageName, elementId: newId,
                note: $"merged shared dependent node {sharedDep} from RBE2 {string.Join(",", conflicts.Select(r => r.Id))}");
            if (prunedCount > 0)
                ctx.AddDiagnostic(DiagnosticSeverity.Info, "RBE_NONBOX_DEP_PRUNED",
                    $"RBE2 {newId} pruned to nearest dep — {prunedCount} extra dep(s) dropped (non-BOX UBOLT).");
            merged++;
        }

        if (merged > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "RBE_SHARED_DEP_MERGED",
                $"{merged} shared dependent RBE2 set(s) merged.");

        return merged;
    }

    private static bool IsBoxUbolt(string remark, string? detailRemark)
        => remark == "UBOLT" &&
           string.Equals(detailRemark, "BOX", StringComparison.OrdinalIgnoreCase);

    private static bool TryFindDependencyCycle(
        IReadOnlyList<RigidElement> rigids,
        out List<RigidDependencyEdge> cycle)
    {
        var adjacency = new Dictionary<int, List<RigidDependencyEdge>>();
        foreach (var rigid in rigids.OrderBy(r => r.Id))
        {
            foreach (int dep in rigid.DependentNodeIds.OrderBy(id => id))
            {
                if (!adjacency.TryGetValue(rigid.IndependentNodeId, out var edges))
                {
                    edges = [];
                    adjacency[rigid.IndependentNodeId] = edges;
                }
                edges.Add(new RigidDependencyEdge(rigid.Id, rigid.IndependentNodeId, dep));
            }
        }

        var color = new Dictionary<int, int>();
        var nodeStack = new List<int>();
        var edgeStack = new List<RigidDependencyEdge>();
        var foundCycle = new List<RigidDependencyEdge>();

        foreach (int nodeId in adjacency.Keys.OrderBy(id => id).ToList())
        {
            if (color.GetValueOrDefault(nodeId) == 0 && Visit(nodeId))
            {
                cycle = foundCycle;
                return true;
            }
        }

        cycle = [];
        return false;

        bool Visit(int nodeId)
        {
            color[nodeId] = 1;
            nodeStack.Add(nodeId);

            if (adjacency.TryGetValue(nodeId, out var edges))
            {
                foreach (var edge in edges)
                {
                    int nextColor = color.GetValueOrDefault(edge.ToNodeId);
                    if (nextColor == 1)
                    {
                        int cycleStart = nodeStack.IndexOf(edge.ToNodeId);
                        if (cycleStart >= 0)
                        {
                            foundCycle = edgeStack.Skip(cycleStart).Append(edge).ToList();
                            return true;
                        }
                    }
                    else if (nextColor == 0)
                    {
                        edgeStack.Add(edge);
                        if (Visit(edge.ToNodeId)) return true;
                        edgeStack.RemoveAt(edgeStack.Count - 1);
                    }
                }
            }

            nodeStack.RemoveAt(nodeStack.Count - 1);
            color[nodeId] = 2;
            return false;
        }
    }

    private readonly record struct RigidDependencyEdge(int RigidId, int FromNodeId, int ToNodeId);
}
