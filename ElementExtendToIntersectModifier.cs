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
  /// [Step 2: Ray-Casting 방향 충돌 검증]
  /// 자유단 노드(Free Node)가 속한 부재(ElementA)의 방향을 연장했을 때,
  /// 타겟 부재(ElementB)의 SearchDim 반경 이내로 교차(Intersect)하는 경우만 찾아 로그로 출력합니다.
  /// </summary>
  public static class ElementExtendToIntersectModifier
  {
    public sealed record Options(
        double ExtraMargin = 10.0, // 단면 치수 외에 추가로 탐색할 여유 거리 마진
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static void Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var nodes = context.Nodes;
      var elements = context.Elements;
      var properties = context.Properties;

      // 1. 자유단 노드(Degree = 1) 목록 추출
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      var freeNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();

      // 빠른 탐색을 위해 [자유단 노드 ID -> 해당 노드가 속한 Element ID] 맵핑 테이블 생성
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

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[탐색 시작] ElementExtendToIntersectModifier (Step 2: 방향 충돌 검사)");
        log($" -> 대상 Free Node 개수: {freeNodes.Count}개");
        log($"==================================================\n");
      }

      // 2. 타겟 부재(ElementB) 순회
      foreach (var targetEid in elements.Keys)
      {
        var targetElem = elements[targetEid];
        var propertyID = targetElem.PropertyID;
        var property = properties[propertyID];
        if (targetElem.NodeIDs.Count < 2) continue;

        int targetnA = targetElem.NodeIDs.First();
        int targetnB = targetElem.NodeIDs.Last();
        var pTargetA = nodes[targetnA];
        var pTargetB = nodes[targetnB];

        // 타겟 부재의 탐색 반경 계산 (단면 치수 + 마진)
        double searchDim = PropertyDimensionHelper.GetMaxCrossSectionDim(property);
        double totalSearchRadius = searchDim + opt.ExtraMargin;

        // 3. 각 자유단 노드(ElementA의 끝점)에서 Ray를 쏴서 충돌하는지 확인
        foreach (var freeNodeId in freeNodes)
        {
          // 본인이 속한 부재 검사는 건너뜀
          if (targetElem.NodeIDs.Contains(freeNodeId)) continue;

          // ElementA 정보 획득
          int sourceEid = freeNodeToElement[freeNodeId];
          var sourceElem = elements[sourceEid];

          // ElementA의 반대쪽 고정단(Anchor) 노드 찾기
          int anchorNodeId = sourceElem.NodeIDs.First() == freeNodeId
                           ? sourceElem.NodeIDs.Last()
                           : sourceElem.NodeIDs.First();

          var pFree = nodes[freeNodeId];
          var pAnchor = nodes[anchorNodeId];

          // ElementA의 뻗어나가는 방향 벡터 (Anchor -> Free 방향)
          var rayDir = (pFree - pAnchor).Normalize();

          // 4. Ray vs Segment 충돌 수학 계산
          bool isHit = TryRaySegmentIntersection(
              pFree, rayDir, pTargetA, pTargetB, totalSearchRadius,
              out double s, out double t, out double dist, out Point3D hitPoint);

          // 5. 충돌 조건 판별
          // - isHit : 3D 공간상의 최단거리가 totalSearchRadius 이내인가?
          // - s > 1e-4 : 뒤로 가지 않고 앞으로 뻗어나가는가? (연장 거리)
          // - s <= totalSearchRadius * 3 : (선택사항) 너무 멀리 있는 걸 잡지 않기 위한 1차 필터링
          if (isHit && s > 1e-4)
          {
       
            Console.ForegroundColor = ConsoleColor.Cyan;
            log($"[충돌 적중] ElementA(E{sourceEid}) 연장 시 ElementB(E{targetEid})와 만납니다.");
            Console.ResetColor();
            log($"   - Source: E{sourceEid} (Free Node: N{freeNodeId})");
            log($"   - Target: E{targetEid}");
            log($"   - 방향 벡터(Ray)   : ({rayDir.X:F3}, {rayDir.Y:F3}, {rayDir.Z:F3})");
            log($"   - 충돌까지의 연장 길이(s) : {s:F2}");
            log($"   - 3D 공간 엇갈림 단차(dist): {dist:F2} (허용 반경: {totalSearchRadius:F2})");
            log($"   - Target 부재 내 위치비율(t): {t:F3} (0~1 사이면 부재 중간을 때린 것)\n");
            
          }
        }
      }

      if (opt.PipelineDebug)
      {
        log($"[탐색 완료] Step 2 종료\n");
      }
    }

    /// <summary>
    /// Ray(광선)와 Segment(선분) 사이의 3D 공간상 최단 거리와 교차 지점을 반환합니다.
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

      double a = u.Dot(u); // 방향벡터이므로 1
      double b = u.Dot(v);
      double c = v.Dot(v);
      double d = u.Dot(w);
      double e = v.Dot(w);

      double D = a * c - b * b;
      const double EPS = 1e-8;

      // 두 선이 평행한 경우
      if (D < EPS) return false;

      // 매개변수 s(Ray 거리), t(Segment 위치 비율) 계산
      s = (b * e - c * d) / D;
      t = (a * e - b * d) / D;

      // 선분(Segment)의 물리적 한계인 0.0 ~ 1.0 안으로 t를 묶어둠 (클램핑)
      if (t < 0.0) t = 0.0;
      else if (t > 1.0) t = 1.0;

      // t가 클램핑되었다면 s를 다시 계산 (가장 가까운 투영점 탐색)
      s = (t * b - d) / a;

      // 각 매개변수를 통해 Ray 위, Segment 위 점을 구함
      Point3D pRay = rayOrigin + (u * s);
      Point3D pSeg = segA + (v * t);

      // 두 점 사이의 최단 거리
      dist = (pRay - pSeg).Magnitude();

      // 거리가 허용 반경 내에 들어오면 Hit으로 판정!
      if (dist <= tolerance)
      {
        hitPoint = pSeg;
        return true;
      }

      return false;
    }
  }
}
