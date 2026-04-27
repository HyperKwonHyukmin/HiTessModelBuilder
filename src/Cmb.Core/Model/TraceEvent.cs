namespace Cmb.Core.Model;

public enum TraceAction
{
    ElementCreated,
    ElementSplit,
    NodeMerged,
    NodeMoved,
    ElementRemoved,
    ElementPreserved,
}

public sealed record TraceEvent(
    TraceAction Action,
    string      StageName,
    int?        ElementId,
    int?        NodeId,
    int?        RelatedElementId,
    int?        RelatedNodeId,
    string?     Note);
