using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: 일반 UBOLT 지능형 스냅]
  /// 수직 Ray-Casting의 한계를 극복하고, 3D 공간상 최단 거리(Point to Segment)를 사용하여
  /// 미세하게 X/Y축이 어긋난 구조물이라도 안정적으로 찾아내어 스냅합니다.
  /// </summary>
  public static class UboltSnapToStructureModifier
  {
    public sealed record Options(
        double MaxSearchRadius = 300.0, // 주변 구조물을 탐색할 최대 3D 반경(구/원통 반경)
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

      // 동일 노드 중복 UBOLT 제거 방어 로직
      var ubolts = new List<KeyValuePair<int, RigidInfo>>();
      var processedNodes = new HashSet<int>();

      foreach (var ubolt in rawUbolts)
      {
        int indepNode = ubolt.Value.IndependentNodeID;
        if (processedNodes.Contains(indepNode))
        {
          context.Rigids.Remove(ubolt.Key);
          continue;
        }
        processedNodes.Add(indepNode);
        ubolts.Add(ubolt);
      }

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] UboltSnapToStructureModifier (3D 최단 거리 스냅 탐색)");
        log($" -> 탐색 대상 일반 UBOLT 개수: {ubolts.Count}개");
        log($"==================================================\n");
      }

      var struElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru").ToList();

      var pipeElements = context.Elements.Where(kv =>
          kv.Value.ExtraData != null && kv.Value.ExtraData.TryGetValue("Category", out var cat) && cat == "Pipe").ToList();

      foreach (var ubolt in ubolts)
      {
        int rigidId = ubolt.Key;
        var info = ubolt.Value;
        int oldIndepNodeId = info.IndependentNodeID;
        var pIndep = context.Nodes[oldIndepNodeId];

        var ownerPipeElem = pipeElements.FirstOrDefault(kv => kv.Value.NodeIDs.Contains(oldIndepNodeId)).Value;

        double bestDist = double.MaxValue;
        Point3D bestHitStruPoint = default;
        int bestStruTargetEid = -1;

        // [핵심 변경] 수직 광선(Ray) 대신 3D 선분 최단 거리(DistancePointToSegment) 탐색 수행
        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          // U-Bolt 노드(pIndep)와 구조물 선분(pA-pB) 사이의 수선의 발과 최단 거리를 구함
          double dist = DistancePointToSegment(pIndep, pA, pB, out Point3D projPoint);

          // 가장 가깝고, 최대 허용 반경(MaxSearchRadius) 이내인 경우 갱신
          if (dist <= opt.MaxSearchRadius && dist < bestDist)
          {
            bestDist = dist;
            bestHitStruPoint = projPoint;
            bestStruTargetEid = struKv.Key;
          }
        }

        // [연결 처리] 타겟 구조물을 찾았다면
        if (bestStruTargetEid != -1)
        {
          // 1. 타겟 구조물 위에 수선의 발(Dependent Node) 생성
          int newDepNodeId = context.Nodes.AddOrGet(bestHitStruPoint.X, bestHitStruPoint.Y, bestHitStruPoint.Z);

          // 2. 배관 선상 위치 보정
          Point3D bestProjPipePoint = pIndep;
          if (ownerPipeElem != null && ownerPipeElem.NodeIDs.Count >= 2)
          {
            var pPipeA = context.Nodes[ownerPipeElem.NodeIDs.First()];
            var pPipeB = context.Nodes[ownerPipeElem.NodeIDs.Last()];
            DistancePointToSegment(bestHitStruPoint, pPipeA, pPipeB, out bestProjPipePoint);
          }
          int newIndepNodeId = context.Nodes.AddOrGet(bestProjPipePoint.X, bestProjPipePoint.Y, bestProjPipePoint.Z);

          // 3. RBE 노드 매핑
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
            log($"[연결 완료] RBE {rigidId} -> 구조물 E{bestStruTargetEid} 연결 (최단거리 스냅, 이격: {bestDist:F1}mm)");
            Console.ResetColor();
          }
        }
        else
        {
          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            log($"[누락] RBE {rigidId} (N{oldIndepNodeId}) -> 반경 {opt.MaxSearchRadius}mm 이내에 지지 구조물을 찾지 못해 스킵되었습니다.");
            Console.ResetColor();
          }
        }
      }

      if (opt.PipelineDebug) 
          log($"[수정 완료] 총 {snappedCount}개의 UBOLT가 3D 최단 거리 경로로 스냅되었습니다.\n");

      return snappedCount;
    }

    /// <summary>
    /// 점 P와 선분 AB 사이의 3D 공간 최단 거리와 선분 위 수선의 발(projPoint)을 계산합니다.
    /// </summary>
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
      t = Math.Max(0.0, Math.Min(1.0, t)); // 선분 양 끝점을 벗어나지 않도록 클램핑

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}
