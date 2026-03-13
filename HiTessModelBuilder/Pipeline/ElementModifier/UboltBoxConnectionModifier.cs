using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: UBOLT BOX 타입 4방향 연결 및 부재 분할]
  /// 1. UBOLT의 Independent Node를 포함하는 배관(Pipe)을 찾고 방향 벡터를 구합니다.
  /// 2. 배관 벡터에 직교하는 상/하/좌/우 로컬 축을 생성합니다.
  /// 3. 모든 구조물 부재에 수선의 발을 내린 뒤, 어느 사분면(구역)에 속하는지 분류합니다.
  /// 4. 각 4방향(윗쪽/아랫쪽/왼쪽/오른쪽)에서 가장 가까운 부재 1개씩(최대 4개)만 선택하여 분할 후 연결합니다.
  /// 5. [추가] 배관 방향의 단차를 없애기 위해 Independent Node를 Dependent 평면으로 정렬합니다.
  /// </summary>
  public static class UboltBoxConnectionModifier
  {
    public sealed record Options(
        double MaxSearchDistance = 2000.0, // 너무 멀리 있는 부재가 엉뚱하게 잡히는 것을 방지
        double ExtraMargin = 100.0,
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
        log($"[수정 시작] UboltBoxConnectionModifier (BOX UBOLT 4방향 탐색 및 평면 정렬)");
        log($" -> 탐색 대상 BOX UBOLT 개수: {uboltBoxes.Count}개");
        log($"==================================================\n");
      }

      foreach (var ubolt in uboltBoxes)
      {
        int rigidId = ubolt.Key;
        int indepNodeId = ubolt.Value.IndependentNodeID;
        var pIndep = context.Nodes[indepNodeId];

        // [Step 1] Independent Node를 공유하는 배관(Pipe) 찾기
        var connectedPipes = context.Elements.Where(kv =>
            kv.Value.ExtraData != null &&
            (
                (kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Pipe") ||
                (kv.Value.ExtraData.TryGetValue("Category", out var cat) && cat == "Pipe")
            ) &&
            kv.Value.NodeIDs.Contains(indepNodeId)
        ).ToList();

        if (connectedPipes.Count == 0)
        {
          if (opt.VerboseDebug) log($"[경고] RBE {rigidId}의 Independent Node(N{indepNodeId})를 가지는 배관 부재를 찾을 수 없습니다.");
          continue;
        }

        // [Step 2] 배관 1개를 골라 방향 벡터(vX) 도출
        var pipeElem = connectedPipes.First().Value;

        double pipeRadius = pipeElem.GetReferencedPropertyDim(context.Properties);
        double dynamicSearchDist = Math.Max(opt.MaxSearchDistance, pipeRadius + opt.ExtraMargin);

        var pPipeA = context.Nodes[pipeElem.NodeIDs.First()];
        var pPipeB = context.Nodes[pipeElem.NodeIDs.Last()];
        var vX = (pPipeB - pPipeA).Normalize();

        if (vX.Magnitude() < 1e-6) continue;

        // 배관 진행 방향 기준, 직교하는 로컬 좌표계(vUp, vRight) 구축
        var vGlobalUp = new Vector3D(0, 0, 1);
        if (Math.Abs(vX.Z) > 0.99) vGlobalUp = new Vector3D(0, 1, 0); // 배관이 Z축으로 서있을 경우 예외처리

        var vRight = CrossProduct(vX, vGlobalUp).Normalize();
        var vUp = CrossProduct(vRight, vX).Normalize();

        // [Step 3] 구조물 부재(Stru)를 순회하며 4방향 버킷에 담기
        var struElements = context.Elements.Where(kv =>
            kv.Value.ExtraData != null &&
            kv.Value.ExtraData.TryGetValue("Classification", out var cls) && cls == "Stru"
        ).ToList();

        var topCands = new List<(int Eid, double Dist, Point3D ProjPoint)>();
        var bottomCands = new List<(int Eid, double Dist, Point3D ProjPoint)>();
        var rightCands = new List<(int Eid, double Dist, Point3D ProjPoint)>();
        var leftCands = new List<(int Eid, double Dist, Point3D ProjPoint)>();

        foreach (var struKv in struElements)
        {
          var elem = struKv.Value;
          if (elem.NodeIDs.Count < 2) continue;

          var pA = context.Nodes[elem.NodeIDs.First()];
          var pB = context.Nodes[elem.NodeIDs.Last()];

          // 수선의 발과 거리 계산
          double dist = DistancePointToSegment(pIndep, pA, pB, out Point3D projPoint);
          if (dist > dynamicSearchDist) continue;

          Vector3D diff = projPoint - pIndep;
          if (diff.Magnitude() < 1e-3) continue;

          // 수선의 발 벡터를 배관의 위(Up)와 오른쪽(Right)에 투영
          double u = diff.Dot(vUp);
          double r = diff.Dot(vRight);

          if (Math.Abs(u) >= Math.Abs(r))
          {
            if (u >= 0) topCands.Add((struKv.Key, dist, projPoint));
            else bottomCands.Add((struKv.Key, dist, projPoint));
          }
          else
          {
            if (r >= 0) rightCands.Add((struKv.Key, dist, projPoint));
            else leftCands.Add((struKv.Key, dist, projPoint));
          }
        }

        // [Step 4] 각 4개 구역에서 가장 거리가 짧은 부재 1개씩만 선택
        var selectedTargets = new List<(int Eid, double Dist, Point3D ProjPoint, string Dir)>();
        if (topCands.Any()) selectedTargets.Add((topCands.OrderBy(x => x.Dist).First().Eid, topCands.OrderBy(x => x.Dist).First().Dist, topCands.OrderBy(x => x.Dist).First().ProjPoint, "윗쪽"));
        if (bottomCands.Any()) selectedTargets.Add((bottomCands.OrderBy(x => x.Dist).First().Eid, bottomCands.OrderBy(x => x.Dist).First().Dist, bottomCands.OrderBy(x => x.Dist).First().ProjPoint, "아랫쪽"));
        if (rightCands.Any()) selectedTargets.Add((rightCands.OrderBy(x => x.Dist).First().Eid, rightCands.OrderBy(x => x.Dist).First().Dist, rightCands.OrderBy(x => x.Dist).First().ProjPoint, "오른쪽"));
        if (leftCands.Any()) selectedTargets.Add((leftCands.OrderBy(x => x.Dist).First().Eid, leftCands.OrderBy(x => x.Dist).First().Dist, leftCands.OrderBy(x => x.Dist).First().ProjPoint, "왼쪽"));

        // =====================================================================
        // ★ [신규 추가] 배관 노드(Independent Node)를 BOX 평면으로 슬라이딩 정렬
        // =====================================================================
        if (selectedTargets.Count > 0)
        {
          // 찾은 Dependent 노드들의 배관 진행 방향(vX) 기준 평균 단차(Offset) 계산
          double avgShift = selectedTargets.Average(t => (t.ProjPoint - pIndep).Dot(vX));

          // 0.01mm 이상의 단차가 존재할 경우, 해당 수치만큼 배관 진행방향으로 이동
          if (Math.Abs(avgShift) > 0.01)
          {
            pIndep = pIndep + (vX * avgShift);
            // 기존 Independent Node의 좌표를 이동된 평면 좌표로 덮어쓰기
            context.Nodes.AddWithID(indepNodeId, pIndep.X, pIndep.Y, pIndep.Z);

            if (opt.VerboseDebug)
              log($"   -> [노드 평면 정렬] Independent Node(N{indepNodeId})를 BOX 구조물 평면에 맞추어 파이프 축 방향으로 {avgShift:F2}mm 이동했습니다.");
          }
        }
        // =====================================================================

        var newDependentNodes = new List<int>();

        // 선택된 부재들에 노드를 찍고 Split 처리
        foreach (var target in selectedTargets)
        {
          int depNodeId = context.Nodes.AddOrGet(target.ProjPoint.X, target.ProjPoint.Y, target.ProjPoint.Z);
          newDependentNodes.Add(depNodeId);

          if (context.Elements.Contains(target.Eid))
          {
            var targetElem = context.Elements[target.Eid];
            int nA = targetElem.NodeIDs.First();
            int nB = targetElem.NodeIDs.Last();

            if (nA == depNodeId || nB == depNodeId) continue; // 이미 끝점이면 분할 생략

            var extraCopy = targetElem.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
            int propId = targetElem.PropertyID;
            var barOrientation = targetElem.Orientation; // 방향성 유지

            // 분할: 기존 요소 수정 및 신규 요소 추가
            context.Elements.Remove(target.Eid);
            context.Elements.AddWithID(target.Eid, new List<int> { nA, depNodeId }, propId, barOrientation, extraCopy);
            context.Elements.AddNew(new List<int> { depNodeId, nB }, propId, barOrientation, extraCopy);

            if (opt.VerboseDebug)
              log($"   -> [{target.Dir}] 부재(E{target.Eid})에 수선의 발(N{depNodeId})을 내리고 쪼갰습니다. (거리: {target.Dist:F1}mm)");
          }
        }

        // RBE 종속 노드 일괄 업데이트
        if (newDependentNodes.Count > 0)
        {
          context.Rigids.AppendDependentNodes(rigidId, newDependentNodes);
          processedCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[연결 완료] RBE {rigidId} -> {newDependentNodes.Count}곳(상/하/좌/우) 연결 완료.");
            Console.ResetColor();
          }
        }
        else
        {
          Console.ForegroundColor = ConsoleColor.Red;
          log($"[연결 실패/스킵] RBE {rigidId} (BOX UBOLT) 주위에 연결할 타겟 부재를 찾지 못했습니다. Dependent Node가 빈 상태로 건너뜁니다.");
          Console.ResetColor();
        }
      }

      if (opt.PipelineDebug)
        log($"[수정 완료] 총 {processedCount}개의 BOX UBOLT 처리가 완료되었습니다.\n");

      return processedCount;
    }

    // --- 헬퍼 메서드 ---
    private static Vector3D CrossProduct(Vector3D a, Vector3D b)
    {
      return new Vector3D(
          a.Y * b.Z - a.Z * b.Y,
          a.Z * b.X - a.X * b.Z,
          a.X * b.Y - a.Y * b.X
      );
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
      t = Math.Max(0.0, Math.Min(1.0, t)); // 선분 밖으로 벗어나지 않게 클램핑

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}