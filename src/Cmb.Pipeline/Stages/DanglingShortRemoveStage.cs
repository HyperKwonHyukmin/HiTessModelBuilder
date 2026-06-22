using Cmb.Core.Model;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 한쪽 끝이 자유단(degree=1)이면서 길이가 임계값 미만인 꼬투리 요소를 제거합니다.
/// 제거 후 새로 생긴 dangling을 연쇄적으로 제거하기 위해 수렴 루프로 동작합니다.
/// </summary>
public sealed class DanglingShortRemoveStage : IPipelineStage
{
    public string Name => "DanglingShortRemove";

    public bool Execute(StageContext ctx)
    {
        var model     = ctx.Model;
        double thresh = ctx.Options.Tolerances.DanglingShortLengthMm;
        int maxIter   = ctx.Options.Tolerances.MaxConvergenceIterations;

        var pointMassNodes = new HashSet<int>(model.PointMasses.Select(pm => pm.NodeId));
        var rigidNodes     = new HashSet<int>(
            model.Rigids.SelectMany(r => r.DependentNodeIds.Append(r.IndependentNodeId)));

        // protected 요소는 한 번만 카운트/trace 기록 (pass 간 중복 방지)
        var preservedTraced = new HashSet<int>();
        int totalRemoved = 0, totalPreservedMass = 0, totalPreservedRbe = 0;
        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            int passRemoved = RunOnePass(ctx, thresh, pointMassNodes, rigidNodes,
                preservedTraced, ref totalPreservedMass, ref totalPreservedRbe);
            totalRemoved += passRemoved;
            if (passRemoved == 0) break;
        }

        if (totalRemoved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "DANGLING_SHORT_REMOVED",
                $"{totalRemoved} dangling short element(s) (< {thresh} mm) removed " +
                $"over {iter + 1} pass(es).");

        if (totalPreservedMass + totalPreservedRbe > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "DANGLING_SHORT_PRESERVED",
                $"{totalPreservedMass + totalPreservedRbe} short element(s) preserved " +
                $"(PointMass={totalPreservedMass}, RBE={totalPreservedRbe}).");

        if (iter >= maxIter)
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "DANGLING_SHORT_NOT_CONVERGED",
                $"DanglingShortRemove did not converge after {maxIter} iterations.");

        return true;
    }

    private static int RunOnePass(
        StageContext ctx,
        double thresh,
        HashSet<int> pointMassNodes,
        HashSet<int> rigidNodes,
        HashSet<int> preservedTraced,
        ref int totalPreservedMass,
        ref int totalPreservedRbe)
    {
        var model = ctx.Model;

        var degree = new Dictionary<int, int>();
        foreach (var e in model.Elements)
        {
            degree[e.StartNodeId] = degree.GetValueOrDefault(e.StartNodeId) + 1;
            degree[e.EndNodeId]   = degree.GetValueOrDefault(e.EndNodeId)   + 1;
        }

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var toRemove = new List<int>();

        foreach (var e in model.Elements)
        {
            if (!nodeById.TryGetValue(e.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(e.EndNodeId,   out var nB)) continue;

            int dA = degree.GetValueOrDefault(e.StartNodeId);
            int dB = degree.GetValueOrDefault(e.EndNodeId);
            if (dA != 1 && dB != 1) continue;

            if (nA.Position.DistanceTo(nB.Position) < thresh)
            {
                bool protectedByMass = pointMassNodes.Contains(e.StartNodeId) || pointMassNodes.Contains(e.EndNodeId);
                bool protectedByRbe  = rigidNodes.Contains(e.StartNodeId)     || rigidNodes.Contains(e.EndNodeId);
                if (protectedByMass || protectedByRbe)
                {
                    if (preservedTraced.Add(e.Id))
                    {
                        totalPreservedMass += protectedByMass ? 1 : 0;
                        totalPreservedRbe  += (protectedByRbe && !protectedByMass) ? 1 : 0;
                        ctx.AddTrace(TraceAction.ElementPreserved, "DanglingShortRemove",
                            elementId: e.Id, note: $"preserved (mass={protectedByMass}, rbe={protectedByRbe})");
                    }
                    continue;
                }

                toRemove.Add(e.Id);
            }
        }

        if (toRemove.Count == 0) return 0;

        var toRemoveSet = new HashSet<int>(toRemove);
        foreach (int id in toRemove)
            ctx.AddTrace(TraceAction.ElementRemoved, "DanglingShortRemove",
                elementId: id, note: $"dangling short (< {thresh} mm)");
        model.Elements.RemoveAll(e => toRemoveSet.Contains(e.Id));

        return toRemove.Count;
    }
}
