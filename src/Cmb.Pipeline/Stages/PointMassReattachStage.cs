using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 어떤 element/rigid에도 참조되지 않는 노드(=공중에 떠 있는 노드)에 붙은 PointMass(CONM2)를
/// 가장 가까운 Pipe 노드로 재연결합니다. Pipe 수정 알고리즘(Meshing/Intersection/GroupConnect 등)이
/// 동작하면서 PointMass가 pipe 기하를 따라가지 못해 고립된 케이스를 복구합니다.
/// </summary>
public sealed class PointMassReattachStage : IPipelineStage
{
    public string Name => "PointMassReattach";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        if (model.PointMasses.Count == 0) return true;

        var tols       = ctx.Options.Tolerances;
        double maxSearch  = tols.PointMassReattachMaxDistMm;
        double warnDist   = tols.PointMassReattachWarnDistMm;
        double cellSize   = tols.SpatialCellSizeMm;

        // 1. Pipe-connected 노드 ID 집합
        var pipeNodeIds = new HashSet<int>();
        foreach (var e in model.Elements)
        {
            if (e.Category != EntityCategory.Pipe) continue;
            pipeNodeIds.Add(e.StartNodeId);
            pipeNodeIds.Add(e.EndNodeId);
        }
        if (pipeNodeIds.Count == 0) return true;

        // 2. element 또는 rigid에 참조된 노드 ID 집합 (PointMass는 제외)
        var attachedNodeIds = new HashSet<int>();
        foreach (var e in model.Elements)
        {
            attachedNodeIds.Add(e.StartNodeId);
            attachedNodeIds.Add(e.EndNodeId);
        }
        foreach (var r in model.Rigids)
        {
            attachedNodeIds.Add(r.IndependentNodeId);
            foreach (int d in r.DependentNodeIds) attachedNodeIds.Add(d);
        }

        // 3. 고립된 PointMass 노드 ID 추출 (여러 PM이 같은 노드를 공유할 수 있음 → Set)
        var orphanPmNodeIds = new HashSet<int>();
        foreach (var pm in model.PointMasses)
            if (!attachedNodeIds.Contains(pm.NodeId))
                orphanPmNodeIds.Add(pm.NodeId);

        if (orphanPmNodeIds.Count == 0) return true;

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var pipeNodes = pipeNodeIds
            .Where(id => nodeById.ContainsKey(id))
            .Select(id => nodeById[id])
            .ToList();
        var pipeHash = new NodeSpatialHash(pipeNodes, cellSize);

        // 4. 각 고립 노드 → 최근접 pipe 노드 매핑 결정
        var remap     = new Dictionary<int, int>();
        int unresolved = 0;
        int farReattach = 0;
        double maxSq   = maxSearch * maxSearch;
        double sumDist = 0.0;
        double maxDist = 0.0;

        foreach (int oldNid in orphanPmNodeIds)
        {
            if (!nodeById.TryGetValue(oldNid, out var oldNode)) continue;
            var pos = oldNode.Position;

            int    bestId  = -1;
            double bestSq  = maxSq;

            foreach (int candId in pipeHash.QueryNear(pos, maxSearch))
            {
                if (candId == oldNid) continue;
                if (!nodeById.TryGetValue(candId, out var candNode)) continue;
                double dx = candNode.Position.X - pos.X;
                double dy = candNode.Position.Y - pos.Y;
                double dz = candNode.Position.Z - pos.Z;
                double dsq = dx * dx + dy * dy + dz * dz;
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    bestId = candId;
                }
            }

            if (bestId == -1)
            {
                unresolved++;
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "POINTMASS_REATTACH_UNRESOLVED",
                    $"Orphan PointMass node N{oldNid} at ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) " +
                    $"has no pipe candidate within {maxSearch:F0}mm — left unattached; OrphanNodeRemove drops it from the final model (mass excluded).",
                    nodeId: oldNid);
                continue;
            }

            remap[oldNid] = bestId;
            double bestDist = Math.Sqrt(bestSq);
            sumDist += bestDist;
            if (bestDist > maxDist) maxDist = bestDist;

            ctx.AddTrace(TraceAction.NodeMerged, Name,
                nodeId: oldNid, relatedNodeId: bestId,
                note: $"orphan PointMass N{oldNid} → pipe N{bestId} ({bestDist:F1}mm)");

            if (bestDist > warnDist)
            {
                farReattach++;
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "POINTMASS_REATTACH_FAR",
                    $"PointMass node N{oldNid} reattached to pipe N{bestId} at {bestDist:F1}mm " +
                    $"(threshold {warnDist:F0}mm) — verify mass location.",
                    nodeId: oldNid);
            }
        }

        if (remap.Count == 0)
        {
            return true;
        }

        // 5. PointMass 노드 ID 직접 재매핑 (RBE는 정의상 orphan PM 노드를 참조하지 않으므로 건드리지 않음)
        for (int i = 0; i < model.PointMasses.Count; i++)
        {
            var pm = model.PointMasses[i];
            if (remap.TryGetValue(pm.NodeId, out int newNid))
                model.PointMasses[i] = new PointMass(pm.Id, newNid, pm.Mass, pm.SourceName);
        }

        // 6. 더 이상 참조되지 않는 고립 노드 제거
        var stillReferenced = new HashSet<int>();
        foreach (var e in model.Elements)
        {
            stillReferenced.Add(e.StartNodeId);
            stillReferenced.Add(e.EndNodeId);
        }
        foreach (var r in model.Rigids)
        {
            stillReferenced.Add(r.IndependentNodeId);
            foreach (int d in r.DependentNodeIds) stillReferenced.Add(d);
        }
        foreach (var pm in model.PointMasses) stillReferenced.Add(pm.NodeId);

        var toRemove = remap.Keys.Where(id => !stillReferenced.Contains(id)).ToHashSet();
        if (toRemove.Count > 0)
            model.Nodes.RemoveAll(n => toRemove.Contains(n.Id));

        double meanDist = remap.Count > 0 ? sumDist / remap.Count : 0.0;
        ctx.AddDiagnostic(DiagnosticSeverity.Info, "POINTMASS_REATTACHED",
            $"{remap.Count} orphan PointMass node(s) reattached to nearest pipe node " +
            $"(removed: {toRemove.Count}, distance mean={meanDist:F1}mm max={maxDist:F1}mm, " +
            $"{farReattach} above {warnDist:F0}mm warn threshold).");

        if (unresolved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "POINTMASS_REATTACH_SUMMARY",
                $"{unresolved} orphan PointMass node(s) had no pipe candidate within {maxSearch:F0}mm.");

        return true;
    }
}
