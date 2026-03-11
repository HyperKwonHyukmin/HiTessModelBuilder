using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: 일반 UBOLT 지능형 스냅]
  /// 배관의 직경(Radius)을 고려한 동적 탐색 반경을 사용하여,
  /// 대구경 배관에서도 주변 지지 구조물을 안정적으로 찾아내 스냅합니다.
  /// </summary>
  public static class UboltSnapToStructureModifier
  {
    public sealed record Options(
        double MaxSearchRadius = 300.0, // 기본 최대 탐색 반경
        double ExtraMargin = 100.0,     // ★ 배관 반지름에 추가로 더할 여유 스냅 마진
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
          // ★ Name 추적 및 로그 추가
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
        log($"[수정 시작] UboltSnapToStructureModifier (동적 탐색 반경 적용)");
        log($" -> 탐색 대상 일반 UBOLT 개수: {ubolts.Count}개");
        log($"==================================================\n");
      }

      var struElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru").ToList();

      // ★ [버그 수정] Classification과 Category 둘 중 하나라도 "Pipe"이면 배관으로 인식하도록 수정
      var pipeElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null &&
          (
              (kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Pipe") ||
              (kv.Value.ExtraData.TryGetValue("Category", out var cat) && cat == "Pipe")
          )
      ).ToList();
      foreach (var ubolt in ubolts)
      {
        int rigidId = ubolt.Key;
        var info = ubolt.Value;
        int oldIndepNodeId = info.IndependentNodeID;
        var pIndep = context.Nodes[oldIndepNodeId];

        var ownerPipeElem = pipeElements.FirstOrDefault(kv => kv.Value.NodeIDs.Contains(oldIndepNodeId)).Value;

        // ★ [신규 핵심 로직] 배관 반지름을 바탕으로 동적 탐색 반경 계산
        double pipeRadius = ownerPipeElem != null ? ownerPipeElem.GetReferencedPropertyDim(context.Properties) : 0.0;
        double dynamicSearchRadius = Math.Max(opt.MaxSearchRadius, pipeRadius + opt.ExtraMargin);

        double bestDist = double.MaxValue;
        Point3D bestHitStruPoint = default;
        int bestStruTargetEid = -1;

        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          double dist = DistancePointToSegment(pIndep, pA, pB, out Point3D projPoint);

          // 고정된 MaxSearchRadius 대신 동적으로 커진 dynamicSearchRadius 사용
          if (dist <= dynamicSearchRadius && dist < bestDist)
          {
            bestDist = dist;
            bestHitStruPoint = projPoint;
            bestStruTargetEid = struKv.Key;
          }
        }

        if (bestStruTargetEid != -1)
        {
          int newDepNodeId = context.Nodes.AddOrGet(bestHitStruPoint.X, bestHitStruPoint.Y, bestHitStruPoint.Z);

          Point3D bestProjPipePoint = pIndep;
          if (ownerPipeElem != null && ownerPipeElem.NodeIDs.Count >= 2)
          {
            var pPipeA = context.Nodes[ownerPipeElem.NodeIDs.First()];
            var pPipeB = context.Nodes[ownerPipeElem.NodeIDs.Last()];
            DistancePointToSegment(bestHitStruPoint, pPipeA, pPipeB, out bestProjPipePoint);
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
            log($"[연결 완료] RBE {rigidId} -> 구조물 E{bestStruTargetEid} (이격: {bestDist:F1}mm / 배관 R: {pipeRadius:F1}mm)");
            Console.ResetColor();
          }
        }
        else
        {
          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            log($"[누락] RBE {rigidId} (N{oldIndepNodeId}) -> 동적 반경 {dynamicSearchRadius:F1}mm 내 지지 구조물 없음.");
            Console.ResetColor();
          }
        }
      }

      if (opt.PipelineDebug && snappedCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        log($"[변경] 일반 U-Bolt 스냅 : 총 {snappedCount}개의 UBOLT가 배관 반경을 고려하여 구조물에 스냅되었습니다.");
        Console.ResetColor();
      }

      return snappedCount;
    }

    private static double DistancePointToSegment(Point3D p, Point3D a, Point3D b, out Point3D projPoint)
    {
      var ab = b - a;
      var ap = p - a;
      double lengthSq = ab.Dot(ab);
      if (lengthSq < 1e-12)
      {
        projPoint = a;
        return (p - a).Magnitude();
      }
      double t = Math.Max(0.0, Math.Min(1.0, ap.Dot(ab) / lengthSq));
      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}
