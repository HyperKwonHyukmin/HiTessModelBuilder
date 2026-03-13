using System;
using System.Collections.Generic;
using System.Linq;
using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.Utils;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 두 요소(Element)가 같은 방향(평행)을 바라보고 있을 때, 
  /// 서로 다른 요소에 속한 두 노드(Node) 간의 거리가 허용치 이내라면 
  /// 해당 노드들을 하나의 노드로 병합(Equivalence)합니다.
  /// </summary>
  public static class ElementCollinearNodeMergeModifier
  {
    public sealed record Options(
        double DistanceTolerance = 50.0, // 노드 간 병합 허용 거리
        double AngleToleranceDeg = 3.0,  // 평행으로 간주할 각도 오차 (도 단위)
        bool PipelineDebug = false,      // 파이프라인 단계별 요약 정보 출력 여부
        bool VerboseDebug = false        // 개별 노드 병합 상세 출력 여부
    );

    public static void Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var nodes = context.Nodes;
      var elements = context.Elements;

      // 허용 각도를 라디안으로 변환 후 Cosine 값 계산 (내적 비교용)
      double angleTolRad = opt.AngleToleranceDeg * Math.PI / 180.0;
      double cosTol = Math.Cos(angleTolRad);

      // 1. 병합 대상이 될 노드 쌍(Pair) 탐색
      var mergePairs = new List<(int n1, int n2)>();
      var elemList = elements.ToList(); // O(N^2) 탐색을 위해 리스트화

      // ★ [신규 추가] 모델 내 모든 강체(Rigid) 관련 노드 ID 수집
      var rigidNodeIds = new HashSet<int>();
      foreach (var kvp in context.Rigids)
      {
        rigidNodeIds.Add(kvp.Value.IndependentNodeID);
        foreach (var dep in kvp.Value.DependentNodeIDs)
          rigidNodeIds.Add(dep);
      }

      for (int i = 0; i < elemList.Count; i++)
      {
        var e1 = elemList[i].Value;
        if (e1.NodeIDs.Count < 2) continue;

        var p1a = nodes[e1.NodeIDs[0]];
        var p1b = nodes[e1.NodeIDs[e1.NodeIDs.Count - 1]];
        var dir1 = (p1b - p1a).Normalize(); // 첫 번째 요소의 방향 벡터

        for (int j = i + 1; j < elemList.Count; j++)
        {
          var e2 = elemList[j].Value;
          if (e2.NodeIDs.Count < 2) continue;

          var p2a = nodes[e2.NodeIDs[0]];
          var p2b = nodes[e2.NodeIDs[e2.NodeIDs.Count - 1]];
          var dir2 = (p2b - p2a).Normalize(); // 두 번째 요소의 방향 벡터

          // [조건 1] 방향 벡터가 같은가? (내적의 절댓값이 Cos(오차)보다 크면 평행)
          if (Math.Abs(dir1.Dot(dir2)) < cosTol) continue;

          // ★ [신규 추가] 배관(Pipe)과 구조물(Stru) 간의 노드 병합 원천 차단
          string cat1 = e1.ExtraData.GetValueOrDefault("Classification") ?? e1.ExtraData.GetValueOrDefault("Category") ?? "";
          string cat2 = e2.ExtraData.GetValueOrDefault("Classification") ?? e2.ExtraData.GetValueOrDefault("Category") ?? "";

          // 두 요소의 분류가 다르면(예: Pipe vs Stru) 절대 병합하지 않고 건너뜀 (자유도 제어를 위한 Zero-length RBE 보존)
          if (cat1 != cat2) continue;

          // [조건 2] 각 요소의 양 끝 노드들 간의 거리가 Tolerance 이내인가?
          // 수정: 부재가 옆으로 꺾이는 것을 방지하기 위해 기준 부재의 방향 벡터(dir1)를 넘겨줍니다.
          CheckAndAddMergePair(e1.NodeIDs[0], e2.NodeIDs[0], p1a, p2a, opt.DistanceTolerance, dir1, mergePairs);
          CheckAndAddMergePair(e1.NodeIDs[0], e2.NodeIDs.Last(), p1a, p2b, opt.DistanceTolerance, dir1, mergePairs);
          CheckAndAddMergePair(e1.NodeIDs.Last(), e2.NodeIDs[0], p1b, p2a, opt.DistanceTolerance, dir1, mergePairs);
          CheckAndAddMergePair(e1.NodeIDs.Last(), e2.NodeIDs.Last(), p1b, p2b, opt.DistanceTolerance, dir1, mergePairs);
        }

        // ★ [신규 로직] Element vs Rigid Node 검사
        // 해당 Element의 축 방향 선상에 Rigid 노드가 존재하면 병합 후보로 등록합니다.
        foreach (int rNodeId in rigidNodeIds)
        {
          if (!nodes.Contains(rNodeId)) continue;
          var rPos = nodes[rNodeId];

          // 동일한 CheckAndAddMergePair를 사용하므로, 
          // 횡방향 단차(Sideways Offset < 1.0)가 없는 완벽한 공선(Collinear) 상태일 때만 병합됩니다!
          CheckAndAddMergePair(e1.NodeIDs[0], rNodeId, p1a, rPos, opt.DistanceTolerance, dir1, mergePairs);
          CheckAndAddMergePair(e1.NodeIDs.Last(), rNodeId, p1b, rPos, opt.DistanceTolerance, dir1, mergePairs);
        }
      }

      if (mergePairs.Count == 0)
      {
        if (opt.PipelineDebug) Console.WriteLine($"[통과] 평행 요소 노드 병합 : {opt.DistanceTolerance} 거리 내에 겹치는 평행 노드가 없습니다.");
        return;
      }

      // 2. Union-Find를 통해 연쇄적으로 겹친 노드들을 하나의 그룹으로 묶기 (예: A=B, B=C 이면 A=B=C)
      var allMergeNodes = mergePairs.SelectMany(p => new[] { p.n1, p.n2 }).Distinct().ToList();
      var uf = new UnionFind(allMergeNodes);
      foreach (var pair in mergePairs)
      {
        uf.Union(pair.n1, pair.n2);
      }

      var mergeGroups = new Dictionary<int, List<int>>();
      foreach (var nid in allMergeNodes)
      {
        int root = uf.Find(nid);
        if (!mergeGroups.ContainsKey(root)) mergeGroups[root] = new List<int>();
        mergeGroups[root].Add(nid);
      }

      // 3. 실제 노드 통폐합(Equivalence) 및 요소 업데이트
      int mergedNodesCount = 0;
      int removedDegenerateCount = 0;

      // ★ [추가] 병합 이력을 추적할 딕셔너리 생성 (삭제된 노드 ID -> 살릴 노드 ID)
      var nodeMapping = new Dictionary<int, int>();

      foreach (var group in mergeGroups.Values)
      {
        if (group.Count < 2) continue;

        // ID가 가장 작은 노드를 '살릴 노드(keepNode)'로 지정하고 나머지는 삭제
        group.Sort();
        int keepNode = group[0];
        var removeNodes = group.Skip(1).ToList();

        foreach (int removeNode in removeNodes)
        {
          if (!nodes.Contains(removeNode)) continue;

          // ★ [추가] 삭제될 노드가 어떤 노드로 흡수되었는지 기록
          nodeMapping[removeNode] = keepNode;

          // 삭제될 노드를 참조하고 있는 주변의 모든 요소 찾기
          var neighbors = elements.Where(kv => kv.Value.NodeIDs.Contains(removeNode)).ToList();

          foreach (var neighbor in neighbors)
          {
            var neighborEid = neighbor.Key;
            var neighborEle = neighbor.Value;

            // 삭제될 노드를 살릴 노드(keepNode) 번호로 갈아끼움
            var newNodeIds = neighborEle.NodeIDs
                .Select(id => id == removeNode ? keepNode : id)
                .ToList();

            if (newNodeIds.Distinct().Count() < 2)
            {
              // ★ 숨겨진 암살자 검거: 찌그러진 부재 삭제 로그 추가
              string rawName = neighborEle.ExtraData?.GetValueOrDefault("ID") ?? neighborEle.ExtraData?.GetValueOrDefault("Name") ?? "Unknown";
              Console.ForegroundColor = ConsoleColor.Yellow;
              if (opt.VerboseDebug)
                Console.WriteLine($"   -> [영구 삭제] 노드 통폐합으로 인해 부재 '{rawName}'(E{neighborEid})가 찌그러져(길이 0) 삭제되었습니다.");
              Console.ResetColor();

              elements.Remove(neighborEid);
              removedDegenerateCount++;
              continue;
            }

            // 요소 업데이트 (기존 ID 유지, 노드만 교체)
            var extraData = neighborEle.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
            var barOrientation = neighborEle.Orientation;
            int propId = neighborEle.PropertyID;

            elements.Remove(neighborEid);
            elements.AddWithID(neighborEid, newNodeIds, propId, barOrientation, extraData);
          }

          // 요소들의 이사가 끝났으니 노드 영구 삭제
          nodes.Remove(removeNode);
          mergedNodesCount++;

          if (opt.VerboseDebug)
            log($"   -> [병합] 노드 N{removeNode}가 N{keepNode}(으)로 통폐합되었습니다.");
        }
      }

      // ★ [추가] 연쇄 병합(A->B, B->C)을 추적하여 RBE와 PointMass에 최종 번호 전파
      if (nodeMapping.Count > 0)
      {
        var resolvedMapping = new Dictionary<int, int>();
        foreach (var key in nodeMapping.Keys)
        {
          int finalNode = key;
          while (nodeMapping.TryGetValue(finalNode, out int nextNode))
          {
            finalNode = nextNode;
          }
          resolvedMapping[key] = finalNode;
        }
        // 변경된 노드 번호를 RBE와 질량에 업데이트
        context.Rigids.RemapAllNodes(resolvedMapping);
        context.PointMasses.RemapAllNodes(resolvedMapping);
        context.RemapWeldNodes(resolvedMapping);
      }

      // 4. 파이프라인 디버그 로그
      if (opt.PipelineDebug)
      {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[변경] 평행 요소 노드 병합 : {opt.DistanceTolerance} 거리 내 인접한 노드 {mergedNodesCount}개 통폐합 완료. (과정 중 찌그러진 요소 {removedDegenerateCount}개 제거)");
        Console.ResetColor();
      }
    }

    // --- Helper: 동일 노드가 아니면서 거리 허용치 이내면 병합 후보에 추가 ---
    private static void CheckAndAddMergePair(int n1, int n2, Point3D p1, Point3D p2, double tol, List<(int, int)> mergePairs)
    {
      if (n1 == n2) return; // 이미 같은 노드(공유 노드)면 무시

      if ((p1 - p2).Magnitude() <= tol)
      {
        mergePairs.Add((n1, n2));
      }
    }

    /// <summary>
    /// 두 노드가 허용 반경 내에 있으면서, 횡방향 단차(Sideways offset)가 거의 없는 
    /// 직축(Coaxial) 상에 위치한 경우에만 안전하게 병합 후보로 추가합니다.
    /// </summary>
    private static void CheckAndAddMergePair(int n1, int n2, Point3D p1, Point3D p2, double tol, Vector3D elementDir, List<(int, int)> mergePairs)
    {
      if (n1 == n2) return;

      Vector3D diff = p1 - p2;
      double dist = diff.Magnitude();

      if (dist <= tol)
      {
        // 횡방향(Sideways) 오프셋 계산: (전체 거리 벡터) - (축 방향 투영 벡터)
        double sidewaysOffset = (diff - (elementDir * diff.Dot(elementDir))).Magnitude();

        // 단차가 1.0mm(허용치) 미만일 때만 꿰맵니다. (크면 억지로 구부러짐)
        if (sidewaysOffset < 1.0)
        {
          mergePairs.Add((n1, n2));
        }
      }
    }
  }
}