using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

public sealed class UboltAnchorVerticalizeStage : IPipelineStage
{
    public string Name => "UboltAnchorVerticalize";

    public bool Execute(StageContext ctx)
    {
        int verticalized = UboltRbeStage.VerticalizeSafeUboltAnchors(ctx, Name);
        if (verticalized > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "UBOLT_ANCHOR_VERTICALIZED",
                $"{verticalized} U-bolt anchor node(s) moved onto vertical support line.");
        return true;
    }
}
