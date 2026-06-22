using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Analysis;

public static class ModelDiagnosticsAnalyzer
{
    public static List<DiagnosticDto> Analyze(FeModel model, Tolerances tolerances)
    {
        var diagnostics = new List<DiagnosticDto>();
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var degree = BuildBeamDegree(model);

        foreach (var node in model.Nodes)
        {
            int deg = degree.GetValueOrDefault(node.Id);
            if (deg == 0)
            {
                // BEAM-graph에서만 차수=0. RBE/PointMass에는 여전히 붙어있을 수 있음.
                // 진짜 orphan(어디에도 참조 안 됨)은 FinalValidationStage의 ORPHAN_NODE 사용.
                diagnostics.Add(new DiagnosticDto
                {
                    Severity = "warning",
                    Code = "BEAM_ISOLATED_NODE",
                    NodeId = node.Id,
                    Message = $"Node {node.Id} is not connected to any BEAM element (still referenced by rigid/PointMass if any).",
                });
            }
            else if (deg == 1 && !node.Tags.HasFlag(NodeTags.Boundary))
            {
                diagnostics.Add(new DiagnosticDto
                {
                    Severity = "warning",
                    Code = "FREE_END_NODE",
                    NodeId = node.Id,
                    Message = $"Node {node.Id} is connected to exactly one BEAM element.",
                });
            }
        }

        foreach (var elem in model.Elements)
        {
            if (!nodeById.TryGetValue(elem.StartNodeId, out var a) ||
                !nodeById.TryGetValue(elem.EndNodeId, out var b))
                continue;

            double length = a.Position.DistanceTo(b.Position);
            if (length < tolerances.ShortElemMinMm)
            {
                diagnostics.Add(new DiagnosticDto
                {
                    Severity = "error",
                    Code = length <= tolerances.ZeroLengthMm ? "ZERO_LENGTH_ELEMENT" : "SHORT_ELEMENT",
                    ElemId = elem.Id,
                    Message = $"Element {elem.Id} length {length:F3} mm is below {tolerances.ShortElemMinMm:F3} mm.",
                });
            }
        }

        diagnostics.AddRange(DisconnectedGroupDiagnostics(model));
        return diagnostics;
    }

    private static Dictionary<int, int> BuildBeamDegree(FeModel model)
    {
        var degree = new Dictionary<int, int>();
        foreach (var elem in model.Elements)
        {
            degree[elem.StartNodeId] = degree.GetValueOrDefault(elem.StartNodeId) + 1;
            degree[elem.EndNodeId] = degree.GetValueOrDefault(elem.EndNodeId) + 1;
        }
        return degree;
    }

    private static IEnumerable<DiagnosticDto> DisconnectedGroupDiagnostics(FeModel model)
    {
        if (model.Elements.Count == 0) yield break;

        var elementNodeIds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .Distinct()
            .ToList();
        var uf = new UnionFind(elementNodeIds);
        foreach (var elem in model.Elements)
            uf.Union(elem.StartNodeId, elem.EndNodeId);

        var groups = model.Elements
            .GroupBy(e => uf.Find(e.StartNodeId))
            .Select(g => new
            {
                Root = g.Key,
                ElementCount = g.Count(),
                NodeCount = g.SelectMany(e => new[] { e.StartNodeId, e.EndNodeId }).Distinct().Count(),
            })
            .OrderByDescending(g => g.ElementCount)
            .Select((g, id) => new { Id = id, g.ElementCount, g.NodeCount })
            .ToList();

        foreach (var group in groups.Skip(1))
        {
            yield return new DiagnosticDto
            {
                Severity = "warning",
                Code = "GROUP_NOT_CONNECTED",
                GroupId = group.Id,
                Message = $"Group {group.Id} ({group.NodeCount} nodes, {group.ElementCount} elements) is not connected to the largest group.",
            };
        }
    }
}
