using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 서로 다른 두 Rigid(RBE)의 노드가 지정된 허용 거리 내에 있을 경우,
  /// 두 노드를 하나로 병합하고 해당 Rigid들을 하나의 거대한 Rigid로 통폐합합니다.
  /// </summary>
  public static class RigidProximityMergeModifier
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

      int mergedRigidCount = 0;

      // 1. 모든 Rigid의 노드 및 역참조(Node -> Rigids) 수집
      var rigidNodes = new HashSet<int>();
      var nodeToRigids = new Dictionary<int, List<int>>();

      foreach (var kvp in context.Rigids)
      {
        int rId = kvp.Key;
        var r = kvp.Value;
        var nids = new List<int> { r.IndependentNodeID };
        nids.AddRange(r.DependentNodeIDs);

        foreach (int nid in nids)
        {
          rigidNodes.Add(nid);
          if (!nodeToRigids.ContainsKey(nid)) nodeToRigids[nid] = new List<int>();
          nodeToRigids[nid].Add(rId);
        }
      }

      if (rigidNodes.Count == 0) return 0;

      // 2. 공간 탐색으로 오차 내에 있는 (Rigid Node - Rigid Node) 쌍 찾기
      var grid = new SpatialHash(context.Nodes, opt.Tolerance * 2.0);
      var closeNodePairs = new List<(int n1, int n2)>();

      foreach (int nid in rigidNodes)
      {
        if (!context.Nodes.Contains(nid)) continue;
        var p = context.Nodes[nid];
        var bbox = new BoundingBox(
            new Point3D(p.X - opt.Tolerance, p.Y - opt.Tolerance, p.Z - opt.Tolerance),
            new Point3D(p.X + opt.Tolerance, p.Y + opt.Tolerance, p.Z + opt.Tolerance)
        );

        var candidates = grid.Query(bbox);
        foreach (int candId in candidates)
        {
          if (candId <= nid) continue; // 중복 방지
          if (!rigidNodes.Contains(candId)) continue; // Rigid 노드끼리만 취급

          double dist = (context.Nodes[candId] - p).Magnitude();
          if (dist <= opt.Tolerance)
          {
            closeNodePairs.Add((nid, candId));
          }
        }
      }

      if (closeNodePairs.Count == 0) return 0;

      // 3. 근접 노드를 공유하는 Rigid들을 하나의 클러스터로 그룹화 (Union-Find)
      var ufRigids = new UnionFind(context.Rigids.Keys.ToList());
      foreach (var pair in closeNodePairs)
      {
        foreach (int r1 in nodeToRigids[pair.n1])
          foreach (int r2 in nodeToRigids[pair.n2])
            ufRigids.Union(r1, r2);
      }

      var rigidClusters = new Dictionary<int, List<int>>();
      foreach (int rId in context.Rigids.Keys)
      {
        int root = ufRigids.Find(rId);
        if (!rigidClusters.ContainsKey(root)) rigidClusters[root] = new List<int>();
        rigidClusters[root].Add(rId);
      }

      var globalOldToRep = new Dictionary<int, int>();

      // 4. 각 클러스터 단위로 병합 진행
      foreach (var cluster in rigidClusters.Values)
      {
        if (cluster.Count < 2) continue; // 2개 이상의 Rigid가 엮인 경우만 통폐합 진행

        var nodesInCluster = new HashSet<int>();
        foreach (int rId in cluster)
        {
          var r = context.Rigids[rId];
          nodesInCluster.Add(r.IndependentNodeID);
          foreach (int dep in r.DependentNodeIDs) nodesInCluster.Add(dep);
        }

        // 클러스터 내부의 노드들끼리 근접 병합
        var clusterClosePairs = closeNodePairs.Where(p => nodesInCluster.Contains(p.n1) && nodesInCluster.Contains(p.n2)).ToList();
        var ufNodes = new UnionFind(nodesInCluster.ToList());
        foreach (var pair in clusterClosePairs) ufNodes.Union(pair.n1, pair.n2);

        var nodeGroups = new Dictionary<int, List<int>>();
        foreach (int nid in nodesInCluster)
        {
          int root = ufNodes.Find(nid);
          if (!nodeGroups.ContainsKey(root)) nodeGroups[root] = new List<int>();
          nodeGroups[root].Add(nid);
        }

        var mappedUniqueNodes = new HashSet<int>();
        foreach (var group in nodeGroups.Values)
        {
          group.Sort();
          int keep = group[0];

          // PointMass가 있는 노드를 최우선으로 살림 (장비 질량 유실 방지)
          var massNode = group.FirstOrDefault(n => context.PointMasses.Any(pm => pm.Value.NodeID == n));
          if (massNode != 0) keep = massNode;

          foreach (int remove in group)
          {
            if (remove != keep) globalOldToRep[remove] = keep;
            mappedUniqueNodes.Add(keep);
          }
        }

        if (mappedUniqueNodes.Count < 2) continue;

        // Independent Node 선정 (PointMass가 있는 곳 우선 지정)
        int independentNode = mappedUniqueNodes.First();
        var pointMassNode = mappedUniqueNodes.FirstOrDefault(n => context.PointMasses.Any(pm => pm.Value.NodeID == n));
        if (pointMassNode != 0) independentNode = pointMassNode;

        var dependentNodes = mappedUniqueNodes.Where(n => n != independentNode).ToList();

        var firstRigid = context.Rigids[cluster[0]];
        var extraData = firstRigid.ExtraData?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, string>();
        extraData["Remark"] = "Merged_Proximity_Rigids";

        // 기존 Rigid 삭제 및 통합된 새 Rigid 생성
        foreach (int rId in cluster) context.Rigids.Remove(rId);

        context.Rigids.AddNew(independentNode, dependentNodes, "123456", extraData);
        mergedRigidCount += (cluster.Count - 1);

        if (opt.VerboseDebug)
          log($"   -> [Rigid 병합] {cluster.Count}개의 근접한 Rigid가 1개로 통폐합되었습니다.");
      }

      // 5. 전역 노드 치환 적용
      if (globalOldToRep.Count > 0)
      {
        context.Rigids.RemapAllNodes(globalOldToRep);
        context.PointMasses.RemapAllNodes(globalOldToRep);
        context.RemapWeldNodes(globalOldToRep);

        foreach (var kvp in globalOldToRep)
        {
          int removeNode = kvp.Key;
          int keepNode = kvp.Value;

          var neighborEids = context.Elements.Where(e => e.Value.NodeIDs.Contains(removeNode)).Select(e => e.Key).ToList();
          foreach (int eid in neighborEids)
          {
            var e = context.Elements[eid];
            var newNodeIds = e.NodeIDs.Select(id => id == removeNode ? keepNode : id).ToList();

            var extraData = e.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
            context.Elements.Remove(eid);
            if (newNodeIds.Distinct().Count() > 1)
            {
              context.Elements.AddWithID(eid, newNodeIds, e.PropertyID, e.Orientation, extraData);
            }
          }
          if (context.Nodes.Contains(removeNode)) context.Nodes.Remove(removeNode);
        }
      }

      if (opt.PipelineDebug && mergedRigidCount > 0)
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        log($"[변경] 근접 강체 통폐합 : 오차 {opt.Tolerance} 내에 인접한 Rigid들을 {mergedRigidCount}회 병합했습니다.");
        Console.ResetColor();
      }

      return mergedRigidCount;
    }
  }
}