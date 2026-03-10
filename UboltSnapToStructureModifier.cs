using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: 일반 UBOLT 지능형 수직 스냅]
  /// 1. UBOLT 위치에서 수직 아래(Down) 방향으로 구조물을 탐색합니다.
  /// 2. 아래쪽에 없다면 수직 위(Up) 방향으로 매달린 배관을 고려해 탐색합니다.
  /// 3. 탐색 반경(Tolerance)을 통해 기울어지거나 대각선에 위치한 부재도 포착합니다.
  /// 4. 찾은 교차점에 노드를 찍고 UBOLT의 Dependent로 연결합니다.
  /// </summary>
  public static class UboltSnapToStructureModifier
  {
    public sealed record Options(
        double Tolerance = 50.0,
        double MaxDepth = 300.0,       // [신규 추가] U-bolt가 구조물을 찾기 위해 광선을 쏠 최대 거리 (이 범위를 넘으면 무시)
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int snappedCount = 0;

      // 1. "UBOLT" 강체 필터링 (단, BOX 타입은 별도로 처리하므로 제외)
      var rawUbolts = context.Rigids.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Type", out var type) && type == "UBOLT" &&
          !(kv.Value.ExtraData.TryGetValue("Remark", out var remark) && remark == "BOX")
      ).ToList();

      if (rawUbolts.Count == 0) return 0;

      // ★ [신규 추가] 중복 방지 방어 로직
      // 동일한 노드(IndependentNode)에 여러 개의 UBOLT가 붙어있다면 1개만 남기고 나머지는 삭제
      var ubolts = new List<KeyValuePair<int, RigidInfo>>();
      var processedNodes = new HashSet<int>();

      foreach (var ubolt in rawUbolts)
      {
        int indepNode = ubolt.Value.IndependentNodeID;

        if (processedNodes.Contains(indepNode))
        {
          // 이미 해당 노드에 UBOLT가 등록되어 있다면, 이 녀석은 중복 찌꺼기이므로 모델에서 영구 삭제
          context.Rigids.Remove(ubolt.Key);
          if (opt.VerboseDebug) log($"[정리] N{indepNode}에 중복된 UBOLT(RBE {ubolt.Key})가 발견되어 삭제되었습니다.");
          continue;
        }

        processedNodes.Add(indepNode);
        ubolts.Add(ubolt);
      }
      // ubolts 컬렉션에는 이제 완벽하게 1노드 1UBOLT만 남아있습니다.

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] UboltSnapToStructureModifier (위/아래 양방향 스냅 탐색)");
        log($" -> 탐색 대상 일반 UBOLT 개수: {ubolts.Count}개");
        log($"==================================================\n");
      }

      var struElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru").ToList();

      var pipeElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Category", out var cat) && cat == "Pipe").ToList();

      Vector3D rayDown = new Vector3D(0, 0, -1);
      Vector3D rayUp = new Vector3D(0, 0, 1);

      foreach (var ubolt in ubolts)
      {
        int rigidId = ubolt.Key;
        var info = ubolt.Value;
        int oldIndepNodeId = info.IndependentNodeID;
        var pIndep = context.Nodes[oldIndepNodeId];

        var ownerPipeElem = pipeElements.FirstOrDefault(kv => kv.Value.NodeIDs.Contains(oldIndepNodeId)).Value;

        double bestS = double.MaxValue;
        Point3D bestHitStruPoint = default;
        int bestStruTargetEid = -1;
        string hitDir = "";

        // [STEP A-1] 수직 아래(Down) 구조물 우선 탐색
        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          // Tolerance가 원기둥 반경 역할을 하여 대각선에 있는 부재도 잡아냅니다.
          bool isHit = TryRaySegmentIntersection(pIndep, rayDown, pA, pB, opt.Tolerance, out double s, out Point3D hitPoint);

          // [수정됨] s(거리)가 MaxDepth보다 작을 때만 채택 (엉뚱하게 멀리 있는 부재 스냅 방지)
          if (isHit && s > 0 && s < opt.MaxDepth && s < bestS)
          {
            bestS = s;
            bestHitStruPoint = hitPoint;
            bestStruTargetEid = struKv.Key;
            hitDir = "아래쪽(Down)";
          }
        }

        // [STEP A-2] 아래쪽에 없다면 수직 위(Up) 구조물 탐색 (매달린 배관 처리)
        if (bestStruTargetEid == -1)
        {
          bestS = double.MaxValue; // 거리 초기화
          foreach (var struKv in struElements)
          {
            var elem = struKv.Value;
            if (elem.NodeIDs.Count < 2) continue;

            var pA = context.Nodes[elem.NodeIDs.First()];
            var pB = context.Nodes[elem.NodeIDs.Last()];

            bool isHit = TryRaySegmentIntersection(pIndep, rayUp, pA, pB, opt.Tolerance, out double s, out Point3D hitPoint);

            // [수정됨] 위쪽 탐색도 동일하게 MaxDepth 적용
            if (isHit && s > 0 && s < opt.MaxDepth && s < bestS)
            {
              bestS = s;
              bestHitStruPoint = hitPoint;
              bestStruTargetEid = struKv.Key;
              hitDir = "윗쪽(Up)";
            }
          }
        }

        // [STEP B] 타겟을 찾았다면 노드 매핑 및 연결 진행
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
            double shiftDist = (pIndep - bestProjPipePoint).Magnitude();
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[연결 완료] RBE {rigidId} -> {hitDir} 구조물 E{bestStruTargetEid} 연결.");
            Console.ResetColor();
          }
        }
        else
        {
          if (opt.VerboseDebug)
          {
            // [STEP C] 위/아래 양쪽 모두 못 찾았을 경우, 치명적 오류 로깅 (Dep:[] 방지 목적)
            Console.ForegroundColor = ConsoleColor.Red;
            log($"[치명적 오류] RBE {rigidId} (일반 UBOLT) -> N{oldIndepNodeId} 반경 {opt.Tolerance}mm 내 위/아래로 뻗은 구조물(Stru)이 없습니다! (Dependent Node 비어있음)");
            Console.ResetColor();
          }

        }
      }

      if (opt.PipelineDebug) log($"[수정 완료] 총 {snappedCount}개의 UBOLT가 위/아래 지능형 경로로 스냅되었습니다.\n");

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

      double t = ap.Dot(ab) / lengthSq;
      t = Math.Max(0.0, Math.Min(1.0, t));

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }

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
