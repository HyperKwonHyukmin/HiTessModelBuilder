using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Spatial;

namespace Cmb.Pipeline.Stages;

/// <summary>
/// 이미 모델에 존재하는 노드가 어떤 요소의 선분 위에 놓여 있을 경우 그 요소를 분할합니다.
/// GroupConnectStage / UboltRbeStage 직후에 실행하여,
/// 두 스테이지가 구조 부재 위에 생성한 snap 노드와 해당 부재 사이의 연결을 확립합니다.
/// </summary>
public sealed class SplitByExistingNodesStage : IPipelineStage
{
    public string Name => "SplitByExistingNodes";

    private const double ParamTol      = 1e-9;  // u=0/1 근처 끝점 제외
    private const double MergeTolAlong = 0.05;  // 선분 방향 근접 분할점 통합 (mm)
    private const double MinSegLen     = 1e-6;  // 유효 세그먼트 최소 길이

    public bool Execute(StageContext ctx)
    {
        var model    = ctx.Model;
        model.SyncIdCounters();
        double tol   = ctx.Options.Tolerances.NodeOnSegmentTolMm;
        double cell  = ctx.Options.Tolerances.SpatialCellSizeMm;

        var nodeById = model.Nodes.ToDictionary(n => n.Id);
        var elemById = model.Elements.ToDictionary(e => e.Id);

        // ElementSpatialHash: 요소 AABB 기반 공간 인덱스 (tol 만큼 팽창)
        var elemHash = new ElementSpatialHash(model.Elements, nodeById, cell, tol);

        // 분할 후보: elementId → [(nodeId, u, s)]
        var splitMap = new Dictionary<int, List<(int nodeId, double u, double s)>>();

        foreach (var node in model.Nodes)
        {
            var pos    = node.Position;
            var boxMin = new Point3(pos.X - tol, pos.Y - tol, pos.Z - tol);
            var boxMax = new Point3(pos.X + tol, pos.Y + tol, pos.Z + tol);

            foreach (int eid in elemHash.QueryBox(boxMin, boxMax))
            {
                if (!elemById.TryGetValue(eid, out var elem)) continue;

                // 이 노드가 이미 해당 요소의 끝점이면 무시
                if (elem.StartNodeId == node.Id || elem.EndNodeId == node.Id) continue;

                if (!nodeById.TryGetValue(elem.StartNodeId, out var nA)) continue;
                if (!nodeById.TryGetValue(elem.EndNodeId,   out var nB)) continue;

                double segLen = nA.Position.DistanceTo(nB.Position);
                if (segLen < MinSegLen) continue;

                var (_, t, dist) = ProjectionUtils.ClosestPointOnSegment(pos, nA.Position, nB.Position);

                if (dist > tol)                       continue; // 선분과 너무 멈
                if (t <= ParamTol || t >= 1.0 - ParamTol) continue; // 끝점 근방 제외

                if (!splitMap.TryGetValue(eid, out var list)) { list = []; splitMap[eid] = list; }
                list.Add((node.Id, t, t * segLen));
            }
        }

        if (splitMap.Count == 0) return true;

        // 요소별로 u 기준 정렬 후 근접 분할점 통합
        foreach (var list in splitMap.Values)
        {
            list.Sort((a, b) => a.s.CompareTo(b.s));
            MergeClose(list, MergeTolAlong);
        }

        int splitCount = ApplySplits(ctx, splitMap, nodeById);

        if (splitCount > 0)
            ctx.AddDiagnostic(DiagnosticSeverity.Info, "SPLIT_BY_EXISTING_NODES",
                $"{splitCount} element(s) split at existing intermediate node(s).");

        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ApplySplits(
        StageContext ctx,
        Dictionary<int, List<(int nodeId, double u, double s)>> splitMap,
        Dictionary<int, Node> nodeById)
    {
        var model      = ctx.Model;
        int splitCount = 0;

        foreach (var (eid, hits) in splitMap)
        {
            var elem = model.Elements.Find(e => e.Id == eid);
            if (elem is null) continue;

            // 체인 구성: StartNode → hit nodes (u 순) → EndNode
            var seen  = new HashSet<int> { elem.StartNodeId };
            var chain = new List<int>    { elem.StartNodeId };

            foreach (var (nid, _, _) in hits)
            {
                if (nid == elem.StartNodeId || nid == elem.EndNodeId) continue;
                if (seen.Add(nid)) chain.Add(nid);
            }
            chain.Add(elem.EndNodeId);

            // 유효 세그먼트 목록 생성
            var segs = new List<(int n1, int n2)>();
            for (int i = 0; i < chain.Count - 1; i++)
            {
                int ca = chain[i], cb = chain[i + 1];
                if (ca == cb) continue;
                if (!nodeById.TryGetValue(ca, out var pa)) continue;
                if (!nodeById.TryGetValue(cb, out var pb)) continue;
                if (pa.Position.DistanceTo(pb.Position) < MinSegLen) continue;
                segs.Add((ca, cb));
            }

            if (segs.Count <= 1) continue; // 실제 분할 없음

            model.Elements.RemoveAll(e => e.Id == eid);

            // 첫 번째 세그먼트는 원본 ID 재사용
            model.Elements.Add(new BeamElement(eid, segs[0].n1, segs[0].n2,
                elem.PropertyId, elem.Category, elem.Orientation,
                elem.SourceName, elem.ParentElementId));
            ctx.AddTrace(TraceAction.ElementSplit, "SplitByExistingNodes",
                elementId: eid, note: $"split into {segs.Count} at existing node(s)");

            for (int i = 1; i < segs.Count; i++)
            {
                int childId = model.AllocElemId();
                model.Elements.Add(new BeamElement(childId, segs[i].n1, segs[i].n2,
                    elem.PropertyId, elem.Category, elem.Orientation,
                    elem.SourceName, eid));
                ctx.AddTrace(TraceAction.ElementSplit, "SplitByExistingNodes",
                    elementId: childId, relatedElementId: eid,
                    note: $"split into {segs.Count} at existing node(s)");
            }

            splitCount++;
        }

        return splitCount;
    }

    /// <summary>
    /// 선분 방향 거리(s)가 mergeTolAlong 이내인 분할점들을 하나로 줄입니다 (in-place).
    /// </summary>
    private static void MergeClose(List<(int nodeId, double u, double s)> hits, double mergeTolAlong)
    {
        if (hits.Count <= 1) return;
        int write = 0;
        for (int i = 1; i < hits.Count; i++)
        {
            if (Math.Abs(hits[i].s - hits[write].s) > mergeTolAlong)
                hits[++write] = hits[i];
        }
        if (write + 1 < hits.Count)
            hits.RemoveRange(write + 1, hits.Count - write - 1);
    }

}
