using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// AM 3D → 1D 추출 시 용접 두께·설계 오차로 발생하는 자유단(degree=1)을
/// 인접 부재의 단면 치수 + 여유(margin) 이내에서 연장하여 연결합니다.
/// HiTess ElementExtendToIntersectModifier에 대응합니다.
/// </summary>
public sealed class ExtendToIntersectStage : IPipelineStage
{
    public string Name => "ExtendToIntersect";

    public bool Execute(StageContext ctx)
    {
        var model = ctx.Model;
        model.SyncIdCounters();
        if (model.Elements.Count < 2) return true;

        var tol      = ctx.Options.Tolerances;
        double margin  = tol.ExtendExtraMarginMm;
        double coplTol = tol.ExtendCoplanarTolMm;
        double snapTol = tol.ExtendSnapLateralMm;
        int    maxIter = tol.ExtendMaxIterations;
        double cell    = tol.SpatialCellSizeMm;

        var alreadyMoved = new HashSet<int>(); // ping-pong 방지: 이미 이동한 노드는 재이동 금지
        int totalMoved = 0;
        for (int iter = 0; iter < maxIter; iter++)
        {
            int moved = ExtendOnce(ctx, margin, coplTol, snapTol, cell, alreadyMoved);
            totalMoved += moved;
            if (moved == 0) break;
            RemoveDegenerateElements(ctx);
        }

        if (totalMoved > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "EXTEND_TO_INTERSECT",
                $"{totalMoved} free end(s) extended to adjacent element(s).");

        return true;
    }

    // ── 한 회차 ─────────────────────────────────────────────────────────────

    private static int ExtendOnce(
        StageContext ctx,
        double margin, double coplTol, double snapTol, double cellSize,
        HashSet<int> alreadyMoved)
    {
        var model    = ctx.Model;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var secById  = model.Sections.ToDictionary(s => s.Id);
        var elemById = model.Elements.ToDictionary(e => e.Id);

        // Degree map
        var degree = new Dictionary<int, int>(nodeById.Count);
        foreach (var e in model.Elements)
        {
            degree[e.StartNodeId] = degree.GetValueOrDefault(e.StartNodeId) + 1;
            degree[e.EndNodeId]   = degree.GetValueOrDefault(e.EndNodeId)   + 1;
        }

        // 자유단 후보: Pipe는 연장 대상에서 제외
        var freeEndElems = new Dictionary<int, BeamElement>();
        foreach (var e in model.Elements)
        {
            if (e.Category == EntityCategory.Pipe) continue;
            if (degree.GetValueOrDefault(e.StartNodeId) == 1)
                freeEndElems.TryAdd(e.StartNodeId, e);
            if (degree.GetValueOrDefault(e.EndNodeId) == 1)
                freeEndElems.TryAdd(e.EndNodeId, e);
        }
        if (freeEndElems.Count == 0) return 0;

        var elemHash = new ElementSpatialHash(model.Elements, nodeById, cellSize, inflate: 0);
        var moves    = new Dictionary<int, Point3>();

        foreach (var (freeNodeId, elemA) in freeEndElems)
        {
            if (alreadyMoved.Contains(freeNodeId)) continue;
            if (!nodeById.TryGetValue(freeNodeId, out var pFreeNode)) continue;
            var pFree = pFreeNode.Position;

            // ElementA의 축 방향 (자유단 → 외부 방향)
            int otherNodeId = elemA.StartNodeId == freeNodeId
                ? elemA.EndNodeId : elemA.StartNodeId;
            if (!nodeById.TryGetValue(otherNodeId, out var otherNode)) continue;

            var span = pFree - otherNode.Position;
            if (span.LengthSquared < 1e-18) continue;
            var dirA = span.Normalize();

            // 검색 반경 = 단면 최대 치수 + margin
            double sectionDim = secById.TryGetValue(elemA.PropertyId, out var sec)
                ? sec.MaxCrossSectionDim() : 0.0;
            double searchRadius = sectionDim + margin;

            var boxMin = new Point3(pFree.X - searchRadius, pFree.Y - searchRadius, pFree.Z - searchRadius);
            var boxMax = new Point3(pFree.X + searchRadius, pFree.Y + searchRadius, pFree.Z + searchRadius);

            double bestS   = double.MaxValue;
            double bestLat = double.MaxValue;
            Point3 bestPRay = pFree;
            Point3 bestPSeg = pFree;
            int    bestBId  = -1;

            foreach (int eid in elemHash.QueryBox(boxMin, boxMax))
            {
                if (eid == elemA.Id) continue;
                if (!elemById.TryGetValue(eid, out var elemB)) continue;
                if (!nodeById.TryGetValue(elemB.StartNodeId, out var segA)) continue;
                if (!nodeById.TryGetValue(elemB.EndNodeId,   out var segB)) continue;

                var (hit, s, _, lat, pRay, pSeg) =
                    RaySegmentIntersection.TryClosest(
                        pFree, dirA, segA.Position, segB.Position, coplTol);

                if (!hit) continue;
                if (s < 1e-6 || s > searchRadius) continue;

                if (s < bestS - 1e-6 || (Math.Abs(s - bestS) < 1e-6 && lat < bestLat))
                {
                    bestS   = s;
                    bestLat = lat;
                    bestPRay = pRay;
                    bestPSeg = pSeg;
                    bestBId  = eid;
                }
            }

            if (bestBId < 0) continue;

            // pSeg(lateral ≤ snapTol) 또는 pRay(lateral ≤ coplTol)
            var newPos = bestLat <= snapTol ? bestPSeg : bestPRay;

            if (HasObstacle(nodeById, elemHash, elemById, elemA.Id, bestBId, pFree, newPos))
                continue;

            moves[freeNodeId] = newPos;
        }

        if (moves.Count == 0) return 0;

        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var node = model.Nodes[i];
            if (moves.TryGetValue(node.Id, out var newPos))
            {
                model.Nodes[i] = new Node(node.Id, newPos, node.Tags);
                alreadyMoved.Add(node.Id);
                ctx.AddTrace(TraceAction.NodeMoved, "ExtendToIntersect",
                    nodeId: node.Id,
                    note: "free end extended to adjacent element");
            }
        }

