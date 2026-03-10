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
      if (initialGroups.Count <= 1) return 0;

      var sorted = initialGroups.OrderByDescending(g => g.Count).ToList();
      var masterElementIds = new HashSet<int>(sorted[0]);
      var initialSlaves = sorted.Skip(1).ToList();

      // 2. [핵심 예방 조치] 방향성 및 근접도 기반 사전 그룹화 실행
      var metaSlaveGroups = PreGroupSlavesByProximityAndIntent(context, initialSlaves, masterElementIds, opt);

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

      // 각 Slave 그룹의 대표 이동 벡터 미리 계산
      var slaveIntents = slaves.Select(s => new {
        Elements = s,
        Vector = CalculateCandidateVector(context, s, masterIds, opt)
      }).ToList();

      // Union-Find를 통한 안전한 그룹 병합
      var uf = new UnionFind(Enumerable.Range(0, slaves.Count).ToList());

      for (int i = 0; i < slaveIntents.Count; i++)
      {
        for (int j = i + 1; j < slaveIntents.Count; j++)
        {
          double dist = GetDistanceBetweenGroups(context, slaveIntents[i].Elements, slaveIntents[j].Elements);

          // [예방 조치 1] 근접성 확인
          if (dist > opt.LocalTolerance) continue;

          // [예방 조치 2] 벡터 일관성 확인 (내적)
          // 방향이 반대(내적 < 0)면 아무 가까워도 묶지 않음 (D-A-B 상황 방지)
          double dot = slaveIntents[i].Vector.Dot(slaveIntents[j].Vector);
          if (dot > 0)
          {
            uf.Union(i, j);
          }
        }
      }

      // 병합된 결과 재구성
      return uf.GetClusters().Values.Select(indices =>
          indices.SelectMany(idx => slaveIntents[idx].Elements).ToList()
      ).ToList();
    }

    /// <summary>
    /// 특정 그룹이 Master에 붙기 위해 필요한 잠재적 이동 벡터를 계산합니다.
    /// </summary>
    private static Vector3D CalculateCandidateVector(FeModelContext context, List<int> slaveElements, HashSet<int> masterIds, Options opt)
    {
      // 가장 가까운 Master 지점을 찾아 방향만 도출
      var (vec, _, target, _) = CalculateBestMove(context, slaveElements, masterIds, NodeDegreeInspector.BuildNodeDegree(context), opt);
      return target == -1 ? new Vector3D(0, 0, 0) : vec.Normalize();
    }

    // --- 이하 보조 헬퍼 메서드 (기존 로직 유지 및 개선) ---

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
      // 간단한 최소 거리 측정 (성능 필요 시 바운딩 박스 사용 가능)
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
