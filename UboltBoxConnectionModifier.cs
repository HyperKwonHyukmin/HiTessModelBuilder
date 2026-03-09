using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: UBOLT BOX 타입 연결 및 부재 분할]
  /// UBOLT (BOX 타입)의 Independent Node를 중심으로 가장 가까운 4개의 구조물(Stru) 부재를 탐색합니다.
  /// 각 부재에 수선의 발을 내려 새로운 Node를 생성하고, 해당 Node를 기준으로 부재를 분할(Split)한 뒤
  /// 생성된 4개의 Node를 UBOLT의 Dependent Node로 일괄 연결합니다.
  /// </summary>
  public static class UboltBoxConnectionModifier
  {
    public sealed record Options(
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int processedCount = 0;

      // 1. "UBOLT" 이면서 Remark가 "BOX"인 강체 필터링
      var uboltBoxes = context.Rigids.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Type", out var type) && type == "UBOLT" &&
          kv.Value.ExtraData.TryGetValue("Remark", out var remark) && remark == "BOX"
      ).ToList();

      if (uboltBoxes.Count == 0) return 0;

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] UboltBoxConnectionModifier (BOX 타입 UBOLT 4점 연결)");
        log($" -> 탐색 대상 BOX UBOLT 개수: {uboltBoxes.Count}개");
        log($"==================================================\n");
      }

      foreach (var ubolt in uboltBoxes)
      {
        int rigidId = ubolt.Key;
        int indepNodeId = ubolt.Value.IndependentNodeID;
        var pIndep = context.Nodes[indepNodeId];

        // 루프마다 Stru 요소를 새로 쿼리합니다. (이전 UBOLT 처리에 의해 분할된 최신 상태 반영)
        var struElements = context.Elements.Where(kv =>
            kv.Value.ExtraData != null &&
            kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru"
        ).ToList();

        // 2. 모든 Stru 부재와의 수선의 발 및 거리 계산
        var distances = new List<(int Eid, double Dist, Point3D ProjPoint)>();

        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          double dist = DistancePointToSegment(pIndep, pA, pB, out Point3D projPoint);
          distances.Add((struKv.Key, dist, projPoint));
        }

        // 3. 거리가 가장 가까운 4개의 부재 선택
        var closest4 = distances.OrderBy(x => x.Dist).Take(4).ToList();
        var newDependentNodes = new List<int>();

        foreach (var target in closest4)
        {
          // 3-1. 수선의 발 위치에 새로운 Node 생성
          int depNodeId = context.Nodes.AddOrGet(target.ProjPoint.X, target.ProjPoint.Y, target.ProjPoint.Z);
          newDependentNodes.Add(depNodeId);

          // 3-2. 해당 부재 쪼개기 (Split)
          if (context.Elements.Contains(target.Eid))
          {
            var targetElem = context.Elements[target.Eid];
            int nA = targetElem.NodeIDs.First();
            int nB = targetElem.NodeIDs.Last();

            // 이미 끝점과 동일한 위치라면 쪼갤 필요 없음
            if (nA == depNodeId || nB == depNodeId) continue;

            var extraCopy = targetElem.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
            int propId = targetElem.PropertyID;

            // 원본 부재의 방향(Orientation) 정보를 가져옵니다.
            var barOrientation = targetElem.Orientation;

            // 기존 요소 덮어쓰기 (앞부분) 및 새 요소 생성 (뒷부분) - Orientation 인자 추가
            context.Elements.Remove(target.Eid);
            context.Elements.AddWithID(target.Eid, new List<int> { nA, depNodeId }, propId, barOrientation, extraCopy);
            context.Elements.AddNew(new List<int> { depNodeId, nB }, propId, barOrientation, extraCopy);

            if (opt.VerboseDebug)
              log($"   -> [분할] E{target.Eid} 부재가 N{depNodeId} 노드를 중심으로 분할되었습니다.");
          }
        }

        // 4. 생성된 4개의 노드를 UBOLT의 Dependent Node로 추가
        if (newDependentNodes.Count > 0)
        {
          context.Rigids.AppendDependentNodes(rigidId, newDependentNodes);
          processedCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[연결 완료] RBE {rigidId} (BOX UBOLT) -> 주변 부재 4곳 연결 완료.");
            Console.ResetColor();
            log($"   - 추가된 Dependent Nodes: {string.Join(", ", newDependentNodes)}\n");
          }
        }
      }

      if (opt.PipelineDebug)
        log($"[수정 완료] 총 {processedCount}개의 BOX UBOLT가 4점 연결 및 부재 분할을 완료했습니다.\n");

      return processedCount;
    }

    /// <summary>
    /// 점 P와 선분 AB 사이의 최단 거리와, 그 수선의 발(투영점)을 함께 반환합니다.
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
      t = Math.Max(0.0, Math.Min(1.0, t)); // 선분 밖으로 벗어나지 않게 클램핑

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}
