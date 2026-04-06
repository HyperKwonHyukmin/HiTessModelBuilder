using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: 일반 UBOLT 지능형 스냅]
  /// 배관의 직경(Radius)을 고려한 동적 탐색 반경을 사용하며,
  /// 실패 시 탐색 반경을 배관 지름(Radius * 2)까지 확장하여 가장 치수(Dim)가 큰 뼈대를 찾아냅니다.
  /// </summary>
  public static class UboltSnapToStructureModifier
  {
    public sealed record Options(
        double MaxSearchRadius = 300.0, // 기본 최대 탐색 반경
        double ExtraMargin = 100.0,     // 배관 반지름에 추가로 더할 여유 스냅 마진
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int snappedCount = 0;

      // 1. "UBOLT" 강체 필터링 (BOX 타입 제외)
      var rawUbolts = context.Rigids.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Type", out var type) && type == "UBOLT" &&
          !(kv.Value.ExtraData.TryGetValue("Remark", out var remark) && remark == "BOX")
      ).ToList();

      if (rawUbolts.Count == 0) return 0;

      var ubolts = new List<KeyValuePair<int, RigidInfo>>();
      var processedNodes = new HashSet<int>();

      foreach (var ubolt in rawUbolts)
      {
        int indepNode = ubolt.Value.IndependentNodeID;
        if (processedNodes.Contains(indepNode))
        {
          string rawName = ubolt.Value.ExtraData?.GetValueOrDefault("Name") ?? "Unknown";
          if (opt.VerboseDebug)
            log($"   -> [중복 UBOLT 삭제] 동일한 위치에 겹쳐 생성된 중복 UBOLT '{rawName}' 삭제됨.");

          context.Rigids.Remove(ubolt.Key);
          continue;
        }
        processedNodes.Add(indepNode);
        ubolts.Add(ubolt);
      }

      if (opt.VerboseDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] UboltSnapToStructureModifier (동적 탐색 및 2차 확장 탐색 적용)");
        log($" -> 탐색 대상 일반 UBOLT 개수: {ubolts.Count}개");
        log($"==================================================\n");
      }

      var struElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru").ToList();

      var pipeElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null &&
          (
              (kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Pipe") ||
              (kv.Value.ExtraData.TryGetValue("Category", out var cat) && cat == "Pipe")
          )
      ).ToList();

      // ★ [Phase 4-4] ElementSpatialHash로 U-Bolt별 구조물 전수 탐색 제거
      // inflate = ExtraMargin으로 요소 bbox를 살짝 확장, cellSize는 넉넉하게 설정
      var struEidSet = new HashSet<int>(struElements.Select(kv => kv.Key));
      double hashInflate = opt.ExtraMargin;
      var spatialHash = new ElementSpatialHash(context.Elements, context.Nodes, (opt.MaxSearchRadius + opt.ExtraMargin) * 2, hashInflate);

      foreach (var ubolt in ubolts)
      {
        int rigidId = ubolt.Key;
        var info = ubolt.Value;
        int oldIndepNodeId = info.IndependentNodeID;
        var pIndep = context.Nodes[oldIndepNodeId];

        var ownerPipeElem = pipeElements.FirstOrDefault(kv => kv.Value.NodeIDs.Contains(oldIndepNodeId)).Value;

        // 배관 반지름
        double pipeRadius = ownerPipeElem != null ? ownerPipeElem.GetReferencedPropertyDim(context.Properties) : 0.0;

        // 1차 기본 탐색 반경 (반지름 기준)
        double dynamicSearchRadius = Math.Max(opt.MaxSearchRadius, pipeRadius + opt.ExtraMargin);

        // 2차 확장 탐색 반경 (지름 기준 = 반지름 * 2)
        double expandedSearchRadius = Math.Max(opt.MaxSearchRadius, (pipeRadius * 2.0) + opt.ExtraMargin);

        double bestDist = double.MaxValue;
        Point3D bestHitStruPoint = default;
        int bestStruTargetEid = -1;

        // =========================================================================
        // [1차 탐색] 기본 반경 내에서 가장 가까운 구조물 탐색 (SpatialHash 활용)
        // =========================================================================
        var queryBB1 = BoundingBox.FromSegment(pIndep, pIndep, dynamicSearchRadius);
        foreach (var targetEid in spatialHash.QueryBBox(queryBB1))
        {
          if (!struEidSet.Contains(targetEid)) continue;

          var elem = context.Elements[targetEid];
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          double dist = ProjectionUtils.DistancePointToSegment(pIndep, pA, pB, out Point3D projPoint);

          if (dist <= dynamicSearchRadius && dist < bestDist)
          {
            bestDist = dist;
            bestHitStruPoint = projPoint;
            bestStruTargetEid = targetEid;
          }
        }

        // =========================================================================
        // [2차 탐색] 1차 탐색 실패 시, 지름(반지름x2)까지 범위를 넓혀 튼튼한 부재 우선 탐색 (SpatialHash 활용)
        // =========================================================================
        bool usedExpandedSearch = false;
        if (bestStruTargetEid == -1)
        {
          var expandedCandidates = new List<(int Eid, double Dist, Point3D ProjPoint, double MaxDim)>();

          var queryBB2 = BoundingBox.FromSegment(pIndep, pIndep, expandedSearchRadius);
          foreach (var targetEid in spatialHash.QueryBBox(queryBB2))
          {
            if (!struEidSet.Contains(targetEid)) continue;

            var elem = context.Elements[targetEid];
            if (elem.NodeIDs.Count < 2) continue;

            var pA = context.Nodes[elem.NodeIDs.First()];
            var pB = context.Nodes[elem.NodeIDs.Last()];

            double dist = ProjectionUtils.DistancePointToSegment(pIndep, pA, pB, out Point3D projPoint);

            if (dist <= expandedSearchRadius)
            {
              // ElementExtensions에 있는 기능 활용: 구조물의 단면 치수(가장 큰 값) 추출
              double maxDim = elem.GetReferencedPropertyDim(context.Properties);
              expandedCandidates.Add((targetEid, dist, projPoint, maxDim));
            }
          }

          if (expandedCandidates.Count > 0)
          {
            // [사용자 요구조건] Dim이 가장 큰 부재(MaxDim)를 최우선으로, 그 다음으로 거리가 가까운 것(Dist) 선택
            var selected = expandedCandidates
                .OrderByDescending(c => c.MaxDim)
                .ThenBy(c => c.Dist)
                .First();

            bestDist = selected.Dist;
            bestHitStruPoint = selected.ProjPoint;
            bestStruTargetEid = selected.Eid;
            usedExpandedSearch = true;
          }
        }

        // =========================================================================
        // 실제 스냅(Snap) 적용 및 Rigid Node 매핑
        // =========================================================================
        if (bestStruTargetEid != -1)
        {
          int newDepNodeId = context.Nodes.AddOrGet(bestHitStruPoint.X, bestHitStruPoint.Y, bestHitStruPoint.Z);

          Point3D bestProjPipePoint = pIndep;
          if (ownerPipeElem != null && ownerPipeElem.NodeIDs.Count >= 2)
          {
            var pPipeA = context.Nodes[ownerPipeElem.NodeIDs.First()];
            var pPipeB = context.Nodes[ownerPipeElem.NodeIDs.Last()];
            ProjectionUtils.DistancePointToSegment(bestHitStruPoint, pPipeA, pPipeB, out bestProjPipePoint);
          }
          int newIndepNodeId = context.Nodes.AddOrGet(bestProjPipePoint.X, bestProjPipePoint.Y, bestProjPipePoint.Z);

          if (info.DependentNodeIDs.Count > 0)
          {
            context.Rigids.RemapNodes(rigidId, new Dictionary<int, int> {
                            { oldIndepNodeId, newIndepNodeId },
                            { info.DependentNodeIDs[0], newDepNodeId }
                        });
          }
          else
          {
            context.Rigids.RemapNodes(rigidId, new Dictionary<int, int> { { oldIndepNodeId, newIndepNodeId } }, dropIfEmpty: false);
            context.Rigids.AppendDependentNodes(rigidId, new[] { newDepNodeId });
          }

          snappedCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            if (usedExpandedSearch)
            {
              log($"[연결 완료(확장)] RBE {rigidId} -> E{bestStruTargetEid} (확장 반경 내 가장 튼튼한 부재에 연결)");
            }
            else
            {
              log($"[연결 완료] RBE {rigidId} -> 구조물 E{bestStruTargetEid} (이격: {bestDist:F1}mm / 배관 R: {pipeRadius:F1}mm)");
            }
            Console.ResetColor();
          }
        }
        else
        {
          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            log($"[누락] RBE {rigidId} (N{oldIndepNodeId}) -> 최대 확장 반경 {expandedSearchRadius:F1}mm 내에도 지지 구조물 없음.");
            Console.ResetColor();
          }
        }
      }

      if (opt.PipelineDebug && snappedCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        log($"[변경] 일반 U-Bolt 스냅 : 총 {snappedCount}개의 UBOLT가 배관 반경 및 치수 확장을 고려하여 구조물에 스냅되었습니다.");
        Console.ResetColor();
      }

      return snappedCount;
    }

  }
}