using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

public sealed class PreservedFreeEndCleanupStage : IPipelineStage
{
    public string Name => "PreservedFreeEndCleanup";

    public bool Execute(StageContext ctx)
    {
        GroupConnectStage.PruneStalePreservedFreeEndRigids(ctx, Name);
        return true;
    }
}
