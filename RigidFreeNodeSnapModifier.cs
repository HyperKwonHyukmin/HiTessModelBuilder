using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// Rigid(RBE)의 노드 중 Element(일반 부재)에 전혀 연결되지 않은 허공의 노드(FreeNode)를 찾아,
  /// 지정된 허용 오차(Tolerance) 내에 있는 "1번만 사용된 Element의 FreeNode"로 강제 스냅(병합)합니다.
  /// 이후 다른 부재(Element, Rigid)에 전혀 사용되지 않고 방치된 찌꺼기 PointMass를 찾아 삭제합니다.
  /// </summary>
  public static class RigidFreeNodeSnapModifier
  {
    public sealed record Options(
        double Tolerance = 50.0,
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      int snappedCount = 0;
      int removedMassCount = 0;

      // 1. Element 노드의 사용 횟수(Degree) 계산 및 유효 구조물 노드 수집
      var validStructureNodes = new HashSet<int>();
      var elementNodeDegree = new Dictionary<int, int>();

      foreach (var kvp in context.Elements)
      {
        foreach (int nid in kvp.Value.NodeIDs)
        {
          validStructureNodes.Add(nid);
          if (!elementNodeDegree.ContainsKey(nid))
            elementNodeDegree[nid] = 0;
          elementNodeDegree[nid]++;
        }
      }

      // 2. Rigid 노드 중 validStructureNodes에 속하지 않는 '완전 고립된 노드(FreeNode)' 추출
      var isolatedRigidNodes = new HashSet<int>();
      foreach (var kvp in context.Rigids)
      {
        var r = kvp.Value;
        if (!validStructureNodes.Contains(r.IndependentNodeID))
          isolatedRigidNodes.Add(r.IndependentNodeID);

        foreach (var dep in r.DependentNodeIDs)
        {
          if (!validStructureNodes.Contains(dep))
            isolatedRigidNodes.Add(dep);
        }
      }

      if (isolatedRigidNodes.Count > 0)
      {
        // 3. 고립된 Rigid 노드에서 가장 가까운 "Element의 FreeNode" 탐색
        var grid = new SpatialHash(context.Nodes, opt.Tolerance * 2.0);
        var oldToRep = new Dictionary<int, int>();

        foreach (int isoNodeId in isolatedRigidNodes)
        {
          if (!context.Nodes.Contains(isoNodeId)) continue;

          var p = context.Nodes[isoNodeId];
          var bbox = new BoundingBox(
              new Point3D(p.X - opt.Tolerance, p.Y - opt.Tolerance, p.Z - opt.Tolerance),
              new Point3D(p.X + opt.Tolerance, p.Y + opt.Tolerance, p.Z + opt.Tolerance)
          );

          var candidates = grid.Query(bbox);

          double minDist = opt.Tolerance;
          int bestTargetNode = -1;

          foreach (int candId in candidates)
          {
            if (candId == isoNodeId) continue;

            // [추가 조건 1] 타겟 노드는 반드시 Element에 속해야 함
            if (!validStructureNodes.Contains(candId)) continue;

            // [추가 조건 2] 타겟 노드 역시 Element 구성에 단 1번만 사용된 FreeNode여야 함
            if (elementNodeDegree.GetValueOrDefault(candId, 0) != 1) continue;

            double dist = (context.Nodes[candId] - p).Magnitude();
            if (dist <= minDist)
            {
              minDist = dist;
              bestTargetNode = candId;
            }
          }

          // 조건을 만족하는 가장 가까운 FreeNode를 찾았다면 치환 예약
          if (bestTargetNode != -1)
          {
            oldToRep[isoNodeId] = bestTargetNode;
          }
        }

        // 4. Rigid 노드 치환(병합) 적용 및 기존 허공 노드 삭제
        if (oldToRep.Count > 0)
        {
          context.Rigids.RemapAllNodes(oldToRep);
          context.PointMasses.RemapAllNodes(oldToRep);
          context.RemapWeldNodes(oldToRep);

          foreach (var kvp in oldToRep)
          {
            int removeNode = kvp.Key;
            int keepNode = kvp.Value;

            if (opt.VerboseDebug && context.Nodes.Contains(removeNode) && context.Nodes.Contains(keepNode))
            {
              double dist = (context.Nodes[keepNode] - context.Nodes[removeNode]).Magnitude();
              log($"   -> [고립 강체 스냅] 허공의 Rigid 노드 N{removeNode}가 부재의 FreeNode N{keepNode}(으)로 스냅(병합)되었습니다. (거리: {dist:F1}mm)");
            }

            // 고립 노드는 삭제
            if (context.Nodes.Contains(removeNode))
              context.Nodes.Remove(removeNode);

            snappedCount++;
          }
        }
      }

      // =========================================================================
      // 5. [추가 조건 3] 쓸모없는 PointMass 영구 삭제 정리
      // =========================================================================

      // 삭제 검사를 위해 (방금 스냅된 결과를 포함하여) Element와 Rigid에 사용 중인 모든 노드 재수집
      var allPhysicallyUsedNodes = new HashSet<int>();
      foreach (var kvp in context.Elements)
        foreach (int nid in kvp.Value.NodeIDs) allPhysicallyUsedNodes.Add(nid);

      foreach (var kvp in context.Rigids)
      {
        allPhysicallyUsedNodes.Add(kvp.Value.IndependentNodeID);
        foreach (var dep in kvp.Value.DependentNodeIDs) allPhysicallyUsedNodes.Add(dep);
      }

      var massKeysToRemove = new List<int>();

      foreach (var kvp in context.PointMasses)
      {
        int nid = kvp.Value.NodeID;
        // PointMass가 할당된 노드가 Element나 Rigid 생성에 단 한 번도 사용되지 않았다면 완전 고립(찌꺼기)으로 판정
        if (!allPhysicallyUsedNodes.Contains(nid))
        {
          massKeysToRemove.Add(kvp.Key);
        }
      }

      foreach (int key in massKeysToRemove)
      {
        string rawName = context.PointMasses[key].ExtraData?.GetValueOrDefault("Name") ?? "Unknown";
        context.PointMasses.Remove(key);
        removedMassCount++;

        if (opt.VerboseDebug)
          log($"   -> [질량 삭제] 어떠한 부재(Element/Rigid)와도 연결되지 않은 허공의 PointMass '{rawName}'(ID:{key})가 영구 삭제되었습니다.");
      }

      // =========================================================================
      // 6. 결과 로그 출력
      // =========================================================================
      if (opt.PipelineDebug)
      {
        if (snappedCount > 0 || removedMassCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          log($"[복구/정리] 고립 강체 스냅 및 질량 정리 : " +
              $"허공의 Rigid 노드 {snappedCount}개를 Element의 FreeNode에 연결하고, " +
              $"연결점이 없는 찌꺼기 질량 {removedMassCount}개를 삭제했습니다.");
          Console.ResetColor();
        }
      }

      return snappedCount;
    }
  }
}
