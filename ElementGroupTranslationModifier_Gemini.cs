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
  /// Master와 떨어진 Slave 그룹들을 이동시키되, 근접도와 이동 방향의 일관성을 검사하여
  /// 역방향 이동으로 인한 연결성 파괴를 예방하며 일괄 이동시킵니다.
  /// (+추가: 단일 부재로 이루어진 고립 그룹은 노이즈로 간주하여 사전에 삭제합니다.)
  /// (+추가: 배관(Pipe) 요소는 설계 정위치 유지를 위해 이동 대상에서 완벽히 제외됩니다.)
  /// </summary>
  public static class ElementGroupTranslationModifier
  {
    public sealed record Options(
        double ExtraMargin = 50.0,      // Master 탐색 허용 반경
        double LocalTolerance = 40.0,   // Slave 그룹 간 사전 통합 허용 거리
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
      // ★ [신규 추가] 노이즈 제거: 단일 요소(1개)로만 이루어진 고립 그룹 자동 삭제
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
              // ★ [사각지대 2] Name 추출 및 조건문 없이 무조건 출력하도록 변경
              string rawName = e.ExtraData?.GetValueOrDefault("ID") ?? e.ExtraData?.GetValueOrDefault("Name") ?? "Unknown";

              context.Elements.Remove(eid);
              removedSingleElementCount++;

      
              Console.ForegroundColor = ConsoleColor.Yellow;
              if (opt.VerboseDebug)
                log($"   -> [영구 삭제] 허공에 고립된 단일 찌꺼기 부재 '{rawName}'(E{eid}) 삭제됨.");
              Console.ResetColor();

              continue;
            }
          }
        }
        // 노이즈가 아니거나(2개 이상), 배관(Pipe)인 경우 유효 그룹으로 보존
        validGroups.Add(group);
      }

      if (opt.PipelineDebug && removedSingleElementCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Magenta;
        if (opt.VerboseDebug)
          log($"[정리] 고립 요소 제거 : 병진 이동(Case 5) 전, 의미 없는 단일 찌꺼기 부재 {removedSingleElementCount}개를 영구 삭제했습니다.");
        Console.ResetColor();
      }

      // 유효한 그룹이 1개 이하로 남았다면 병진 이동할 대상이 없으므로 종료
      if (validGroups.Count <= 1) return 0;
      // ====================================================================

      var sorted = validGroups.OrderByDescending(g => g.Count).ToList();
      var masterElementIds = new HashSet<int>(sorted[0]);
      var initialSlaves = sorted.Skip(1).ToList();

      // ★ [기존 로직 유지] 배관(Pipe)이 포함된 그룹은 강제 이동(Translation) 대상에서 완전 제외 (정위치 보존)
      var filteredSlaves = new List<List<int>>();
      foreach (var group in initialSlaves)
      {
        bool isPipeGroup = group.Any(eid =>
        {
          if (!context.Elements.Contains(eid)) return false;
          var e = context.Elements[eid];
          if (e.ExtraData == null) return false;

          bool hasClass = e.ExtraData.TryGetValue("Classification", out string cls) && cls == "Pipe";
          bool hasCat = e.ExtraData.TryGetValue("Category", out string cat) && cat == "Pipe";

          return hasClass || hasCat;
        });

        if (!isPipeGroup)
        {
          filteredSlaves.Add(group); // 순수 구조물(Stru) 그룹만 이동 후보로 추가
        }
        else if (opt.VerboseDebug)
        {
          log($"   -> [보존] 배관(Pipe)이 포함된 그룹(요소 {group.Count}개)은 정위치 지를 위해 이동에서 제외됩니다.");
        }
      }

      if (filteredSlaves.Count == 0) return 0;

      // 2. 방향성 및 근접도 기반 사전 그룹화 실행 (필터링된 그룹만 넘김)
      var metaSlaveGroups = PreGroupSlavesByProximityAndIntent(context, filteredSlaves, masterElementIds, opt);

      int translatedCount = 0;
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);

      // 3. 검증된 Meta-Group 단위로 이동 처리
      foreach (var slaveGroup in metaSlaveGroups)
      {
        var (vector, sourceNode, targetEid, dist) = CalculateBestMove(context, slaveGroup, masterElementIds, nodeDegree, opt);

        if (targetEid != -1 && dist > 1e-4)
        {
          // 그룹 내 모든 노드 수집 및 일괄 이동
          var allNodesInGroup = GetAllNodesInElements(elements, slaveGroup);
          foreach (var nid in allNodesInGroup)
          {
            var p = nodes[nid];
            nodes.AddWithID(nid, p.X + vector.X, p.Y + vector.Y, p.Z + vector.Z);
          }
          translatedCount++;

          if (opt.VerboseDebug)
            log($"[이동 성공] Meta-Group(요소 {slaveGroup.Count}개) -> 벡터({vector.X:F1}, {vector.Y:F1}, {vector.Z:F1}) 적용");
        }
      }

      return translatedCount;
    }

    /// <summary>
    /// Slave 그룹들 간의 거리와 Master 방향 벡터를 비교하여, 같은 방향으로 움직여야 하는 그룹들만 하나로 묶습니다.
    /// </summary>
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
          if (dot > 0)
          {
            uf.Union(i, j);
          }
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

          if (dist < bestDist && dist <= (PropertyDimensionHelper.GetMaxCrossSectionDim(context.Properties[masterElem.PropertyID]) + opt.ExtraMargin))
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
