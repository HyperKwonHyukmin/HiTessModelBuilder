using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Serialization;
using Cmb.Pipeline.Core;

namespace Cmb.Pipeline.Analysis;

public static class HealthMetricsAnalyzer
{
    public static HealthMetricsDto Analyze(
        FeModel model,
        IReadOnlyList<Diagnostic>? diagnostics = null,
        Tolerances? tolerances = null)
    {
        var tol = tolerances ?? new Tolerances();
        var connectivity = ConnectivityAnalyzer.Analyze(model);

        return new HealthMetricsDto
        {
            Totals           = ComputeTotals(model),
            Issues           = ComputeIssues(model, tol, connectivity),
            DiagnosticCounts = ComputeDiagnosticCounts(diagnostics),
            ElementQuality   = ComputeElementQuality(model, tol),
        };
    }

    // ── Totals ────────────────────────────────────────────────────────────────

    private static HealthTotalsDto ComputeTotals(FeModel model)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);

        var elemsByCategory = new Dictionary<string, int>();
        var lengthByCategory = new Dictionary<string, double>();

        double totalLength = 0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (var node in model.Nodes)
        {
            if (node.Position.X < minX) minX = node.Position.X;
            if (node.Position.Y < minY) minY = node.Position.Y;
            if (node.Position.Z < minZ) minZ = node.Position.Z;
            if (node.Position.X > maxX) maxX = node.Position.X;
            if (node.Position.Y > maxY) maxY = node.Position.Y;
            if (node.Position.Z > maxZ) maxZ = node.Position.Z;
        }

        foreach (var elem in model.Elements)
        {
            var cat = elem.Category.ToString();
            elemsByCategory[cat] = elemsByCategory.GetValueOrDefault(cat) + 1;

            double len = 0;
            if (nodeById.TryGetValue(elem.StartNodeId, out var sn) &&
                nodeById.TryGetValue(elem.EndNodeId,   out var en))
            {
                len = sn.Position.DistanceTo(en.Position);
            }
            totalLength += len;
            lengthByCategory[cat] = lengthByCategory.GetValueOrDefault(cat) + len;
        }

        bool hasNodes = model.Nodes.Count > 0;
        return new HealthTotalsDto
        {
            NodeCount          = model.Nodes.Count,
            ElementCount       = model.Elements.Count,
            RigidCount         = model.Rigids.Count,
            PointMassCount     = model.PointMasses.Count,
            ElementsByCategory = elemsByCategory,
            TotalLengthMm      = Math.Round(totalLength, 2),
            LengthByCategoryMm = lengthByCategory.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            Bbox = hasNodes
                ? new BBoxDto { MinX = minX, MinY = minY, MinZ = minZ, MaxX = maxX, MaxY = maxY, MaxZ = maxZ }
                : new BBoxDto(),
        };
    }

    // ── Issues ────────────────────────────────────────────────────────────────

    private static HealthIssuesDto ComputeIssues(FeModel model, Tolerances tol, ConnectivityDto connectivity)
    {
        return new HealthIssuesDto
        {
            FreeEndNodes       = CountFreeEndNodes(model),
            OrphanNodes        = connectivity.IsolatedNodeCount,
            ShortElements      = CountShortElements(model, tol),
            UnresolvedUbolts   = CountUnresolvedUbolts(model),
            DisconnectedGroups = connectivity.GroupCount > 0 ? connectivity.GroupCount - 1 : 0,
        };
    }

    // TODO: 이 로직은 FinalValidationStage.CheckFreeEndNodes 와 동일합니다.
    // 후속 작업에서 NodeDegreeCalculator 공용 유틸로 추출하여 drift 를 방지하세요.
    private static int CountFreeEndNodes(FeModel model)
    {
        var nodeRefCount = new Dictionary<int, int>();
        foreach (var elem in model.Elements)
        {
            nodeRefCount[elem.StartNodeId] = nodeRefCount.GetValueOrDefault(elem.StartNodeId) + 1;
            nodeRefCount[elem.EndNodeId]   = nodeRefCount.GetValueOrDefault(elem.EndNodeId)   + 1;
        }

        // RBE 연결 노드도 차수에 포함 (이미 요소에 연결된 노드에 한정)
        foreach (var rigid in model.Rigids)
        {
            if (nodeRefCount.ContainsKey(rigid.IndependentNodeId))
                nodeRefCount[rigid.IndependentNodeId]++;
            foreach (int dep in rigid.DependentNodeIds)
                if (nodeRefCount.ContainsKey(dep))
                    nodeRefCount[dep]++;
        }

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        int count = 0;
        foreach (var (nodeId, degree) in nodeRefCount)
        {
            if (degree != 1) continue;
            if (!nodeById.TryGetValue(nodeId, out var node)) continue;
            if (node.Tags.HasFlag(NodeTags.Boundary)) continue;
            count++;
        }
        return count;
    }

    private static int CountShortElements(FeModel model, Tolerances tol)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        int count = 0;
        foreach (var elem in model.Elements)
        {
            if (!nodeById.TryGetValue(elem.StartNodeId, out var sn)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var en)) continue;
            double len = sn.Position.DistanceTo(en.Position);
            if (len < tol.ShortElemMinMm)
                count++;
        }
        return count;
    }

    private static int CountUnresolvedUbolts(FeModel model)
    {
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        int count = 0;
        foreach (var rigid in model.Rigids)
        {
            if (rigid.Remark != "UBOLT") continue;
            bool hasBoundaryDependent = rigid.DependentNodeIds.Any(depId =>
                nodeById.TryGetValue(depId, out var n) && n.Tags.HasFlag(NodeTags.Boundary));
            if (hasBoundaryDependent)
                count++;
        }
        return count;
    }

    // ── ElementQuality ────────────────────────────────────────────────────────

    private static ElementQualityDto? ComputeElementQuality(FeModel model, Tolerances tol)
    {
        if (model.Elements.Count == 0) return null;

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var lengths  = new List<double>(model.Elements.Count);

        foreach (var e in model.Elements)
        {
            if (nodeById.TryGetValue(e.StartNodeId, out var sn) &&
                nodeById.TryGetValue(e.EndNodeId,   out var en))
                lengths.Add(sn.Position.DistanceTo(en.Position));
        }

        if (lengths.Count == 0) return null;
        lengths.Sort();

        double mean    = lengths.Sum() / lengths.Count;
        int shortCount = lengths.Count(l => l < tol.ShortElemMinMm);

        return new ElementQualityDto
        {
            LengthDistribution = new LengthDistributionDto
            {
                Min    = Math.Round(lengths[0],                3),
                Max    = Math.Round(lengths[^1],               3),
                Mean   = Math.Round(mean,                      3),
                Median = Math.Round(Percentile(lengths, 0.50), 3),
                P10    = Math.Round(Percentile(lengths, 0.10), 3),
                P90    = Math.Round(Percentile(lengths, 0.90), 3),
            },
            ShortElementCount       = shortCount,
            ShortElementThresholdMm = tol.ShortElemMinMm,
            AspectRatioOk           = true,
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        double idx = p * (sorted.Count - 1);
        int    lo  = (int)idx;
        int    hi  = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    // ── DiagnosticCounts ──────────────────────────────────────────────────────

    private static DiagnosticCountsDto ComputeDiagnosticCounts(IReadOnlyList<Diagnostic>? diagnostics)
    {
        if (diagnostics is null or { Count: 0 })
            return new DiagnosticCountsDto();

        int errors = 0, warnings = 0, infos = 0;
        var byCode = new Dictionary<string, int>();

        foreach (var d in diagnostics)
        {
            switch (d.Severity)
            {
                case DiagnosticSeverity.Error:   errors++;   break;
                case DiagnosticSeverity.Warning: warnings++; break;
                case DiagnosticSeverity.Info:    infos++;    break;
            }
            byCode[d.Code] = byCode.GetValueOrDefault(d.Code) + 1;
        }

        return new DiagnosticCountsDto
        {
            Error   = errors,
            Warning = warnings,
            Info    = infos,
            ByCode  = byCode,
        };
    }
}
