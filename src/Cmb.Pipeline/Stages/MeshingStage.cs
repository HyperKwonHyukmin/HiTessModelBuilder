using Cmb.Core.Model;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

public sealed class MeshingStage : IPipelineStage
{
    public string Name => "Meshing";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();
        var tol = ctx.Options.Tolerances;

        var elementIds = model.Elements.Select(e => e.Id).ToList();
        int splitCount = 0;

        foreach (int eid in elementIds)
        {
            var elem = model.Elements.Find(e => e.Id == eid);
            if (elem is null) continue;
            if (elem.Category == EntityCategory.Equipment) continue;

            var nA = model.FindNode(elem.StartNodeId);
            var nB = model.FindNode(elem.EndNodeId);
            if (nA is null || nB is null) continue;

            double maxLen = elem.Category == EntityCategory.Structure
                ? tol.MeshingMaxLengthStructure
                : tol.MeshingMaxLengthPipe;

            double length = nA.Position.DistanceTo(nB.Position);
            if (length <= maxLen * 1.1) continue;

            int steps = (int)Math.Ceiling(length / maxLen);
            var dir = (nB.Position - nA.Position) * (1.0 / length);
            double stepLen = length / steps;

            var chain = new List<int> { nA.Id };
            for (int i = 1; i < steps; i++)
            {
                var pos = nA.Position + dir * (stepLen * i);
                var newNode = new Node(model.AllocNodeId(), pos);
                model.Nodes.Add(newNode);
                chain.Add(newNode.Id);
            }
            chain.Add(nB.Id);

            model.Elements.RemoveAll(e => e.Id == eid);

            for (int i = 0; i < chain.Count - 1; i++)
            {
                int childId = model.AllocElemId();
                model.Elements.Add(new BeamElement(
                    childId, chain[i], chain[i + 1],
                    elem.PropertyId, elem.Category, elem.Orientation,
                    sourceName: elem.SourceName, parentElementId: eid));
                ctx.AddTrace(TraceAction.ElementSplit, "Meshing",
                    elementId: childId, relatedElementId: eid,
                    note: $"split into {steps} segments");
            }

            splitCount++;
        }

        if (splitCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "MESHED",
                $"{splitCount} element(s) split by meshing.");

        return true;
    }

}
