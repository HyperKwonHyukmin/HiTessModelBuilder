using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.NodeInspector;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 6: 지능형 강체 그룹 병진 이동]
  /// Master와 떨어진 Slave 그룹들을 이동시키되, 근접도와 이동 방향의 일관성을 검사하여 일괄 이동시킵니다.
  /// (+추가: 단일 부재 중 치수가 작고 짧은 찌꺼기(Noise)만 선별하여 삭제합니다.)
  /// (+추가: 주요 치수(길이/단면)를 만족하는 대형 단일 부재는 삭제를 면제하고 이동시킵니다.)
  /// </summary>
  public static class ElementGroupTranslationModifier
  {
    public sealed record Options(
        double ExtraMargin = 50.0,             // Master 탐색 허용 반경
        double LocalTolerance = 40.0,          // Slave 그룹 간 사전 통합 허용 거리

        // ★ [신규 추가] 노이즈 판별 임계값 (이 수치들을 넘으면 삭제 면제)
        double ImportantLengthThreshold = 500.0, // 길이 500mm 이상이면 주요 부재로 간주
        double ImportantDimThreshold = 100.0,    // 단면의 최대 치수가 100mm 이상이면 주요 부재로 간주

        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var elements = context.Elements;
      var nodes = context.Nodes;

      // 1. 물리적 연결 기반 기본 그룹 추출
      var initialGroups = ElementConnectivityInspector.FindConnectedElementGroups(elements);

      // ====================================================================
      // ★ 노이즈 제거: 단일 요소(1개) 고립 그룹 중 "크기가 작은 찌꺼기"만 선별 삭제
      // ====================================================================
      var validGroups = new List<List<int>>();
      int removedSingleElementCount = 0;

      foreach (var group in initialGroups)
      {
        if (group.Count == 1)
        {
          int eid = group[0];
          if (context.Elements.Contains(eid))
          {
            var e = context.Elements[eid];
            bool isPipe = e.ExtraData != null &&
                          (e.ExtraData.GetValueOrDefault("Classification") == "Pipe" ||
                           e.ExtraData.GetValueOrDefault("Category") == "Pipe");

            if (!isPipe)
            {
              // [방어 로직 1] 부재 길이 측정
              int n1 = e.NodeIDs.First();
              int n2 = e.NodeIDs.Last();
              double length = (nodes[n1] - nodes[n2]).Magnitude();

              // [방어 로직 2] 부재 단면의 최대 치수 측정
              double maxDim = 0.0;
              if (context.Properties.TryGetValue(e.PropertyID, out var prop) && prop.Dim != null && prop.Dim.Count > 0)
              {
                maxDim = prop.Dim.Max(); // (예: 176, 24, 200, 8 -> 200 추출)
              }

              // [핵심] 길이와 단면 모두 임계치 미만일 때만 '진짜 찌꺼기'로 판정하여 삭제
              if (length < opt.ImportantLengthThreshold && maxDim < opt.ImportantDimThreshold)
              {
                string rawName = e.ExtraData?.GetValueOrDefault("ID") ?? e.ExtraData?.GetValueOrDefault("Name") ?? "Unknown";
                context.Elements.Remove(eid);
                removedSingleElementCount++;

                if (opt.VerboseDebug)
                  log($"   -> [노이즈 영구 삭제] 허공의 소형 찌꺼기 부재 '{rawName}'(E{eid}) 삭제 (L={length:F1}, MaxDim={maxDim:F1})");

                continue; // 삭제되었으므로 유효 그룹에 넣지 않고 건너뜀
              }
              else
              {
                // 중요 부재로 인정받아 보존됨!
                if (opt.VerboseDebug)
                {
                  string rawName = e.ExtraData?.GetValueOrDefault("ID") ?? e.ExtraData?.GetValueOrDefault("Name") ?? "Unknown";
                  log($"   -> [보존] 고립된 단일 부재지만 중요 치수 초과로 보존됨: '{rawName}'(E{eid}) (L={length:F1}, MaxDim={maxDim:F1})");
                }
              }
            }
          }
        }

        // 찌꺼기가 아니거나(보존된 주요 부재), 요소 2개 이상이거나, 배관인 경우 유효 그룹에 추가
        validGroups.Add(group);
      }

      if (opt.PipelineDebug && removedSingleElementCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Magenta;
        log($"[정리] 찌꺼기 요소 제거 : 병진 이동 전, 의미 없는 소형 단일 부재 {removedSingleElementCount}개를 영구 삭제했습니다.");
        Console.ResetColor();
      }

      if (validGroups.Count <= 1) return 0;
      // ====================================================================

      var sorted = validGroups.OrderByDescending(g => g.Count).ToList();
      var masterElementIds = new HashSet<int>(sorted[0]);
      var initialSlaves = sorted.Skip(1).ToList();

      // 배관(Pipe)이 포함된 그룹 이동 제외 (정위치 보존)
      var filteredSlaves = new List<List<int>>();
      foreach (var group in initialSlaves)
      {
        bool isPipeGroup = group.Any(eid =>
        {
          if (!context.Elements.Contains(eid)) return false;
          var e = context.Elements[eid];
          return e.ExtraData != null &&
                 (e.ExtraData.GetValueOrDefault("Classification") == "Pipe" ||
                  e.ExtraData.GetValueOrDefault("Category") == "Pipe");
        });

        if (!isPipeGroup) filteredSlaves.Add(group);
      }

      if (filteredSlaves.Count == 0) return 0;

      // 2. 방향성 및 근접도 기반 사전 그룹화
      var metaSlaveGroups = PreGroupSlavesByProximityAndIntent(context, filteredSlaves, masterElementIds, opt);

      int translatedCount = 0;
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);

      // 3. 검증된 Meta-Group 이동 처리
      foreach (var slaveGroup in metaSlaveGroups)
      {
        var (vector, sourceNode, targetEid, dist) = CalculateBestMove(context, slaveGroup, masterElementIds, nodeDegree, opt);

        if (targetEid != -1 && dist > 1e-4)
        {
          var allNodesInGroup = GetAllNodesInElements(elements, slaveGroup);
          foreach (var nid in allNodesInGroup)
          {
            var p = nodes[nid];
            nodes.AddWithID(nid, p.X + vector.X, p.Y + vector.Y, p.Z + vector.Z);
          }
          translatedCount++;
        }
      }

      return translatedCount;
    }

    private static List<List<int>> PreGroupSlavesByProximityAndIntent(
        FeModelContext context, List<List<int>> slaves, HashSet<int> masterIds, Options opt)
    {
      if (slaves.Count <= 1) return slaves;

      var slaveIntents = slaves.Select(s => new {
        Elements = s,
        Vector = CalculateCandidateVector(context, s, masterIds, opt)
      }).ToList();

      var uf = new UnionFind(Enumerable.Range(0, slaves.Count).ToList());

      for (int i = 0; i < slaveIntents.Count; i++)
      {
        for (int j = i + 1; j < slaveIntents.Count; j++)
        {
          double dist = GetDistanceBetweenGroups(context, slaveIntents[i].Elements, slaveIntents[j].Elements);
          if (dist > opt.LocalTolerance) continue;

          double dot = slaveIntents[i].Vector.Dot(slaveIntents[j].Vector);
          if (dot > 0) uf.Union(i, j);
        }
      }

      return uf.GetClusters().Values.Select(indices =>
          indices.SelectMany(idx => slaveIntents[idx].Elements).ToList()
      ).ToList();
    }

    private static Vector3D CalculateCandidateVector(FeModelContext context, List<int> slaveElements, HashSet<int> masterIds, Options opt)
    {
      var (vec, _, target, _) = CalculateBestMove(context, slaveElements, masterIds, NodeDegreeInspector.BuildNodeDegree(context), opt);
      return target == -1 ? new Vector3D(0, 0, 0) : vec.Normalize();
    }

    private static (Vector3D vec, int srcNode, int targetEid, double dist) CalculateBestMove(
        FeModelContext context, List<int> group, HashSet<int> masters, Dictionary<int, int> degrees, Options opt)
    {
      double bestDist = double.MaxValue;
      Vector3D bestVec = default;
      int bestSrc = -1;
      int bestTarget = -1;

      var freeNodes = group.SelectMany(eid => context.Elements[eid].NodeIDs)
                           .Distinct().Where(nid => degrees.GetValueOrDefault(nid) == 1);

      foreach (var fnid in freeNodes)
      {
        var pFree = context.Nodes[fnid];
        foreach (var meid in masters)
        {
          var masterElem = context.Elements[meid];
          double dist = DistancePointToSegment(pFree, context.Nodes[masterElem.NodeIDs[0]], context.Nodes[masterElem.NodeIDs[1]], out Point3D proj);

          // 이 부분에서 Property 참조 오류를 방지하기 위해 TryGetValue 사용 권장
          double maxCrossSection = 0.0;
          if (context.Properties.TryGetValue(masterElem.PropertyID, out var prop))
          {
            maxCrossSection = PropertyDimensionHelper.GetMaxCrossSectionDim(prop);
          }

          if (dist < bestDist && dist <= (maxCrossSection + opt.ExtraMargin))
          {
            bestDist = dist;
            bestVec = proj - pFree;
            bestSrc = fnid;
            bestTarget = meid;
          }
        }
      }
      return (bestVec, bestSrc, bestTarget, bestDist);
    }

    private static HashSet<int> GetAllNodesInElements(Elements elements, List<int> eids)
        => new HashSet<int>(eids.SelectMany(eid => elements[eid].NodeIDs));

    private static double GetDistanceBetweenGroups(FeModelContext context, List<int> g1, List<int> g2)
    {
      var nodes1 = g1.SelectMany(e => context.Elements[e].NodeIDs).Distinct();
      var nodes2 = g2.SelectMany(e => context.Elements[e].NodeIDs).Distinct();
      return nodes1.Min(n1 => nodes2.Min(n2 => (context.Nodes[n1] - context.Nodes[n2]).Magnitude()));
    }

    private static double DistancePointToSegment(Point3D p, Point3D a, Point3D b, out Point3D projPoint)
    {
      var ab = b - a; var ap = p - a;
      double lenSq = ab.Dot(ab);
      if (lenSq < 1e-12) { projPoint = a; return (p - a).Magnitude(); }
      double t = Math.Max(0, Math.Min(1, ap.Dot(ab) / lenSq));
      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}