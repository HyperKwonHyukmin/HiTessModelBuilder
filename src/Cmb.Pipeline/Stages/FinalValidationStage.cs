using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Stages;

public sealed class FinalValidationStage : IPipelineStage
{
    public string Name => "FinalValidation";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;

        CheckMissingProperties(ctx, model);
        CheckMissingMaterials(ctx, model);
        CheckOrphanNodes(ctx, model);
        CheckFreeEndNodes(ctx, model);

        bool hasError = ctx.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        return !hasError;
    }

    private static void CheckMissingProperties(StageContext ctx, FeModel model)
    {
        var sectionIds = model.Sections.Select(s => s.Id).ToHashSet();
        foreach (var elem in model.Elements)
        {
            if (!sectionIds.Contains(elem.PropertyId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_PROPERTY",
                    $"Element {elem.Id} references PropertyId {elem.PropertyId} which does not exist.",
                    elementId: elem.Id);
        }
    }

    private static void CheckMissingMaterials(StageContext ctx, FeModel model)
    {
        var materialIds = model.Materials.Select(m => m.Id).ToHashSet();
        foreach (var section in model.Sections)
        {
            if (!materialIds.Contains(section.MaterialId))
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "MISSING_MATERIAL",
                    $"Section {section.Id} references MaterialId {section.MaterialId} which does not exist.");
        }
    }

    private static void CheckOrphanNodes(StageContext ctx, FeModel model)
    {
        var referencedNodeIds = new HashSet<int>();
        foreach (var elem in model.Elements)
        {
            referencedNodeIds.Add(elem.StartNodeId);
            referencedNodeIds.Add(elem.EndNodeId);
        }
        foreach (var rigid in model.Rigids)
        {
            referencedNodeIds.Add(rigid.IndependentNodeId);
            foreach (var dep in rigid.DependentNodeIds)
                referencedNodeIds.Add(dep);
        }
        foreach (var pm in model.PointMasses)
            referencedNodeIds.Add(pm.NodeId);

        foreach (var node in model.Nodes)
        {
            if (!referencedNodeIds.Contains(node.Id))
                ctx.AddDiagnostic(DiagnosticSeverity.Warning, "ORPHAN_NODE",
                    $"Node {node.Id} at ({node.Position.X:F1},{node.Position.Y:F1},{node.Position.Z:F1}) is not referenced by any element.",
                    nodeId: node.Id);
        }
    }

    private static void CheckFreeEndNodes(StageContext ctx, FeModel model)
    {
        var nodeRefCount = new Dictionary<int, int>();
        foreach (var elem in model.Elements)
        {
            nodeRefCount[elem.StartNodeId] = nodeRefCount.GetValueOrDefault(elem.StartNodeId) + 1;
            nodeRefCount[elem.EndNodeId]   = nodeRefCount.GetValueOrDefault(elem.EndNodeId)   + 1;
        }

        // RBE 연결 노드도 차수에 포함 (U-bolt dependent 파이프 노드 등이 오검출되는 것 방지)
        // 단, 이미 요소에 연결된 노드에 한정 (RBE 전용 노드를 새로 추가하면 안 됨)
        foreach (var rigid in model.Rigids)
        {
            if (nodeRefCount.ContainsKey(rigid.IndependentNodeId))
                nodeRefCount[rigid.IndependentNodeId]++;
            foreach (int dep in rigid.DependentNodeIds)
                if (nodeRefCount.ContainsKey(dep))
                    nodeRefCount[dep]++;
        }

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        foreach (var (nodeId, count) in nodeRefCount)
        {
            if (count != 1) continue;
            if (!nodeById.TryGetValue(nodeId, out var node)) continue;
            if (node.Tags.HasFlag(NodeTags.Boundary)) continue;

            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "FREE_END_NODE",
                $"Node {nodeId} is a free end (connected to exactly 1 element).",
                nodeId: nodeId);
        }
    }
}
