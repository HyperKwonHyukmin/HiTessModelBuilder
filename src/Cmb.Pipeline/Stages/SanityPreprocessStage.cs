using Cmb.Core.Model;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

public sealed class SanityPreprocessStage : IPipelineStage
{
    public string Name => "SanityPreprocess";

    public bool Execute(StageContext ctx)
    {
        RemoveDuplicateElements(ctx);
        RemoveShortElements(ctx);
        return true;
    }

    private static void RemoveDuplicateElements(StageContext ctx)
    {
        var model = ctx.Model;
        var seen = new HashSet<(int, int)>();
        var toRemove = new List<int>();

        foreach (var elem in model.Elements)
        {
            int lo = Math.Min(elem.StartNodeId, elem.EndNodeId);
            int hi = Math.Max(elem.StartNodeId, elem.EndNodeId);
            if (!seen.Add((lo, hi)))
                toRemove.Add(elem.Id);
        }

        foreach (int id in toRemove)
        {
            model.Elements.RemoveAll(e => e.Id == id);
            ctx.AddTrace(TraceAction.ElementRemoved, "SanityPreprocess", elementId: id, note: "duplicate");
        }

        if (toRemove.Count > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "DUPLICATE_REMOVED",
                $"{toRemove.Count} duplicate element(s) removed.");
    }

    private static void RemoveShortElements(StageContext ctx)
    {
        var model = ctx.Model;
        double tol = ctx.Options.Tolerances.ShortElemMinMm;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var toRemove = new List<int>();

        foreach (var elem in model.Elements)
        {
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId, out var nB)) continue;
            if (nA.Position.DistanceTo(nB.Position) < tol)
                toRemove.Add(elem.Id);
        }

        foreach (int id in toRemove)
        {
            model.Elements.RemoveAll(e => e.Id == id);
            ctx.AddTrace(TraceAction.ElementRemoved, "SanityPreprocess", elementId: id,
                note: $"short (< {tol} mm)");
        }

        if (toRemove.Count > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "SHORT_REMOVED",
                $"{toRemove.Count} short element(s) (< {tol} mm) removed.");
    }
}
