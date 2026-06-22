namespace Cmb.Pipeline.Core;

public sealed record StageDeltaInfo(
    string FromStage,
    int    ElemCountBefore,
    int    ElemCountAfter,
    int    NodeCountBefore,
    int    NodeCountAfter,
    int    ElementsCreated,
    int    ElementsRemoved,
    int    ElementsSplit,
    int    NodesMerged,
    int    NodesMoved,
    int    NodesCreated = 0,
    int    NodesRemoved = 0)
{
    public int NetElementDelta => ElemCountAfter - ElemCountBefore;
    public int NetNodeDelta    => NodeCountAfter - NodeCountBefore;
}
