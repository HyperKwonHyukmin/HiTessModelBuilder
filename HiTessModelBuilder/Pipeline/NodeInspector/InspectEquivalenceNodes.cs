using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.NodeInspector
{
  public static class NodeEquivalenceInspector
  {
    /// <summary>
    /// 허용 오차(Tolerance) 내에 존재하는 중복 노드 그룹을 O(N log N)으로 빠르고 정확하게 찾아냅니다.
    /// </summary>
    public static List<List<int>> InspectEquivalenceNodes(FeModelContext context, double tolerance)
    {
      var resultGroups = new List<List<int>>();

      // 1. X축 기준으로만 정렬 (Sweep Line 기준축)
      var sortedNodes = context.Nodes.GetAllNodes()
          .Select(kv => new { ID = kv.Key, Point = kv.Value })
          .OrderBy(n => n.Point.X)
          .ToList();

      if (sortedNodes.Count < 2) return resultGroups;

      var visited = new HashSet<int>();
      double tolSq = tolerance * tolerance; // 제곱근(Sqrt) 계산을 피하기 위한 최적화

      // 2. Sweep and Prune 로직
      for (int i = 0; i < sortedNodes.Count; i++)
      {
        if (visited.Contains(sortedNodes[i].ID)) continue;

        var currentGroup = new List<int> { sortedNodes[i].ID };
        visited.Add(sortedNodes[i].ID);

        // 자기 다음 노드들 탐색
        for (int j = i + 1; j < sortedNodes.Count; j++)
        {
          // ★ X좌표 차이가 오차를 벗어나면, 그 뒤는 볼 필요 없이 루프 즉시 탈출 (성능 핵심)
          if (sortedNodes[j].Point.X - sortedNodes[i].Point.X > tolerance)
            break;

          if (visited.Contains(sortedNodes[j].ID)) continue;

          // X, Y, Z 실제 3D 거리 제곱 비교
          double distSq = Math.Pow(sortedNodes[j].Point.X - sortedNodes[i].Point.X, 2) +
                          Math.Pow(sortedNodes[j].Point.Y - sortedNodes[i].Point.Y, 2) +
                          Math.Pow(sortedNodes[j].Point.Z - sortedNodes[i].Point.Z, 2);

          if (distSq <= tolSq)
          {
            currentGroup.Add(sortedNodes[j].ID);
            visited.Add(sortedNodes[j].ID);
          }
        }

        if (currentGroup.Count > 1)
        {
          resultGroups.Add(currentGroup);
        }
      }

      return resultGroups;
    }
  }
}