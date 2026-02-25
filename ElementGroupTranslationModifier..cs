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
  /// [Stage 6: 강체 그룹 병진 이동]
  /// 메인 구조물(Master)과 떨어져 있는 독립된 부재 그룹(Slave)을 찾아,
  /// 형상의 왜곡 없이 그룹 전체를 병진 이동(Translation)시켜 Master 부재에 스냅합니다.
  /// </summary>
  public static class ElementGroupTranslationModifier
  {
    public sealed record Options(
        double ExtraMargin = 50.0, // 그룹 간 오프셋 탐색 허용 여유 반경
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var elements = context.Elements;
      var nodes = context.Nodes;
      var properties = context.Properties;

      int translatedGroupCount = 0;

      // 1. 위상 연결 그룹 추출 (ElementConnectivityInspector 활용)
      var groups = ElementConnectivityInspector.FindConnectedElementGroups(elements);

      if (groups.Count <= 1)
      {
        if (opt.PipelineDebug) log("[통과] 모든 요소가 하나의 그룹으로 연결되어 있어 그룹 병진 이동이 필요 없습니다.");
        return 0;
      }

      // 2. 가장 큰 그룹을 Master로 간주, 나머지를 Slave로 분리
      var sortedGroups = groups.OrderByDescending(g => g.Count).ToList();
      var masterGroup = sortedGroups.First();
      var slaveGroups = sortedGroups.Skip(1).ToList();

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] ElementGroupTranslationModifier (강체 그룹 병진 이동)");
        log($" -> 전체 그룹 수: {groups.Count} (Master 1개, Slave {slaveGroups.Count}개)");
        log($"==================================================\n");
      }

      // 전체 노드의 연결 차수 (Free Node 판별용)
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);

      // 빠른 검색을 위한 Master 요소 HashSet
      var masterElementIds = new HashSet<int>(masterGroup);

      // 3. 각 Slave 그룹을 순회하며 일괄 이동 처리
      foreach (var slaveGroup in slaveGroups)
      {
        // Slave 그룹에 속한 요소와 고유 노드 수집
        var slaveNodeIds = new HashSet<int>();
        var slaveFreeNodes = new List<int>();

        foreach (var eid in slaveGroup)
        {
          if (!elements.Contains(eid)) continue;
          foreach (var nid in elements[eid].NodeIDs)
          {
            slaveNodeIds.Add(nid);
          }
        }

        // Slave 내부의 노드 중 전체 컨텍스트에서 Degree가 1인 노드(Free Node) 추출
        foreach (var nid in slaveNodeIds)
        {
          if (nodeDegree.TryGetValue(nid, out int deg) && deg == 1)
          {
            slaveFreeNodes.Add(nid);
          }
        }

        if (slaveFreeNodes.Count == 0) continue; // 붙일 기준(Free Node)이 없으면 패스

        double bestDist = double.MaxValue;
        Vector3D bestTranslationVector = default;
        int bestSourceNode = -1;
        int bestTargetElement = -1;

        // 4. Slave의 각 Free Node에 대해 가장 가까운 Master 요소 탐색
        foreach (var freeNodeId in slaveFreeNodes)
        {
          var pFree = nodes[freeNodeId];

          foreach (var masterEid in masterElementIds)
          {
            if (!elements.Contains(masterEid)) continue;
            var masterElem = elements[masterEid];
            if (masterElem.NodeIDs.Count < 2) continue;

            var pA = nodes[masterElem.NodeIDs.First()];
            var pB = nodes[masterElem.NodeIDs.Last()];

            var prop = properties[masterElem.PropertyID];
            double searchDim = PropertyDimensionHelper.GetMaxCrossSectionDim(prop);
            double allowedDist = searchDim + opt.ExtraMargin;

            // 점과 선분 사이의 최단 거리 및 투영점 계산
            double dist = DistancePointToSegment(pFree, pA, pB, out Point3D projPoint);

            // 허용 거리 내에 들어오고, 기존에 찾은 것보다 더 가깝다면 갱신
            if (dist <= allowedDist && dist < bestDist)
            {
              bestDist = dist;
              bestTranslationVector = projPoint - pFree; // 이동해야 할 벡터
              bestSourceNode = freeNodeId;
              bestTargetElement = masterEid;
            }
          }
        }

        // 5. 최적의 타겟을 찾았다면 Slave 그룹 전체 노드를 일괄 이동 (형상 유지)
        if (bestTargetElement != -1 && bestDist > 1e-4)
        {
          foreach (var nid in slaveNodeIds)
          {
            var p = nodes[nid];
            var newP = p + bestTranslationVector; // 모든 노드에 동일 벡터 적용

            // 기존 ID를 유지한 채 좌표만 덮어쓰기 (Nodes 컬렉션의 AddWithID 활용)
            nodes.AddWithID(nid, newP.X, newP.Y, newP.Z);
          }

          translatedGroupCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[그룹 이동 완료] Slave 그룹(요소 {slaveGroup.Count}개)이 통째로 이동하여 Master E{bestTargetElement}에 스냅되었습니다.");
            Console.ResetColor();
            log($"   - 앵커(선봉) 노드: N{bestSourceNode}");
            log($"   - 일괄 이동 벡터: ({bestTranslationVector.X:F1}, {bestTranslationVector.Y:F1}, {bestTranslationVector.Z:F1})");
            log($"   - 병진 이동 거리: {bestDist:F2}\n");
          }
        }
      }

      if (opt.PipelineDebug)
      {
        if (translatedGroupCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          log($"[수정 완료] 총 {translatedGroupCount}개의 Slave 그룹이 병진 이동되어 Master에 스냅되었습니다.\n");
          Console.ResetColor();
        }
        else
        {
          log($"[수정 완료] 강체 병진 이동이 필요한 그룹이 없습니다.\n");
        }
      }

      return translatedGroupCount;
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
      t = Math.Max(0.0, Math.Min(1.0, t));

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}
