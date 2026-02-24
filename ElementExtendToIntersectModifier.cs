using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.ElementInspector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  public static class ElementExtendToIntersectModifier
  {
    public sealed record Options(
        double ExtraMargin = 10.0,
        double CoplanarTolerance = 1.0,
        int MaxIterations = 5,
        bool PipelineDebug = false,
        bool VerboseDebug = false,

        // [추가] 핀포인트 디버깅 옵션 (특정 부재 간의 실패 사유를 추적)
        int? DiagnosticSourceEid = null,
        int? DiagnosticTargetEid = null
    );

    public static void Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var nodes = context.Nodes;
      var elements = context.Elements;
      var properties = context.Properties;

      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      var freeNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();

      int totalExtendedCount = 0;
      int pass = 0;
      bool changedInPass;

      do
      {
        pass++;
        changedInPass = false;
        int passExtendedCount = 0;

        foreach (var eid in elements.Keys.ToList())
        {
          var elem = elements[eid];
          if (elem.NodeIDs.Count < 2) continue;

          int nA = elem.NodeIDs.First();
          int nB = elem.NodeIDs.Last();

          int freeNode = -1;
          int anchorNode = -1;

          if (freeNodes.Contains(nA)) { freeNode = nA; anchorNode = nB; }
          else if (freeNodes.Contains(nB)) { freeNode = nB; anchorNode = nA; }

          if (freeNode == -1) continue;

          var pFree = nodes[freeNode];
          var pAnchor = nodes[anchorNode];

          var rayDir = (pFree - pAnchor).Normalize();

          bool extended = false;
          double bestDist = double.MaxValue;
          Point3D bestHitPoint = default;

          foreach (var targetEid in elements.Keys)
          {
            if (targetEid == eid) continue;

            var targetElem = elements[targetEid];
            if (targetElem.NodeIDs.Count < 2) continue;

            var targetA = nodes[targetElem.NodeIDs.First()];
            var targetB = nodes[targetElem.NodeIDs.Last()];

            double maxDim = GetMaxCrossSectionDim(properties[targetElem.PropertyID]);
            double maxExtendDist = maxDim + opt.ExtraMargin;

            // [진단 모드 확인] 사용자가 지정한 특정 Source-Target 쌍인지 체크
            bool isDiagnostic = (opt.DiagnosticSourceEid == eid && opt.DiagnosticTargetEid == targetEid);

            if (isDiagnostic)
            {
              Console.ForegroundColor = ConsoleColor.Yellow;
              log($"\n==================================================");
              log($"[디버그 진단] Pass {pass}: Source E{eid} -> Target E{targetEid}");
              log($" - 광선 방향 벡터: ({rayDir.X:F3}, {rayDir.Y:F3}, {rayDir.Z:F3})");
              log($" - 최대 허용 연장 길이 (단면 반경 + 마진): {maxExtendDist:F2}");
            }

            // 수학적 결과를 받아오기 위해 out 파라미터 추가 (dist, D)
            bool hit = TryRaySegmentIntersection(pFree, rayDir, targetA, targetB, opt.CoplanarTolerance,
                    out double s, out double t, out Point3D hitPoint, out double dist, out double D);

            if (isDiagnostic)
            {
              log($" [수학 연산 결과]");
              log($"  * 평행도(D): {D:E2} (1e-8 이하면 두 선이 평행하여 만날 수 없음)");
              log($"  * 최단 엇갈림 거리(dist): {dist:F3} (허용치 CoplanarTol: {opt.CoplanarTolerance})");
              log($"  * 광선 연장 거리(s): {s:F3} (허용 범위: 0 ~ {maxExtendDist:F2})");
              log($"  * 타겟 선분 내 위치(t): {t:F3} (허용 범위: 0.0 ~ 1.0)");

              log($" [최종 판별 사유]");
              if (D < 1e-8) log($"  => [실패] 두 부재가 평행(또는 거의 평행)합니다.");
              else if (dist > opt.CoplanarTolerance) log($"  => [실패] 3D 공간상에서 선이 스쳐 지나갑니다 (동일 평면 오차 초과). Z단차 확인 요망.");
              else if (Math.Abs(s) <= 1e-4) log($"  => [실패] 이미 완벽하게 교차되어 있어 이동할 필요가 없습니다.");
              else if (Math.Abs(s) > maxExtendDist) log($"  => [실패] 교차점까지의 거리({Math.Abs(s):F2})가 허용 마진({maxExtendDist:F2})을 초과하여 너무 멉니다.");
              else if (t < -1e-4 || t > 1.0001) log($"  => [실패] 연장/축소선이 타겟 부재의 물리적 길이(0~1)를 벗어난 허공을 찌릅니다.");
              else log($"  => [성공] 교차 조건을 모두 만족하여 연장/축소 후보로 등록됩니다!");

              log($"==================================================\n");
              Console.ResetColor();
            }

            // 실제 교차 판별 로직 적용 (양방향 검사로 업그레이드)
            double absS = Math.Abs(s);

            // s가 음수여도(뒤로 삐져나왔어도) 절댓값이 maxExtendDist 이내라면 합격!
            if (hit && absS > 1e-4 && absS <= maxExtendDist && t >= -1e-4 && t <= 1.0001)
            {
              // 가장 가까운(절댓값이 가장 작은) 교차점을 찾음
              if (absS < bestDist)
              {
                bestDist = absS; // 거리는 절댓값으로 저장
                bestHitPoint = hitPoint;
                extended = true;
              }
            }
          }

          if (extended)
          {
            nodes.AddWithID(freeNode, bestHitPoint.X, bestHitPoint.Y, bestHitPoint.Z);
            passExtendedCount++;
            changedInPass = true;

            if (opt.VerboseDebug && !opt.DiagnosticSourceEid.HasValue)
              log($"   -> [Pass {pass} 연장] E{eid}의 N{freeNode}를 {bestDist:F2} 연장함.");
          }
        }

        totalExtendedCount += passExtendedCount;

      } while (changedInPass && pass < opt.MaxIterations);

      if (opt.PipelineDebug && !opt.DiagnosticSourceEid.HasValue)
      {
        if (totalExtendedCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          Console.WriteLine($"[변경] 1D 부재 연장 연결 : 단면 이격 부재 {totalExtendedCount}개를 연장 접합시켰습니다.");
          Console.ResetColor();
        }
        else
        {
          Console.WriteLine($"[통과] 1D 부재 연장 연결 : 연장이 필요한 부재가 없습니다.");
        }
      }
    }

    private static double GetMaxCrossSectionDim(Property prop)
    {
      var dim = prop.Dim;
      if (dim == null || dim.Count == 0) return 0.0;
      string type = prop.Type.ToUpper();
      return type switch
      {
        "L" => Math.Max(dim.ElementAtOrDefault(0), dim.ElementAtOrDefault(1)),
        "H" => Math.Max(dim.ElementAtOrDefault(0), dim.ElementAtOrDefault(2)),
        "TUBE" => dim.ElementAtOrDefault(0),
        "ROD" => dim.ElementAtOrDefault(0),
        "BAR" => Math.Max(dim.ElementAtOrDefault(0), dim.ElementAtOrDefault(1)),
        "CHAN" => dim.Max(),
        _ => dim.Max()
      };
    }

    // out 파라미터에 dist와 D를 추가하여 디버깅이 가능하도록 개선
    private static bool TryRaySegmentIntersection(
        Point3D rayOrigin, Vector3D rayDir,
        Point3D segA, Point3D segB,
        double coplanarTol,
        out double s, out double t, out Point3D hitPoint, out double dist, out double D)
    {
      s = 0; t = 0; hitPoint = default; dist = double.MaxValue;

      Vector3D u = rayDir;
      Vector3D v = segB - segA;
      Vector3D w = rayOrigin - segA;

      double a = u.Dot(u);
      double b = u.Dot(v);
      double c = v.Dot(v);
      double d = u.Dot(w);
      double e = v.Dot(w);

      D = a * c - b * b;
      const double EPS = 1e-8;

      if (D < EPS) return false;

      s = (b * e - c * d) / D;
      t = (a * e - b * d) / D;

      Point3D pRay = rayOrigin + (u * s);
      Point3D pSeg = segA + (v * t);

      dist = (pRay - pSeg).Magnitude();

      if (dist <= coplanarTol)
      {
        hitPoint = pSeg;
        return true;
      }
      return false;
    }
  }
}