        return moves.Count;
    }

    // ── Obstacle 검사 ────────────────────────────────────────────────────────

    private static bool HasObstacle(
        Dictionary<int, Node>        nodeById,
        ElementSpatialHash           elemHash,
        Dictionary<int, BeamElement> elemById,
        int elemAId, int elemBId,
        Point3 pFrom, Point3 pTo)
    {
        const double obsTol = 1.0;

        var boxMin = new Point3(
            Math.Min(pFrom.X, pTo.X) - obsTol,
            Math.Min(pFrom.Y, pTo.Y) - obsTol,
            Math.Min(pFrom.Z, pTo.Z) - obsTol);
        var boxMax = new Point3(
            Math.Max(pFrom.X, pTo.X) + obsTol,
            Math.Max(pFrom.Y, pTo.Y) + obsTol,
            Math.Max(pFrom.Z, pTo.Z) + obsTol);

        foreach (int eid in elemHash.QueryBox(boxMin, boxMax))
        {
            if (eid == elemAId || eid == elemBId) continue;
            if (!elemById.TryGetValue(eid, out var e)) continue;
            if (!nodeById.TryGetValue(e.StartNodeId, out var nC)) continue;
            if (!nodeById.TryGetValue(e.EndNodeId,   out var nD)) continue;

            var (_, _, sc, _, dist) = ProjectionUtils.SegmentToSegmentClosestPoints(
                pFrom, pTo, nC.Position, nD.Position);

            // 경로 내부(endpoint 제외)에서 교차하는 경우만 장애물로 판정
            if (dist < obsTol && sc > 0.1 && sc < 0.9)
                return true;
        }
        return false;
    }

    // ── Degenerate 요소 정리 ─────────────────────────────────────────────────

    private static void RemoveDegenerateElements(StageContext ctx)
    {
        var model    = ctx.Model;
        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var toRemove = new List<int>();

        foreach (var e in model.Elements)
        {
            if (e.StartNodeId == e.EndNodeId) { toRemove.Add(e.Id); continue; }
            if (!nodeById.TryGetValue(e.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(e.EndNodeId,   out var nB)) continue;
            if (nA.Position.DistanceTo(nB.Position) < 1e-9)
                toRemove.Add(e.Id);
        }

        foreach (int id in toRemove)
        {
            model.Elements.RemoveAll(e => e.Id == id);
            ctx.AddTrace(TraceAction.ElementRemoved, "ExtendToIntersect",
                elementId: id, note: "degenerate after extend");
        }
    }
}
