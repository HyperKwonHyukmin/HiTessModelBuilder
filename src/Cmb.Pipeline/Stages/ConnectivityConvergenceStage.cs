using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// HiTess Stage 4/5와 같은 연결성 수렴 루프.
/// Extend 수렴 후 GroupConnect가 이동을 만들 때마다 topology cleanup과 Extend를 다시 수행합니다.
/// </summary>
public sealed class ConnectivityConvergenceStage : IPipelineStage
{
    public string Name => "ConnectivityConvergence";

    public bool Execute(StageContext ctx)
    {
        int maxIter = Math.Max(1, ctx.Options.Tolerances.GroupConnectMaxIterations);

        RunExtendAndCleanupIfMoved(ctx);

        int groupIterations = 0;
        int totalTranslated = 0;
        while (groupIterations < maxIter)
        {
            groupIterations++;

            var groupConnect = new GroupConnectStage();
            if (!groupConnect.Execute(ctx)) return false;

            int translated = groupConnect.LastTranslatedCount;
            totalTranslated += translated;
            if (translated <= 0) break;

            RunBaseCleanupOnce(ctx);
            RunExtendAndCleanupIfMoved(ctx);
        }

        if (groupIterations >= maxIter)
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "CONNECTIVITY_CONVERGENCE_LIMIT",
                $"Connectivity convergence reached the iteration limit ({maxIter}); additional group translations may remain.");

        if (groupIterations > 1 || totalTranslated > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "CONNECTIVITY_CONVERGED",
                $"Connectivity convergence completed after {groupIterations} group iteration(s), translated {totalTranslated} slave group(s).");

        return true;
    }

    private static void RunExtendAndCleanupIfMoved(StageContext ctx)
    {
        int before = ctx.Diagnostics.Count;
        if (!new ExtendToIntersectStage().Execute(ctx)) return;

        bool moved = ctx.Diagnostics
            .Skip(before)
            .Any(d => d.Code == "EXTEND_TO_INTERSECT");
        if (moved)
            RunBaseCleanupOnce(ctx);
    }

    private static void RunBaseCleanupOnce(StageContext ctx)
    {
        new SplitByExistingNodesStage().Execute(ctx);
        new DanglingShortRemoveStage().Execute(ctx);
        new CollinearNodeMergeStage().Execute(ctx);
    }
}
