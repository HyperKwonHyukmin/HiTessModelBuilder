using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: UBOLT 지능형 수직 스냅]
  /// 1. UBOLT의 위치에서 수직 아래 방향으로 구조물을 탐색하여 교차점(Dependent Node)을 찾습니다.
  /// 2. 찾은 교차점에서 다시 배관(Pipe)에 수선의 발을 내려 최적의 직교 지점을 찾습니다.
  /// 3. UBOLT의 Independent Node를 수선의 발 위치로 이동시키고, Dependent Node를 구조물에 연결합니다.
  /// </summary>
  public static class UboltSnapToStructureModifier
  {
    public sealed record Options(
        double Tolerance = 50.0,        // 레이캐스팅 허용 오차 (단면 두께 고려)
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int snappedCount = 0;

      // 1. "UBOLT" 강체 필터링
      var ubolts = context.Rigids.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Type", out var type) &&
          type == "UBOLT").ToList();

      if (ubolts.Count == 0) return 0;

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] UboltSnapToStructureModifier (직교 경로 보정 기능 적용)");
        log($" -> 탐색 대상 UBOLT 개수: {ubolts.Count}개");
        log($"==================================================\n");
      }

      // 2. 구조물(Stru) 요소 및 배관(Pipe) 요소 필터링
      var struElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru").ToList();

      var pipeElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Category", out var cat) && cat == "Pipe").ToList();

      Vector3D rayDir = new Vector3D(0, 0, -1);

      // 3. 각 UBOLT 마다 로직 수행
      foreach (var ubolt in ubolts)
      {
        int rigidId = ubolt.Key;
        var info = ubolt.Value;
        int oldIndepNodeId = info.IndependentNodeID;
        var pIndep = context.Nodes[oldIndepNodeId];

        // 현재 UBOLT가 매달려 있는 배관 부재(Pipe Element) 찾기
        var ownerPipeElem = pipeElements.FirstOrDefault(kv => kv.Value.NodeIDs.Contains(oldIndepNodeId)).Value;

        double bestS = double.MaxValue;
        Point3D bestHitStruPoint = default;
        int bestStruTargetEid = -1;

        // [STEP A] 수직 아래 구조물 탐색
        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          bool isHit = TryRaySegmentIntersection(pIndep, rayDir, pA, pB, opt.Tolerance, out double s, out Point3D hitPoint);

          if (isHit && s > 0 && s < bestS)
          {
            bestS = s;
            bestHitStruPoint = hitPoint;
            bestStruTargetEid = struKv.Key;
          }
        }

        // [STEP B] 구조물 교차점을 찾았다면, 최적의 직교 경로 재계산 및 노드 업데이트
        if (bestStruTargetEid != -1)
        {
          // 1. 구조물 위에 생성될 Dependent Node
          int newDepNodeId = context.Nodes.AddOrGet(bestHitStruPoint.X, bestHitStruPoint.Y, bestHitStruPoint.Z);

          // 2. 구조물 교차점(bestHitStruPoint)에서 배관(ownerPipeElem)으로 수선의 발 내리기
          Point3D bestProjPipePoint = pIndep; // 배관을 못 찾으면 기존 위치 유지
          if (ownerPipeElem != null && ownerPipeElem.NodeIDs.Count >= 2)
          {
            var pPipeA = context.Nodes[ownerPipeElem.NodeIDs.First()];
            var pPipeB = context.Nodes[ownerPipeElem.NodeIDs.Last()];

            // 구조물 점 -> 배관 선분으로 수선의 발(Projection) 계산
            DistancePointToSegment(bestHitStruPoint, pPipeA, pPipeB, out bestProjPipePoint);
          }

          // 3. 수선의 발 위치에 새로운 Independent Node 생성
          int newIndepNodeId = context.Nodes.AddOrGet(bestProjPipePoint.X, bestProjPipePoint.Y, bestProjPipePoint.Z);

          // 4. 강체(RBE) 업데이트: 인디펜던트 노드 이동 & 디펜던트 노드 할당
          if (info.DependentNodeIDs.Count > 0)
          {
            // 레거시 더미 노드가 있는 경우: 둘 다 치환
            context.Rigids.RemapNodes(rigidId, new Dictionary<int, int> {
                            { oldIndepNodeId, newIndepNodeId },
                            { info.DependentNodeIDs[0], newDepNodeId }
                        });
          }
          else
          {
            // 빈 배열로 생성된 경우: 인디펜던트 먼저 옮기고(dropIfEmpty: false로 삭제 방지), 디펜던트 추가
            context.Rigids.RemapNodes(rigidId, new Dictionary<int, int> { { oldIndepNodeId, newIndepNodeId } }, dropIfEmpty: false);
            context.Rigids.AppendDependentNodes(rigidId, new[] { newDepNodeId });
          }

          snappedCount++;

          if (opt.VerboseDebug)
          {
            double shiftDist = (pIndep - bestProjPipePoint).Magnitude();
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[UBOLT 직교 스냅 완료] RBE {rigidId} -> 수직 아래 구조물 E{bestStruTargetEid} 연결.");
            Console.ResetColor();
            log($"   - Indep 노드 재배치: N{oldIndepNodeId} -> N{newIndepNodeId} (배관 위 슬라이딩: {shiftDist:F2}mm)");
            log($"   - 연결된 Dep 노드: N{newDepNodeId} (구조물 타겟 거리: {bestS:F1}mm)");
          }
        }
      }

      if (opt.PipelineDebug) log($"[수정 완료] 총 {snappedCount}개의 UBOLT가 직교 경로로 보정되어 스냅되었습니다.\n");

      return snappedCount;
    }

    // --- Helper 1: 선분으로 수선의 발 내리기 (최단 거리 및 투영점 반환) ---
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

      double t = ap.Dot(ab) / lengthSq;
      t = Math.Max(0.0, Math.Min(1.0, t)); // 선분 밖으로 벗어나지 않게 클램핑

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }

    // --- Helper 2: Ray(직선)와 Segment(선분) 간의 교차 검사 ---
    private static bool TryRaySegmentIntersection(
        Point3D rayOrigin, Vector3D rayDir,
        Point3D segA, Point3D segB,
        double tolerance,
        out double s, out Point3D hitPoint)
    {
      s = 0; hitPoint = default;
      Vector3D u = rayDir;
      Vector3D v = segB - segA;
      Vector3D w = rayOrigin - segA;

      double a = u.Dot(u);
      double b = u.Dot(v);
      double c = v.Dot(v);
      double d = u.Dot(w);
      double e = v.Dot(w);

      double D = a * c - b * b;
      if (D < 1e-8) return false;

      s = (b * e - c * d) / D;
      double t = (a * e - b * d) / D;

      if (t < 0.0) t = 0.0;
      else if (t > 1.0) t = 1.0;

      s = (t * b - d) / a;
      Point3D pRay = rayOrigin + (u * s);
      Point3D pSeg = segA + (v * t);

      if ((pRay - pSeg).Magnitude() <= tolerance)
      {
        hitPoint = pSeg;
        return true;
      }
      return false;
    }
  }
}
