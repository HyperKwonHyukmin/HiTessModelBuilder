using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 자유단 노드(Free Node)를 탐색하여 지정된 4가지 조건을 모두 만족할 경우 
  /// 대상 부재(ElementA)와의 교점(Intersection)으로 노드를 이동시킵니다.
  /// </summary>
  public static class ElementExtendToIntersectModifier
  {
    public sealed record Options(
        double ExtraMargin = 10.0,      // SearchDim에 추가할 여유 거리 마진
        double CoplanarTolerance = 1.0, // 3D 공간상의 교차(Hit)를 인정할 선분 간 최소 단차 오차
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var nodes = context.Nodes;
      var elements = context.Elements;
      var properties = context.Properties;

      // [Condition 2] 단 1번만 사용된 FreeNode(Degree=1) 찾기
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      var freeNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();

      // FreeNode가 속한 ElementB 매핑
      var freeNodeToElement = new Dictionary<int, int>();
      foreach (var kv in elements)
      {
        foreach (var nid in kv.Value.NodeIDs)
        {
          if (freeNodes.Contains(nid))
          {
            freeNodeToElement[nid] = kv.Key;
          }
        }
      }

      int movedNodesCount = 0;

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] ElementExtendToIntersectModifier (4가지 조건 엄격 매핑)");
        log($" -> 탐색 대상 Free Node 개수: {freeNodes.Count}개");
        log($"==================================================\n");
      }

      foreach (var freeNodeId in freeNodes)
      {
        // ElementB (FreeNode를 소유한 부재) 정보 획득
        int elemB_Id = freeNodeToElement[freeNodeId];
        var elemB = elements[elemB_Id];

        // ElementB의 고정단(Anchor) 찾기
        int anchorNodeId = elemB.NodeIDs.First() == freeNodeId
                         ? elemB.NodeIDs.Last()
                         : elemB.NodeIDs.First();

        var pFree = nodes[freeNodeId];
        var pAnchor = nodes[anchorNodeId];

        // ElementB가 가지는 방향 벡터 (Anchor -> Free 방향)
        var rayDir = (pFree - pAnchor).Normalize();

        double bestS = double.MaxValue;
        Point3D bestHitPoint = default;
        int bestTargetEid = -1;
        double bestDistToTarget = 0;

        // 모든 부재를 순회하며 ElementA(타겟 부재) 후보 찾기
        foreach (var elemA_Id in elements.Keys)
        {
          if (elemA_Id == elemB_Id) continue;
          
          var elemA = elements[elemA_Id];
          if (elemA.NodeIDs.Count < 2) continue;
          if (elemA.NodeIDs.Contains(freeNodeId)) continue; 

          var pA1 = nodes[elemA.NodeIDs.First()];
          var pA2 = nodes[elemA.NodeIDs.Last()];

          // ElementA의 SearchDim 계산
          var propertyA = properties[elemA.PropertyID];
          double searchDimA = PropertyDimensionHelper.GetMaxCrossSectionDim(propertyA);
          double totalSearchRadiusA = searchDimA + opt.ExtraMargin;

          // [Condition 1] ElementA의 SearchDim 영역 이내에 해당 Node가 물리적으로 존재하는가?
          double distNodeToElementA = DistancePointToSegment(pFree, pA1, pA2);
          if (distNodeToElementA > totalSearchRadiusA) 
          {
              continue; // 영역 밖이면 아예 연장(교차) 검사를 하지 않음
          }

          // [Condition 3] ElementB의 방향벡터로 연장하여 ElementA와 만나서 교점을 이루는가?
          // (병렬 강제 이동 방지: 점이 자기 축을 이탈하여 이동하지 않고 오직 rayDir로만 이동함)
          bool isHit = TryRaySegmentIntersection(
              pFree, rayDir, pA1, pA2, opt.CoplanarTolerance,
              out double s, out double t, out double distRayToSeg, out Point3D hitPoint);

          // 교차가 성공했고, 연장 거리가 앞으로 향하며(s > 1e-4), 다른 부재보다 더 가깝다면 갱신
          if (isHit && s > 1e-4 && s < bestS)
          {
            bestS = s;
            bestHitPoint = hitPoint;
            bestTargetEid = elemA_Id;
            bestDistToTarget = distNodeToElementA; // 조건1에서 통과된 실제 물리적 거리 기록
          }
        }

        // [Condition 4] 위 조건을 만족하는 최적의 교점을 찾았다면 ElementA와 만나는 교점으로 이동
        if (bestTargetEid != -1)
        {
          nodes.AddWithID(freeNodeId, bestHitPoint.X, bestHitPoint.Y, bestHitPoint.Z);
          movedNodesCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[노드 이동 완료] Free Node N{freeNodeId}가 ElementA(E{bestTargetEid})와 교차하여 이동했습니다.");
            Console.ResetColor();
            log($"   - Source 부재(ElementB): E{elemB_Id}");
            log($"   - Target 부재(ElementA): E{bestTargetEid}");
            log($"   - [조건1] 탐색 반경 진입 여부 : 통과 (ElementA까지의 거리 {bestDistToTarget:F2} <= 허용치)");
            log($"   - [조건3] 방향 벡터 연장 거리(s): {bestS:F2} (해당 축방향 그대로 전진)");
            log($"   - 변경된 Node 좌표: ({bestHitPoint.X:F1}, {bestHitPoint.Y:F1}, {bestHitPoint.Z:F1})\n");
          }
        }
      }

      if (opt.PipelineDebug)
      {
        if (movedNodesCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            log($"[수정 완료] 총 {movedNodesCount}개의 Free Node가 조건에 맞춰 교차점으로 이동되었습니다.\n");
            Console.ResetColor();
        }
        else
        {
            log($"[수정 완료] 연장 조건을 만족하여 이동된 노드가 없습니다.\n");
        }
      }
      return movedNodesCount;
      
    }

    /// <summary>
    /// 점 P와 선분 AB 사이의 최단 거리를 계산합니다. (영역 진입 검사용 - Condition 1)
    /// </summary>
    private static double DistancePointToSegment(Point3D p, Point3D a, Point3D b)
    {
        var ab = b - a; 
        var ap = p - a; 

        double lengthSq = ab.Dot(ab);
        if (lengthSq < 1e-12) return (p - a).Magnitude(); 

        double t = ap.Dot(ab) / lengthSq;
        t = Math.Max(0.0, Math.Min(1.0, t));

        var proj = a + (ab * t);
        return (p - proj).Magnitude();
    }

    /// <summary>
    /// Ray(광선)와 Segment(선분) 사이의 3D 공간상 최단 거리와 교차 지점을 반환합니다. (방향 연장 검사용 - Condition 3)
    /// </summary>
    private static bool TryRaySegmentIntersection(
        Point3D rayOrigin, Vector3D rayDir,
        Point3D segA, Point3D segB,
        double tolerance,
        out double s, out double t, out double dist, out Point3D hitPoint)
    {
      s = 0; t = 0; dist = double.MaxValue; hitPoint = default;

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

      if (D < EPS) return false;

      s = (b * e - c * d) / D;
      t = (a * e - b * d) / D;

      if (t < 0.0) t = 0.0;
      else if (t > 1.0) t = 1.0;

      s = (t * b - d) / a;

      Point3D pRay = rayOrigin + (u * s);
      Point3D pSeg = segA + (v * t);

      dist = (pRay - pSeg).Magnitude();

      if (dist <= tolerance)
      {
        hitPoint = pSeg;
        return true;
      }

      return false;
    }
  }
}
