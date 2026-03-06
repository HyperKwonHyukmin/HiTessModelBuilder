using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: UBOLT 수직 스냅]
  /// UBOLT 강체 요소의 Independent Node에서 수직 아래(Z: -1) 방향으로 레이(Ray)를 쏘아,
  /// 허용 반경 내에 있는 가장 가까운 구조물(Stru) 요소를 찾고 해당 위치에 Dependent Node를 생성 및 연결합니다.
  /// </summary>
  public static class UboltSnapToStructureModifier
  {
    public sealed record Options(
        double Tolerance = 50.0,        // 구조물 중심선으로부터의 레이캐스팅 허용 오차 (단면 두께 고려)
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int snappedCount = 0;

      // 1. "UBOLT" 타입의 강체(Rigid)만 필터링
      var ubolts = context.Rigids.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Type", out var type) &&
          type == "UBOLT").ToList();

      if (ubolts.Count == 0) return 0;

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] UboltSnapToStructureModifier (UBOLT 하향 구조물 스냅)");
        log($" -> 탐색 대상 UBOLT 개수: {ubolts.Count}개");
        log($"==================================================\n");
      }

      // 2. 타겟이 될 "구조물(Stru)" 요소만 필터링
      var struElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Classification", out var cls) &&
          cls == "Stru").ToList();

      // 수직 아래 방향 벡터
      Vector3D rayDir = new Vector3D(0, 0, -1);

      // 3. 각 UBOLT 마다 수직 아래의 최적 구조물 탐색
      foreach (var ubolt in ubolts)
      {
        int rigidId = ubolt.Key;
        var info = ubolt.Value;
        var pIndep = context.Nodes[info.IndependentNodeID];

        double bestS = double.MaxValue;
        Point3D bestHitPoint = default;
        int bestTargetEid = -1;

        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          // Ray-Segment 교차 검사 실행
          bool isHit = TryRaySegmentIntersection(pIndep, rayDir, pA, pB, opt.Tolerance, out double s, out Point3D hitPoint);

          // s > 0 : 방향이 아래쪽인지 확인
          // s < bestS : 가장 먼저 부딪히는(가장 가까운) 구조물인지 확인
          if (isHit && s > 0 && s < bestS)
          {
            bestS = s;
            bestHitPoint = hitPoint;
            bestTargetEid = struKv.Key;
          }
        }

        // 4. 최적의 구조물을 찾았다면 종속 노드(Dependent Node) 갈아끼우기
        // 4. 최적의 구조물을 찾았다면 종속 노드(Dependent Node) 채워넣기
        if (bestTargetEid != -1)
        {
          int newDepNodeId = context.Nodes.AddOrGet(bestHitPoint.X, bestHitPoint.Y, bestHitPoint.Z);

          // ★ [수정] Remap이 아니라 빈 강체에 새로운 종속 노드를 "추가"합니다.
          // (Rigids.cs에 이미 구현되어 있는 AppendDependentNodes 활용)
          context.Rigids.AppendDependentNodes(rigidId, new[] { newDepNodeId });

          snappedCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[UBOLT 연결 완료] RBE {rigidId} (Indep: N{info.IndependentNodeID}) -> 수직 아래 구조물 E{bestTargetEid} 스냅.");
            Console.ResetColor();
            log($"   - 생성/연결된 Dep 노드: N{newDepNodeId} (이동 거리: {bestS:F1}mm)");
          }
        }
        else
        {
          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            log($"[UBOLT 연결 실패] RBE {rigidId} (Indep: N{info.IndependentNodeID}) 수직 아래 허용 반경 내에 구조물이 없습니다.");
            Console.ResetColor();
          }
        }
      }

      if (opt.PipelineDebug) log($"[수정 완료] 총 {snappedCount}개의 UBOLT가 하부 구조물에 스냅되었습니다.\n");

      return snappedCount;
    }

    /// <summary>
    /// 3D 공간 상에서 Ray(직선)와 Segment(선분) 간의 최단거리를 구하고, 허용치 이내면 교차로 판정합니다.
    /// </summary>
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
      const double EPS = 1e-8;

      if (D < EPS) return false; // 레이와 선분이 평행함

      s = (b * e - c * d) / D;
      double t = (a * e - b * d) / D;

      // 선분 밖으로 벗어난 경우 양 끝점으로 클램핑
      if (t < 0.0) t = 0.0;
      else if (t > 1.0) t = 1.0;

      s = (t * b - d) / a;

      Point3D pRay = rayOrigin + (u * s);
      Point3D pSeg = segA + (v * t);

      double dist = (pRay - pSeg).Magnitude();

      // 두 선 사이의 최단 거리가 오차 이내라면 교차(Hit)한 것으로 인정
      if (dist <= tolerance)
      {
        hitPoint = pSeg;
        return true;
      }

      return false;
    }
  }
}
