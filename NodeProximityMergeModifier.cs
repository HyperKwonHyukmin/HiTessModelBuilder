using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 구조물(Stru)-강체(Rigid), 또는 강체(Rigid)-강체(Rigid) 간의 노드가 
  /// 지정된 허용 오차(Tolerance) 내에 있을 경우 하나의 노드로 강제 병합(Equivalence)합니다.
  /// </summary>
  public static class NodeProximityMergeModifier
  {
    public sealed record Options(
        double Tolerance = 5.0,         // 병합을 허용할 최대 근접 거리
        bool PipelineDebug = true,
        bool VerboseDebug = false
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var nodes = context.Nodes;
      int mergedCount = 0;

      // 1. 병합 검사 대상 드 추출 (Rigid 노드 전체)
      var rigidNodeIds = new HashSet<int>();

      // [수정됨] .Values 대신 KeyValuePair를 순회하여 .Value에 접근
      foreach (var kvp in context.Rigids)
      {
        var r = kvp.Value;
        rigidNodeIds.Add(r.IndependentNodeID);
        foreach (var dep in r.DependentNodeIDs) rigidNodeIds.Add(dep);
      }

      if (rigidNodeIds.Count == 0) return 0;

      // 2. 고속 공간 탐색을 위한 SpatialHash 생성 (전체 노드 대상)
      var grid = new SpatialHash(nodes, opt.Tolerance * 2.0);
      var mergePairs = new List<(int keep, int remove)>();
      var visited = new HashSet<int>();

      // 3. 근접 노드 탐색
      foreach (int rigidNodeId in rigidNodeIds)
      {
        if (!nodes.Contains(rigidNodeId) || visited.Contains(rigidNodeId)) continue;

        var p = nodes[rigidNodeId];
        var bbox = new BoundingBox(
            new Point3D(p.X - opt.Tolerance, p.Y - opt.Tolerance, p.Z - opt.Tolerance),
            new Point3D(p.X + opt.Tolerance, p.Y + opt.Tolerance, p.Z + opt.Tolerance)
        );

        var candidates = grid.Query(bbox);
        foreach (int candId in candidates)
        {
          if (candId == rigidNodeId) continue;

          double dist = (nodes[candId] - p).Magnitude();
          if (dist <= opt.Tolerance)
          {
            // 작은 번호의 노드를 살리도록 규칙 정립
            int keep = Math.Min(rigidNodeId, candId);
            int remove = Math.Max(rigidNodeId, candId);
            mergePairs.Add((keep, remove));
            visited.Add(remove);
          }
        }
      }

      if (mergePairs.Count == 0) return 0;

      // 4. Union-Find를 이용한 연쇄 병합 그룹화
      var allInvolvedNodes = mergePairs.SelectMany(x => new[] { x.keep, x.remove }).Distinct().ToList();
      var uf = new UnionFind(allInvolvedNodes);
      foreach (var pair in mergePairs) uf.Union(pair.keep, pair.remove);

      var oldToRep = new Dictionary<int, int>();
      foreach (var nid in allInvolvedNodes)
      {
        int root = uf.Find(nid);
        if (nid != root) oldToRep[nid] = root;
      }

      // 5. 전역 데이터 매핑 및 삭제
      foreach (var kvp in oldToRep)
      {
        int removeNode = kvp.Key;
        int keepNode = kvp.Value;

        // Elements 교체
        var neighborEids = context.Elements.Where(e => e.Value.NodeIDs.Contains(removeNode)).Select(e => e.Key).ToList();
        foreach (int eid in neighborEids)
        {
          var e = context.Elements[eid];
          var newNodeIds = e.NodeIDs.Select(id => id == removeNode ? keepNode : id).ToList();

          var extraData = e.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
          context.Elements.Remove(eid);
          // 찌그러진 요소는 자동 소멸, 정상이면 재생성
          if (newNodeIds.Distinct().Count() > 1)
          {
            context.Elements.AddWithID(eid, newNodeIds, e.PropertyID, e.Orientation, extraData);
          }
        }

        // Node 영구 삭제
        if (context.Nodes.Contains(removeNode)) context.Nodes.Remove(removeNode);
        mergedCount++;

        if (opt.VerboseDebug)
          log($"   -> [근접 병합] N{removeNode}가 N{keepNode}로 통합되었습니다. (Stru-Rigid 연동)");
      }

      // Rigids, PointMasses, Weld 일괄 업데이트
      context.Rigids.RemapAllNodes(oldToRep);
      context.PointMasses.RemapAllNodes(oldToRep);
      context.RemapWeldNodes(oldToRep);

      if (opt.PipelineDebug)
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        log($"[변경] 이종 엔티티 노드 병합 : 공차 {opt.Tolerance} 내의 근접 노드 {mergedCount}개를 병합 완료했습니다.");
        Console.ResetColor();
      }

      return mergedCount;
    }
  }
}
