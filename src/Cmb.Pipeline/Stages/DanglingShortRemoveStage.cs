using Cmb.Core.Model;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 한쪽 끝이 자유단(degree=1)이면서 길이가 임계값 미만인 꼬투리 요소를 제거합니다.
/// IntersectionStage 직후에 실행하여 교차 분할 시 생기는 짧은 stub을 정리합니다.
/// </summary>
public sealed class DanglingShortRemoveStage : IPipelineStage
{
    public string Name => "DanglingShortRemove";

    public bool Execute(StageContext ctx)
    {
        var model     = ctx.Model;
        double thresh = ctx.Options.Tolerances.DanglingShortLengthMm;

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
                toRemove.Add(e.Id);
        }

        foreach (int id in toRemove)
        {
            model.Elements.RemoveAll(e => e.Id == id);
            ctx.AddTrace(TraceAction.ElementRemoved, Name,
                elementId: id, note: $"dangling short (< {thresh} mm)");
        }

        if (toRemove.Count > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "DANGLING_SHORT_REMOVED",
                $"{toRemove.Count} dangling short element(s) (< {thresh} mm) removed.");

        return true;
    }
}
