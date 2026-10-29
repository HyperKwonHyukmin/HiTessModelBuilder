namespace Cmb.Core.Model;

public enum TraceAction
{
    ElementCreated,
    ElementSplit,
    NodeMerged,
    NodeMoved,
    ElementRemoved,
    ElementPreserved,
    NodeCreated,
    NodeRemoved,
    // 입력 행이 있었으나 유효 형상/부착점을 만들지 못해 산출물에 반영되지 않고 누락됨(질량 손실 등 가시화용).
    ElementSkipped,
}

public sealed record TraceEvent(
    TraceAction Action,
    string      StageName,
    int?        ElementId,
    int?        NodeId,
    int?        RelatedElementId,
    int?        RelatedNodeId,
    string?     Note);
