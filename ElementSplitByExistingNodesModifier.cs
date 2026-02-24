using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 기존에 존재하는 노드(Node)가 요소(Element)의 경로 위에 있을 경우,
  /// 해당 노드를 기준으로 요소를 분할(Split)하는 수정자(Modifier)입니다.
  /// </summary>
  public static class ElementSplitByExistingNodesModifier
  {
    // =================================================================
    // 내부 알고리즘 설정값 (캡슐화)
    // =================================================================
    private const double ParamTol = 1e-9;
    private const double MergeTolAlong = 0.05;
    private const double MinSegLenTol = 1e-6;
    private const double GridCellSize = 5.0;
    private const bool SnapNodeToLine = false;
    private const bool ReuseOriginalIdForFirst = true;

    /// <summary>
    /// 사용자 실행 옵션 정의 (파이프라인 연동용)
    /// </summary>
    public sealed record Options(
        double DistanceTol = 0.5,       // 점-선 거리 허용치 (이 거리 이내면 선 위의 점으로 간주)
        bool PipelineDebug = false,     // 파이프라인 단계별 요약 정보 출력 여부
        bool VerboseDebug = false,      // 개별 요소의 분할 과정 상세 출력 여부
        int MaxPrintElements = 10,      // 디버그 출력 시 표시할 최대 요소 개수
        int MaxPrintNodesPerElement = 5 // 요소당 표시할 최대 분할 노드 개수
    );

    public sealed record Result(
        int ElementsScanned,      // 검사한 총 요소 수
        int ElementsNeedSplit,    // 분할이 필요한 요소 수 (후보)
        int ElementsActuallySplit,// 실제로 분할 수행된 요소 수
        int ElementsRemoved,      // 삭제된 원본 요소 수
        int ElementsAdded         // 새로 생성된 요소 수
    );

    // =================================================================
    // Public Entry Point
    // =================================================================

    /// <summary>
    /// 수정자 실행 메인 메서드입니다.
    /// </summary>
    /// <summary>
    /// 요소(Element) 경로 위의 노드들을 탐색하여 요소를 분할(Split)합니다.
    /// </summary>
    public static Result Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine; // 외부에서 로거를 안 넘기면 기본 콘솔 출력 사용

      var nodes = context.Nodes;

      // 1) Spatial Hash 구축 (노드 검색 가속화)
      var grid = new SpatialHash(nodes, GridCellSize);

      // 2) 분할 후보 탐색 (log 파라미터 전달)
      var (scanned, candidates) = FindSplitCandidates(context, grid, opt, log);

      // 3) 분할 적용 (log 파라미터 전달)
      var (splitCount, removed, added) = ApplySplit(context, candidates, opt, log);

      // 4) PipelineDebug 출력 (Sanity Inspector 스타일)
      if (opt.PipelineDebug)
      {
        if (splitCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          Console.WriteLine($"[변경] 요소 자동 분할 : 대상 요소 {candidates.Count}개 중 {splitCount}개 분할됨 (기존 요소 {removed}개 삭제, 신규 요소 {added}개 생성)");
          Console.ResetColor();
        }
        else
        {
          Console.WriteLine($"[통과] 요소 자동 분할 : 분할이 필요한 요소가 발견되지 않았습니다.");
        }
      }

      return new Result(scanned, candidates.Count, splitCount, removed, added);
    }

    // =================================================================
    // Internal Logic
    // =================================================================

    private static (int scanned, Dictionary<int, List<int>> candidates) FindSplitCandidates(
        FeModelContext context, SpatialHash grid, Options opt, Action<string> log)
    {
      var nodes = context.Nodes;
      var elements = context.Elements;
      int scanned = 0;
      var candidates = new Dictionary<int, List<int>>();

      // 변경 중 컬렉션 오류 방지를 위해 ID 리스트 스냅샷 생성
      var elementIds = elements.Keys.ToList();

      foreach (var eid in elementIds)
      {
        if (!elements.Contains(eid)) continue;
        scanned++;

        var e = elements[eid];
        if (e.NodeIDs == null || e.NodeIDs.Count < 2) continue;

        int nA = e.NodeIDs.First();
        int nB = e.NodeIDs.Last();

        // 양 끝 노드가 유효한지 확인
        if (!nodes.Contains(nA) || !nodes.Contains(nB)) continue;

        var A = nodes.GetNodeCoordinates(nA);
        var B = nodes.GetNodeCoordinates(nB);

        // 요소 길이 검사 (0인 경우 스킵)
        double len = DistanceUtils.GetDistanceBetweenNodes(A, B);
        if (len <= 1e-9) continue;

        // [최적화] 해당 요소를 감싸는 BoundingBox 생성
        var bbox = BoundingBox.FromSegment(A, B, opt.DistanceTol);

        // Grid를 통해 주변 노드 후보 검색
        var candidateNodeIds = grid.Query(bbox);
        var hits = new List<NodeHit>();

        foreach (var nid in candidateNodeIds)
        {
          // 자기 자신의 양 끝점은 제외
          if (nid == nA || nid == nB) continue;

          var P = nodes.GetNodeCoordinates(nid);

          // P가 선분 AB 위에 있는지 투영(Projection)하여 확인
          var projResult = ProjectionUtils.ProjectPointToInfiniteLine(P, A, B);
          double u = projResult.T; // 매개변수 t (0.0 ~ 1.0)

          // 1. 범위 체크 (양 끝점 근처 제외)
          if (u <= 0.0 + ParamTol || u >= 1.0 - ParamTol) continue;

          // 2. 거리 체크 (직선과의 거리가 허용오차 이내인지)
          if (projResult.Distance > opt.DistanceTol) continue;

          double s = u * len; // 시작점으로부터의 실제 거리
          hits.Add(new NodeHit(nid, u, s, projResult.Distance));
        }

        if (hits.Count == 0) continue;

        // 시작점 기준 정렬 및 근접 노드 병합
        hits.Sort((x, y) => x.S.CompareTo(y.S));
        var merged = MergeCloseHits(hits, MergeTolAlong);

        // [옵션] 노드 스냅: 노드를 정확히 직선 위로 이동
        if (SnapNodeToLine)
        {
          foreach (var h in merged)
          {
            var P = nodes.GetNodeCoordinates(h.NodeId);
            var proj = ProjectionUtils.ProjectPointToInfiniteLine(P, A, B).ProjectedPoint;

            // 좌표 업데이트 (덮어쓰기)
            nodes.AddWithID(h.NodeId, proj.X, proj.Y, proj.Z);
          }
        }

        var internalNodeIds = merged.Select(h => h.NodeId).ToList();
        if (internalNodeIds.Count > 0)
          candidates[eid] = internalNodeIds;
      }

      return (scanned, candidates);
    }

    private static (int splitCount, int removed, int added) ApplySplit(
        FeModelContext context, Dictionary<int, List<int>> candidates, Options opt, Action<string> log)
    {
      var nodes = context.Nodes;
      var elements = context.Elements;
      int splitCount = 0, removed = 0, added = 0;

      var targetElementIds = candidates.Keys.ToList();

      foreach (var eid in targetElementIds)
      {
        if (!elements.Contains(eid)) continue;
        var e = elements[eid];

        // 유효성 재확인
        if (e.NodeIDs == null || e.NodeIDs.Count < 2) continue;

        int nA = e.NodeIDs.First();
        int nB = e.NodeIDs.Last();
        var A = nodes.GetNodeCoordinates(nA);
        var B = nodes.GetNodeCoordinates(nB);

        // 분할 점들을 매개변수 u 기준으로 정렬
        var internalNodeIds = candidates[eid];
        var ordered = internalNodeIds
            .Select(nid =>
            {
              var P = nodes.GetNodeCoordinates(nid);
              double u = ProjectionUtils.ProjectPointToScalar(P, A, B);
              return (nid, u);
            })
            .OrderBy(x => x.u)
            .Select(x => x.nid)
            .ToList();

        // 연결 체인 생성: [Start] -> [Mid1] -> [Mid2] -> ... -> [End]
        var chain = new List<int>(ordered.Count + 2);
        chain.Add(nA);
        chain.AddRange(ordered);
        chain.Add(nB);

        // 세그먼트(요소) 생성 준비
        var segs = new List<(int n1, int n2)>();
        for (int i = 0; i < chain.Count - 1; i++)
        {
          int n1 = chain[i];
          int n2 = chain[i + 1];
          if (n1 == n2) continue; // 동일 노드 방어

          var p1 = nodes.GetNodeCoordinates(n1);
          var p2 = nodes.GetNodeCoordinates(n2);

          // 너무 짧은 요소 생성 방지
          if (DistanceUtils.GetDistanceBetweenNodes(p1, p2) < MinSegLenTol) continue;

          segs.Add((n1, n2));
        }

        // 생성된 세그먼트가 없으면 원본 삭제만 수행
        if (segs.Count == 0)
        {
          elements.Remove(eid);
          removed++;
          continue;
        }

        // 속성 복사
        var extra = (e.ExtraData != null) ? e.ExtraData.ToDictionary(k => k.Key, v => v.Value) : null;

        if (ReuseOriginalIdForFirst)
        {
          // 첫 번째 조각은 기존 ID 재사용 (덮어쓰기)
          elements.AddWithID(eid, new List<int> { segs[0].n1, segs[0].n2 }, e.PropertyID, extra);

          // 나머지 조각은 신규 생성
          for (int i = 1; i < segs.Count; i++)
          {
            int newId = elements.AddNew(new List<int> { segs[i].n1, segs[i].n2 }, e.PropertyID, extra);
            added++;
            if (opt.VerboseDebug)
              log($"   -> [추가] E{newId} 생성 (노드: {segs[i].n1}-{segs[i].n2})");
          }
          if (opt.VerboseDebug)
            log($"[분할 완료] E{eid} 유지 및 분할됨. (총 {segs.Count}개 조각)");
        }
        else
        {
          // 기존 ID 삭제 후 모두 신규 생성
          elements.Remove(eid);
          removed++;
          for (int i = 0; i < segs.Count; i++)
          {
            int newId = elements.AddNew(new List<int> { segs[i].n1, segs[i].n2 }, e.PropertyID, extra);
            added++;
            if (opt.VerboseDebug)
              log($"   -> [신규] E{newId} 생성 (노드: {segs[i].n1}-{segs[i].n2})");
          }
          if (opt.VerboseDebug)
            log($"[분할 완료] 원본 E{eid} 삭제 후 {segs.Count}개로 재생성.");
        }
        splitCount++;
      }
      return (splitCount, removed, added);
    }

    // =================================================================
    // Helper Methods & Classes
    // =================================================================

    /// <summary>
    /// 선분 방향으로 매우 가까운 노드들은 하나로 병합 (가장 가까운 놈 선택)
    /// </summary>
    private static List<NodeHit> MergeCloseHits(List<NodeHit> hits, double mergeTolAlong)
    {
      if (hits.Count == 0) return hits;
      var merged = new List<NodeHit>();
      NodeHit cur = hits[0];
      merged.Add(cur);

      for (int i = 1; i < hits.Count; i++)
      {
        var h = hits[i];
        // 거리차이가 허용치 이내라면 병합
        if (Math.Abs(h.S - cur.S) <= mergeTolAlong)
        {
          // 직선에 더 가까운(Dist가 작은) 노드를 우선시
          if (h.Dist < cur.Dist)
          {
            cur = h;
            merged[merged.Count - 1] = cur;
          }
          continue;
        }
        cur = h;
        merged.Add(cur);
      }
      return merged;
    }

    private static void PrintCandidatesSummary(Dictionary<int, List<int>> candidates, Options opt, Action<string> log)
    {
      log($"[DryRun] 분할 대상 요소 수: {candidates.Count}");
      int shown = 0;
      foreach (var kv in candidates.OrderBy(k => k.Key))
      {
        if (shown >= opt.MaxPrintElements) { log($"[DryRun] ... (최대 {opt.MaxPrintElements}개까지만 표시)"); break; }
        var preview = kv.Value.Take(opt.MaxPrintNodesPerElement);
        log($" - E{kv.Key}: 분할 예정 노드[{kv.Value.Count}] -> {string.Join(",", preview)}");
        shown++;
      }
    }

    /// <summary>
    /// 요소(Element) 경로를 검사할 때, 선분 위 또는 근처에서 발견된 노드의 투영 정보를 담는 구조체입니다.
    /// </summary>
    private readonly struct NodeHit
    {
      /// <summary>발견된 노드의 고유 ID</summary>
      public readonly int NodeId;

      /// <summary>선분 시작점(0.0)부터 끝점(1.0) 사이의 투영 위치 비율 (u 값)</summary>
      public readonly double U;

      /// <summary>선분 시작점으로부터의 실제 물리적 거리</summary>
      public readonly double S;

      /// <summary>직선(요소 경로)과 노드 사이의 최단 수직 거리</summary>
      public readonly double Dist;

      public NodeHit(int nodeId, double u, double s, double dist)
      {
        NodeId = nodeId;
        U = u;
        S = s;
        Dist = dist;
      }
    }
  }  
}
