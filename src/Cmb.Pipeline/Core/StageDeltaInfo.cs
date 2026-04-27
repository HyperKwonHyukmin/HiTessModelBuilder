namespace Cmb.Pipeline.Core;

public sealed record StageDeltaInfo(
    string FromStage,
    int    ElemCountBefore,
    int    ElemCountAfter,
    int    NodeCountBefore,
    int    NodeCountAfter)
{
    public int NetElementDelta => ElemCountAfter - ElemCountBefore;
    public int NetNodeDelta    => NodeCountAfter - NodeCountBefore;
}
