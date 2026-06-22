using Cmb.Core.Model;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

public sealed class FinalMeshSizeStage : IPipelineStage
{
    private readonly double? _structureMeshSize;
    private readonly double? _pipeMeshSize;

    public FinalMeshSizeStage(double? structureMeshSize, double? pipeMeshSize)
    {
        _structureMeshSize = structureMeshSize;
        _pipeMeshSize = pipeMeshSize;
    }

    public string Name => "FinalMeshSize";

    public bool Execute(StageContext ctx)
    {
        if (_structureMeshSize is null && _pipeMeshSize is null)
            return true;

        var model = ctx.Model;
        model.SyncIdCounters();

        var elementIds = model.Elements.Select(e => e.Id).ToList();
        int splitCount = 0;

        foreach (int eid in elementIds)
        {
            var elem = model.Elements.Find(e => e.Id == eid);
            if (elem is null) continue;

            double? maxLen = elem.Category switch
            {
                EntityCategory.Structure => _structureMeshSize,
                EntityCategory.Pipe => _pipeMeshSize,
                _ => null,
            };
            if (maxLen is null) continue;

            var nA = model.FindNode(elem.StartNodeId);
            var nB = model.FindNode(elem.EndNodeId);
            if (nA is null || nB is null) continue;

            double length = nA.Position.DistanceTo(nB.Position);
            if (length < maxLen.Value) continue;

            int steps = (int)Math.Floor(length / maxLen.Value) + 1;
            if (steps < 2) continue;

            var dir = (nB.Position - nA.Position) * (1.0 / length);
            double stepLen = length / steps;

            var chain = new List<int> { nA.Id };
            for (int i = 1; i < steps; i++)
            {
                var pos = nA.Position + dir * (stepLen * i);
                var newNode = new Node(model.AllocNodeId(), pos);
                model.Nodes.Add(newNode);
                chain.Add(newNode.Id);
                ctx.AddTrace(TraceAction.NodeCreated, Name,
                    nodeId: newNode.Id, relatedElementId: eid,
                    note: $"final mesh segment {i}/{steps}");
            }
            chain.Add(nB.Id);

            model.Elements.RemoveAll(e => e.Id == eid);
            for (int i = 0; i < chain.Count - 1; i++)
            {
                int childId = model.AllocElemId();
                model.Elements.Add(new BeamElement(
                    childId, chain[i], chain[i + 1],
                    elem.PropertyId, elem.Category, elem.Orientation,
                    sourceName: elem.SourceName, parentElementId: elem.ParentElementId ?? eid));
                ctx.AddTrace(TraceAction.ElementSplit, Name,
                    elementId: childId, relatedElementId: eid,
                    note: $"split into {steps} final mesh segments");
            }

            splitCount++;
        }

        if (splitCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FINAL_MESH_SIZE_APPLIED",
                $"{splitCount} structure/pipe element(s) split by final mesh size.");

        return true;
    }
}
