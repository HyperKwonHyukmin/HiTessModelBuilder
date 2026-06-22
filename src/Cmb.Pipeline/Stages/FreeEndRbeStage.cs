using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// GroupConnect/Extend/UboltRbe 이후 남은 자유단(degree=1)을 각각 가장 가까운 요소에 수선의 발로
/// RBE2 브릿지 연결. HiTess ElementRbeConnectionModifier 이식.
/// U-bolt 영역(pipe 단면 + axial 여유)은 exclusion zone으로 제외하여 잘못된 자유단→U-bolt 연결을 방지.
///
/// unresolved 자유단은 per-node FREE_END_UNRESOLVED warning(가장 가까운 후보·거리·카테고리·거부사유)
/// 와 종합 FREE_END_UNRESOLVED_SUMMARY info(거리 bucket / 카테고리 / 사유 분포)로 보고.
/// </summary>
public sealed class FreeEndRbeStage : IPipelineStage
{
    public string Name => "FreeEndRbe";

    public bool Execute(StageContext ctx)
    {
        var model  = ctx.Model;
        var tol    = ctx.Options.Tolerances;
        model.SyncIdCounters();

        if (model.Elements.Count == 0) return true;

        var nodeById    = model.Nodes.ToDictionary(n => n.Id);
        var elemById    = model.Elements.ToDictionary(e => e.Id);
        var sectionById = model.Sections.ToDictionary(s => s.Id);
        var nodeHash    = new NodeSpatialHash(model.Nodes, tol.SpatialCellSizeMm);

        // 1. 전역 node degree (Elements + Rigids)
        var degree    = BuildNodeDegree(model);
        var elementNodes = BuildElementNodeSet(model);
        var freeNodes = degree
            .Where(kv => kv.Value == 1 && elementNodes.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToHashSet();
        if (freeNodes.Count == 0) return true;

        // 2. SpatialHash (inflate = ExtraMargin + FreeEndRbeMaxSearchMm)
        double hashInflate = tol.FreeEndRbeExtraMarginMm + tol.FreeEndRbeMaxSearchMm;
        var spatial = new ElementSpatialHash(model.Elements, nodeById,
            tol.SpatialCellSizeMm, hashInflate);

        // 3. 공선 검사용 free-node → 소속 부재 방향/카테고리
        var (freeDir, freeCategory) = BuildFreeNodeContext(model, freeNodes, nodeById);

        // 4. 단면 치수 캐시 (sectionById에서 즉시 조회)
        var dimCache = new Dictionary<int, double>();

        // 5. U-bolt exclusion zones — projection이 zone 내부면 후보에서 제외
        var exclusionZones = UboltExclusionZones.Build(model, nodeById, sectionById,
            tol.BoxUboltAxialSafetyMm);

        // 6. 자유단 처리 — 후보 평가 + closest tracking
        var pending      = new List<(int FreeNodeId, Point3 ProjPoint, int TargetEid)>();
        int onTargetDeferred = 0;
        var unresolvedDetails = new List<UnresolvedDetail>();
        int excludedHits = 0;
        var elementEdges = BuildElementEdges(model);

        foreach (int fnid in freeNodes)
        {
            if (!nodeById.TryGetValue(fnid, out var freeNode)) continue;
            var pFree = freeNode.Position;

            freeDir.TryGetValue(fnid, out var fdir);
            double freeDirLen = fdir.Length;

            double bestDist     = double.MaxValue;
            Point3 bestProj     = default;
            int    bestTargetId = -1;
            double bestT        = 0.0;

            // 가장 가까운 거부된 후보 추적 (unresolved 시 진단용)
            double closestDist     = double.MaxValue;
            int    closestEid      = -1;
            string closestCategory = "?";
            string closestReason   = "no-candidate";

            var boxMin = new Point3(pFree.X - hashInflate, pFree.Y - hashInflate, pFree.Z - hashInflate);
            var boxMax = new Point3(pFree.X + hashInflate, pFree.Y + hashInflate, pFree.Z + hashInflate);

            foreach (int eid in spatial.QueryBox(boxMin, boxMax))
            {
                if (!elemById.TryGetValue(eid, out var elem)) continue;
                if (elem.StartNodeId == fnid || elem.EndNodeId == fnid) continue;

                if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
                if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

                var (proj, t, dist) = ProjectionUtils.ClosestPointOnSegment(pFree, nA.Position, nB.Position);
                double tRaw = ProjectionUtils.ProjectParamOnInfiniteLine(pFree, nA.Position, nB.Position);

                if (!dimCache.TryGetValue(elem.PropertyId, out double searchDim))
                {
                    searchDim = sectionById.TryGetValue(elem.PropertyId, out var sec)
                        ? sec.MaxCrossSectionDim()
                        : 0.0;
                    dimCache[elem.PropertyId] = searchDim;
                }

                const double kInteriorEps = 1e-4;
                bool isInterior = tRaw > kInteriorEps && tRaw < 1.0 - kInteriorEps;
                double activeMargin = isInterior ? tol.FreeEndRbeExtraMarginMm
                                                 : tol.FreeEndRbeEndpointMarginMm;
                double allowed = searchDim + activeMargin;
                bool isOutsideSegment = tRaw < -kInteriorEps || tRaw > 1.0 + kInteriorEps;
                var targetDir = nB.Position - nA.Position;
                double targetDirLen = targetDir.Length;
                double lateralToTargetLine = targetDirLen > 1e-9
                    ? pFree.DistanceTo(nA.Position + targetDir * tRaw)
                    : double.MaxValue;
                bool isNearEndpointExtension =
                    ((tRaw < -kInteriorEps && tRaw >= -0.1) ||
                     (tRaw > 1.0 + kInteriorEps && tRaw <= 1.1))
                    && dist <= tol.FreeEndRbeEndpointMarginMm * 4;
                bool isCollinearEndpointGap = isOutsideSegment
                                              && dist <= allowed
                                              && lateralToTargetLine <= tol.NodeMergeMm;
                bool isEndpointGap = isNearEndpointExtension || isCollinearEndpointGap;

                bool zoneRejected = exclusionZones.Count > 0 &&
                                    UboltExclusionZones.IsInside(proj, exclusionZones);
                if (zoneRejected) excludedHits++;

                bool collinearRejected = false;
                if (!zoneRejected && freeDirLen > 1e-9 && isInterior)
                {
                    if (targetDirLen > 1e-9)
                    {
                        double cosAngle = Math.Abs(Vector3.Dot(fdir, targetDir) / (freeDirLen * targetDirLen));
                        if (cosAngle > 0.985) collinearRejected = true;
                    }
                }

                bool inSegmentRange = tRaw >= -1e-4 && tRaw <= 1.0001;
                bool outOfRange    = !(inSegmentRange || isEndpointGap) || dist > allowed;
                bool redundantEndpoint = !zoneRejected && !outOfRange &&
                                         IsProjectionNearEndpointAlreadyConnected(
                                             fnid, proj, elem, nA, nB, elementEdges,
                                             tol.FreeEndRbeEndpointMarginMm);

                string reason;
                if (zoneRejected)            reason = "excluded";
                else if (collinearRejected)  reason = "collinear";
                else if (redundantEndpoint)  reason = "redundant-endpoint";
                else if (outOfRange)         reason = "out-of-range";
                else                         reason = "accepted";

                // closest 추적 (수락/거부 무관하게 가장 가까운 후보)
                if (dist < closestDist)
                {
                    closestDist     = dist;
                    closestEid      = eid;
                    closestCategory = elem.Category.ToString();
                    closestReason   = reason;
                }

                if (reason == "accepted" && dist < bestDist)
                {
                    bestDist     = dist;
                    bestProj     = proj;
                    bestTargetId = eid;
                    bestT        = t;
                }
            }

            if (bestTargetId == -1)
            {
                string freeCat = freeCategory.GetValueOrDefault(fnid, "?");
                unresolvedDetails.Add(new UnresolvedDetail(fnid, freeCat,
                    closestEid, closestDist, closestCategory, closestReason));
                continue;
            }

            if (bestDist <= 1e-4 && bestT > 1e-4 && bestT < 1.0 - 1e-4)
            {
                ctx.AddTrace(TraceAction.ElementPreserved, Name,
                    nodeId: fnid, relatedElementId: bestTargetId,
                    note: "free node already lies on target element; deferred to SplitByExistingNodes");
                onTargetDeferred++;
                continue;
            }

            pending.Add((fnid, bestProj, bestTargetId));
        }

        // 7. 예약된 RBE2 일괄 추가 — element/RBE 중복 노드쌍은 skip
        // - element edge: CBEAM이 이미 강체 연결 제공
        // - rbe edge: 두 free-end가 서로를 향해 projection되어 양방향 cycle을 만드는 케이스 차단 (Nastran FATAL 6202 MCE1)
        var rbeEdges = new HashSet<(int, int)>();
        foreach (var r in model.Rigids)
            foreach (int dep in r.DependentNodeIds)
                rbeEdges.Add(EdgeKey(r.IndependentNodeId, dep));

        int created = 0;
        int selfLoopSkipped = 0;
        int redundantEdgeSkipped = 0;
        int rbeCycleSkipped = 0;
        foreach (var (fnid, projPt, targetEid) in pending)
        {
            int projNodeId = FindOrCreateNode(ctx, model, nodeById, nodeHash, projPt, tol.NodeMergeMm, fnid, targetEid);
            if (projNodeId == fnid)
            {
                selfLoopSkipped++;
                continue;
            }
            var edgeKey = EdgeKey(projNodeId, fnid);
            if (elementEdges.Contains(edgeKey))
            {
                redundantEdgeSkipped++;
                ctx.AddTrace(TraceAction.ElementPreserved, Name,
                    nodeId: fnid, relatedElementId: targetEid,
                    note: $"free N{fnid} ↔ N{projNodeId} already connected by element — RBE skipped");
                continue;
            }
            if (rbeEdges.Contains(edgeKey))
            {
                rbeCycleSkipped++;
                ctx.AddTrace(TraceAction.ElementPreserved, Name,
                    nodeId: fnid, relatedElementId: targetEid,
                    note: $"free N{fnid} ↔ N{projNodeId} already linked by RBE — bidirectional cycle skipped");
                continue;
            }
            int rbeId = model.AllocElemId();
            model.Rigids.Add(new RigidElement(rbeId, projNodeId, [fnid], "FREE_END_RBE"));
            rbeEdges.Add(edgeKey);
            double dist = nodeById[projNodeId].Position.DistanceTo(nodeById[fnid].Position);
            ctx.AddTrace(TraceAction.ElementCreated, Name, elementId: rbeId,
                note: $"free N{fnid} → E{targetEid} @ N{projNodeId} ({dist:F1}mm)");
            created++;
        }

        // 8. 진단 발행
        EmitUnresolvedDiagnostics(ctx, unresolvedDetails);

        if (created > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_RBE_CREATED",
                $"{created} free-end node(s) linked to nearest element via RBE2 " +
                $"(maxSearch {tol.FreeEndRbeMaxSearchMm:F0}mm).");
        if (excludedHits > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_UBOLT_EXCLUDED",
                $"{excludedHits} candidate projection(s) skipped — inside U-bolt exclusion zone(s).");
        if (selfLoopSkipped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_SELFLOOP_SKIPPED",
                $"{selfLoopSkipped} free-end RBE skipped — projection coincided with free node (already on target).");
        if (redundantEdgeSkipped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_REDUNDANT_EDGE_SKIPPED",
                $"{redundantEdgeSkipped} free-end RBE skipped — node pair already connected by element.");
        if (rbeCycleSkipped > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_RBE_CYCLE_SKIPPED",
                $"{rbeCycleSkipped} free-end RBE skipped — bidirectional cycle would create singular RMM (Nastran FATAL 6202).");
        if (onTargetDeferred > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_ON_TARGET_DEFERRED",
                $"{onTargetDeferred} free-end node(s) already lie on target element — deferred to SplitByExistingNodes.");

        return true;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    private readonly record struct UnresolvedDetail(
        int FreeNodeId, string FreeCategory,
        int ClosestElemId, double ClosestDist, string ClosestCategory, string Reason);

    private static void EmitUnresolvedDiagnostics(StageContext ctx, List<UnresolvedDetail> details)
    {
        if (details.Count == 0) return;

        // per-node warning
        foreach (var d in details)
        {
            string near = d.ClosestElemId < 0
                ? "no candidate in search box"
                : $"nearest {d.ClosestCategory} E{d.ClosestElemId} at {d.ClosestDist:F1}mm — {d.Reason}";
            ctx.AddDiagnostic(DiagnosticSeverity.Warning, "FREE_END_UNRESOLVED",
                $"Free node N{d.FreeNodeId} ({d.FreeCategory}) has no RBE candidate; {near}.",
                nodeId: d.FreeNodeId);
        }

        // summary distribution
        int n = details.Count;
        int bLt50 = 0, b50_200 = 0, b200_500 = 0, b500_1000 = 0, bGt1000 = 0, bNoCand = 0;
        var byCategory = new Dictionary<string, int>();
        var byReason   = new Dictionary<string, int>();
        foreach (var d in details)
        {
            byCategory[d.FreeCategory] = byCategory.GetValueOrDefault(d.FreeCategory) + 1;
            byReason[d.Reason]         = byReason.GetValueOrDefault(d.Reason)         + 1;

            if (d.ClosestElemId < 0)        bNoCand++;
            else if (d.ClosestDist <  50)    bLt50++;
            else if (d.ClosestDist <  200)   b50_200++;
            else if (d.ClosestDist <  500)   b200_500++;
            else if (d.ClosestDist < 1000)   b500_1000++;
            else                              bGt1000++;
        }

        string distMsg  = $"distance: <50={bLt50}, 50-200={b50_200}, 200-500={b200_500}, " +
                          $"500-1000={b500_1000}, >1000={bGt1000}, no-candidate={bNoCand}";
        string catMsg   = string.Join(", ", byCategory.OrderByDescending(kv => kv.Value)
                                                       .Select(kv => $"{kv.Key}={kv.Value}"));
        string reasonMsg = string.Join(", ", byReason.OrderByDescending(kv => kv.Value)
                                                     .Select(kv => $"{kv.Key}={kv.Value}"));

        ctx.AddDiagnostic(DiagnosticSeverity.Info, "FREE_END_UNRESOLVED_SUMMARY",
            $"{n} free-end node(s) unresolved. {distMsg}. category: {catMsg}. reason: {reasonMsg}.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Dictionary<int, int> BuildNodeDegree(FeModel model)
    {
        var deg = new Dictionary<int, int>();
        foreach (var e in model.Elements)
        {
            deg[e.StartNodeId] = deg.GetValueOrDefault(e.StartNodeId) + 1;
            deg[e.EndNodeId]   = deg.GetValueOrDefault(e.EndNodeId)   + 1;
        }
        foreach (var r in model.Rigids)
        {
            deg[r.IndependentNodeId] = deg.GetValueOrDefault(r.IndependentNodeId) + 1;
            foreach (int did in r.DependentNodeIds)
                deg[did] = deg.GetValueOrDefault(did) + 1;
        }
        return deg;
    }

    private static HashSet<int> BuildElementNodeSet(FeModel model)
    {
        var result = new HashSet<int>();
        foreach (var e in model.Elements)
        {
            result.Add(e.StartNodeId);
            result.Add(e.EndNodeId);
        }
        return result;
    }

    private static HashSet<(int, int)> BuildElementEdges(FeModel model)
    {
        var edges = new HashSet<(int, int)>();
        foreach (var e in model.Elements)
            edges.Add(EdgeKey(e.StartNodeId, e.EndNodeId));
        return edges;
    }

    private static bool IsProjectionNearEndpointAlreadyConnected(
        int freeNodeId,
        Point3 projection,
        BeamElement target,
        Node targetStart,
        Node targetEnd,
        HashSet<(int, int)> elementEdges,
        double endpointTol)
    {
        if (projection.DistanceTo(targetStart.Position) <= endpointTol &&
            elementEdges.Contains(EdgeKey(freeNodeId, target.StartNodeId)))
            return true;

        if (projection.DistanceTo(targetEnd.Position) <= endpointTol &&
            elementEdges.Contains(EdgeKey(freeNodeId, target.EndNodeId)))
            return true;

        return false;
    }

    /// <summary>
    /// 자유단 노드 → 소속 부재 방향 + 소속 부재 카테고리(Pipe/Structure/Equipment) 매핑.
    /// 자유단은 정의상 1개 element에만 속하므로 첫 매칭이 곧 소속.
    /// </summary>
    private static (Dictionary<int, Vector3> dir, Dictionary<int, string> cat)
        BuildFreeNodeContext(FeModel model, HashSet<int> freeNodes, Dictionary<int, Node> nodeById)
    {
        var dirMap = new Dictionary<int, Vector3>();
        var catMap = new Dictionary<int, string>();
        foreach (var elem in model.Elements)
        {
            if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
            if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

            bool firstFree = freeNodes.Contains(elem.StartNodeId);
            bool lastFree  = freeNodes.Contains(elem.EndNodeId);
            if (!firstFree && !lastFree) continue;

            var dir = nB.Position - nA.Position;
            string cat = elem.Category.ToString();
            if (firstFree && !dirMap.ContainsKey(elem.StartNodeId))
            {
                dirMap[elem.StartNodeId] = dir;
                catMap[elem.StartNodeId] = cat;
            }
            if (lastFree && !dirMap.ContainsKey(elem.EndNodeId))
            {
                dirMap[elem.EndNodeId] = dir;
                catMap[elem.EndNodeId] = cat;
            }
        }
        return (dirMap, catMap);
    }

    private static (int, int) EdgeKey(int a, int b)
        => a < b ? (a, b) : (b, a);

    private static int FindOrCreateNode(
        StageContext ctx,
        FeModel model, Dictionary<int, Node> nodeById, NodeSpatialHash nodeHash,
        Point3 pos, double mergeTol, int? excludeNodeId = null,
        int? relatedElementId = null)
    {
        foreach (int candId in nodeHash.QueryNear(pos, mergeTol))
        {
            if (excludeNodeId.HasValue && candId == excludeNodeId.Value) continue;
            if (!nodeById.TryGetValue(candId, out var n)) continue;
            if (n.Position.DistanceTo(pos) <= mergeTol) return n.Id;
        }

        int newId = model.AllocNodeId();
        var newNode = new Node(newId, pos);
        model.Nodes.Add(newNode);
        nodeById[newId] = newNode;
        nodeHash.Add(newId, pos);
        ctx.AddTrace(TraceAction.NodeCreated, "FreeEndRbe",
            nodeId: newId, relatedElementId: relatedElementId,
            note: "free-end RBE projection node");
        return newId;
    }
}
