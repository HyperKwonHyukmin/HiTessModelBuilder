using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
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

      // =========================================================================
      // [STEP 1] 허공에 뜬 Rigid 노드를 찾아 Element 끝단에 스냅 (이전 로직 유지)
      // =========================================================================
      var validStructureNodes = new HashSet<int>();
      var elementNodeDegree = new Dictionary<int, int>();

      foreach (var kvp in context.Elements)
      {
        foreach (int nid in kvp.Value.NodeIDs)
        {
          validStructureNodes.Add(nid);
          if (!elementNodeDegree.ContainsKey(nid)) elementNodeDegree[nid] = 0;
          elementNodeDegree[nid]++;
        }
      }

      var isolatedRigidNodes = new HashSet<int>();
      foreach (var kvp in context.Rigids)
      {
        var r = kvp.Value;
        if (!validStructureNodes.Contains(r.IndependentNodeID)) isolatedRigidNodes.Add(r.IndependentNodeID);
        foreach (var dep in r.DependentNodeIDs)
          if (!validStructureNodes.Contains(dep)) isolatedRigidNodes.Add(dep);
      }

      if (isolatedRigidNodes.Count > 0)
      {
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
            if (!validStructureNodes.Contains(candId)) continue;
            if (elementNodeDegree.GetValueOrDefault(candId, 0) != 1) continue;

            double dist = (context.Nodes[candId] - p).Magnitude();
            if (dist <= minDist) { minDist = dist; bestTargetNode = candId; }
          }

          if (bestTargetNode != -1) oldToRep[isoNodeId] = bestTargetNode;
        }

        if (oldToRep.Count > 0)
        {
          context.Rigids.RemapAllNodes(oldToRep);
          context.PointMasses.RemapAllNodes(oldToRep);
          context.RemapWeldNodes(oldToRep);

          foreach (var kvp in oldToRep)
          {
            if (context.Nodes.Contains(kvp.Key)) context.Nodes.Remove(kvp.Key);
            snappedCount++;
          }
        }
      }

      // =========================================================================
      // [STEP 2] 스냅되지 못하고 남은 "고립된 찌꺼기 그룹(PointMass + Rigid)" 영구 삭제
      // =========================================================================
      int deletedMassCount = 0;
      int deletedRigidCount = 0;

      // 모든 노드의 물리적 연결망(Graph) 생성
      var allUsedNodes = new HashSet<int>();

      // [에러 수정] .Values 대신 KeyValuePair 순회 후 .Value 참조
      foreach (var kvp in context.Elements)
        foreach (var n in kvp.Value.NodeIDs) allUsedNodes.Add(n);

      foreach (var kvp in context.Rigids)
      {
        allUsedNodes.Add(kvp.Value.IndependentNodeID);
        foreach (var n in kvp.Value.DependentNodeIDs) allUsedNodes.Add(n);
      }

      foreach (var kvp in context.PointMasses)
        allUsedNodes.Add(kvp.Value.NodeID);

      if (allUsedNodes.Count > 0)
      {
        var uf = new UnionFind(allUsedNodes.ToList());

        // 뼈대 연결
        foreach (var kvp in context.Elements)
          for (int i = 1; i < kvp.Value.NodeIDs.Count; i++) uf.Union(kvp.Value.NodeIDs[0], kvp.Value.NodeIDs[i]);

        // 강체 연결
        foreach (var kvp in context.Rigids)
          foreach (int dep in kvp.Value.DependentNodeIDs) uf.Union(kvp.Value.IndependentNodeID, dep);

        // Element가 하나라도 포함된 Root 덩어리 판별
        var rootsWithElements = new HashSet<int>();
        foreach (var kvp in context.Elements)
          rootsWithElements.Add(uf.Find(kvp.Value.NodeIDs[0]));

        // Element와 전혀 연결되지 않은 유령 PointMass 삭제
        var massesToRemove = new List<int>();
        foreach (var kvp in context.PointMasses)
        {
          int root = uf.Find(kvp.Value.NodeID);
          if (!rootsWithElements.Contains(root)) massesToRemove.Add(kvp.Key);
        }
        foreach (int id in massesToRemove) { context.PointMasses.Remove(id); deletedMassCount++; }

        // Element와 전혀 연결되지 않은 유령 Rigid 삭제
        var rigidsToRemove = new List<int>();
        foreach (var kvp in context.Rigids)
        {
          int root = uf.Find(kvp.Value.IndependentNodeID);
          if (!rootsWithElements.Contains(root)) rigidsToRemove.Add(kvp.Key);
        }
        foreach (int id in rigidsToRemove) { context.Rigids.Remove(id); deletedRigidCount++; }
      }

      if (opt.PipelineDebug && (snappedCount > 0 || deletedMassCount > 0 || deletedRigidCount > 0))
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        log($"[복구/정리] 고립 노드 스냅 및 찌꺼기 제거 : {snappedCount}개 스냅 완료 / " +
            $"구조물과 단절된 허공 PointMass {deletedMassCount}개, Rigid {deletedRigidCount}개 영구 삭제");
        Console.ResetColor();
      }

      return snappedCount;
    }
  }
}
