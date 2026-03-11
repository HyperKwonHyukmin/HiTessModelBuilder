using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.NodeInspector;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 6: 강체 그룹 병진 이동]
  /// 메인 구조물(Master)과 떨어져 있는 독립된 덩어리(Slave)를 찾아,
  /// 형상의 왜곡 없이 덩어리 전체(Element + Rigid + Mass)를 병진 이동(Translation)시켜 스냅합니다.
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

      // 1. [핵심 개선] Element와 Rigid를 모두 포함한 완벽한 '노드 단위 덩어리' 추출
      var nodeGroups = FindConnectedNodeGroups(context);

      if (nodeGroups.Count <= 1)
      {
        if (opt.PipelineDebug) log("[통과] 모든 노드가 물리적으로 연결되어 있어 그룹 병진 이동이 필요 없습니다.");
        return 0;
      }

      // 2. 가장 큰 덩어리를 Master로 간주, 나머지를 Slave로 분리
      var sortedGroups = nodeGroups.OrderByDescending(g => g.Count).ToList();
      var masterNodeIds = sortedGroups.First();
      var slaveGroups = sortedGroups.Skip(1).ToList();

      if (opt.PipelineDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] ElementGroupTranslationModifier (덩어리 일괄 병 이동)");
        log($" -> 전체 노드 덩어리 수: {nodeGroups.Count} (Master 1개, Slave {slaveGroups.Count}개)");
        log($"==================================================\n");
      }

      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);

      // Master 요소(Element) ID 사전 수집 (스냅 타겟을 빠르게 찾기 위함)
      var masterElementIds = new HashSet<int>();
      foreach (var kvp in elements)
      {
        if (kvp.Value.NodeIDs.Any(n => masterNodeIds.Contains(n)))
        {
          masterElementIds.Add(kvp.Key);
        }
      }

      // 3. 각 Slave 덩어리를 순회하며 통째로 병진 이동 처리
      foreach (var slaveNodeIds in slaveGroups)
      {
        var slaveFreeNodes = new List<int>();

        // Slave 덩어리 내부에서 Degree=1인 자유단(Free Node) 탐색
        foreach (var nid in slaveNodeIds)
        {
          if (nodeDegree.TryGetValue(nid, out int deg) && deg == 1)
          {
            slaveFreeNodes.Add(nid);
          }
        }

        if (slaveFreeNodes.Count == 0) continue; // 앵커로 쓸 자유단이 없으면 패스

        double bestDist = double.MaxValue;
        Vector3D bestTranslationVector = default;
        int bestSourceNode = -1;
        int bestTargetElement = -1;

        // 4. 최적의 타겟(Master 부재) 탐색
        foreach (var freeNodeId in slaveFreeNodes)
        {
          if (!nodes.Contains(freeNodeId)) continue;
          var pFree = nodes[freeNodeId];

          foreach (var masterEid in masterElementIds)
          {
            if (!elements.Contains(masterEid)) continue;
            var masterElem = elements[masterEid];
            if (masterElem.NodeIDs.Count < 2) continue;

            var pA = nodes[masterElem.NodeIDs.First()];
            var pB = nodes[masterElem.NodeIDs.Last()];

            // 속성 치수를 기반으로 탐색 반경 설정 (ElementExtensions 활용)
            double searchDim = masterElem.GetReferencedPropertyDim(properties);
            double allowedDist = searchDim + opt.ExtraMargin;

            double dist = DistancePointToSegment(pFree, pA, pB, out Point3D projPoint);

            if (dist <= allowedDist && dist < bestDist)
            {
              bestDist = dist;
              bestTranslationVector = projPoint - pFree;
              bestSourceNode = freeNodeId;
              bestTargetElement = masterEid;
            }
          }
        }

        // 5. 타겟을 찾았다면 해당 Slave 덩어리의 '모든 노드'를 일괄 이동 (왜곡 완벽 차단)
        if (bestTargetElement != -1 && bestDist > 1e-4)
        {
          foreach (var nid in slaveNodeIds)
          {
            if (!nodes.Contains(nid)) continue;
            var p = nodes[nid];
            var newP = p + bestTranslationVector;

            // 기존 노드 ID를 유지한 채 좌표만 통째로 시프트
            nodes.AddWithID(nid, newP.X, newP.Y, newP.Z);
          }

          translatedGroupCount++;

          if (opt.VerboseDebug)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            log($"[그룹 이동 완료] Slave 덩어리(노드 {slaveNodeIds.Count}개)가 통째로 E{bestTargetElement}에 스냅되었습니다.");
            Console.ResetColor();
            log($"   - 앵커 노드: N{bestSourceNode}");
            log($"   - 이동 벡터: ({bestTranslationVector.X:F1}, {bestTranslationVector.Y:F1}, {bestTranslationVector.Z:F1})");
            log($"   - 이동 거리: {bestDist:F2}\n");
          }
        }
      }

      if (opt.PipelineDebug)
      {
        if (translatedGroupCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          log($"[수정 완료] 총 {translatedGroupCount}개의 Slave 덩어리가 완벽한 직진성을 유지하며 병진 이동되었습니다.\n");
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
    /// Element와 Rigid(RBE)의 연결성을 모두 고려하여 위상학적 덩어리(Node Group)를 추출합니다.
    /// 밸브/트랩 등으로 쪼개진 배관을 하나의 완벽한 강체 그룹으로 인식하게 만듭니다.
    /// </summary>
    private static List<HashSet<int>> FindConnectedNodeGroups(FeModelContext context)
    {
      var allNodeIDs = new HashSet<int>();

      // 1. 요소(CBEAM 등)에서 노드 추출
      foreach (var kvp in context.Elements)
        foreach (var nid in kvp.Value.NodeIDs)
          allNodeIDs.Add(nid);

      // 2. 강체(RBE2 등)에서 노드 추출
      foreach (var kvp in context.Rigids)
      {
        allNodeIDs.Add(kvp.Value.IndependentNodeID);
        foreach (var nid in kvp.Value.DependentNodeIDs)
          allNodeIDs.Add(nid);
      }

      if (allNodeIDs.Count == 0) return new List<HashSet<int>>();

      var uf = new UnionFind(allNodeIDs.ToList());

      // 3. 일반 요소의 양 끝단 연결
      foreach (var kvp in context.Elements)
      {
        var nodeIds = kvp.Value.NodeIDs;
        if (nodeIds.Count < 2) continue;
        int baseNode = nodeIds[0];
        for (int i = 1; i < nodeIds.Count; i++)
          uf.Union(baseNode, nodeIds[i]);
      }

      // 4. 강체의 마스터-슬레이브 연결 (밸브로 끊긴 파이프를 한 덩어리로 꿰맴)
      foreach (var kvp in context.Rigids)
      {
        int indepNode = kvp.Value.IndependentNodeID;
        foreach (var depNode in kvp.Value.DependentNodeIDs)
          uf.Union(indepNode, depNode);
      }

      // 5. 그룹화
      var groups = new Dictionary<int, HashSet<int>>();
      foreach (var nid in allNodeIDs)
      {
        int root = uf.Find(nid);
        if (!groups.ContainsKey(root))
          groups[root] = new HashSet<int>();
        groups[root].Add(nid);
      }

      return groups.Values.ToList();
    }

    /// <summary>
    /// 점 P와 선분 AB 사이의 최단 거리와 투영점(수선의 발)을 계산합니다.
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
      t = Math.Max(0.0, Math.Min(1.0, t)); // 선분 내부로 클램핑

      projPoint = a + (ab * t);
      return (p - projPoint).Magnitude();
    }
  }
}
