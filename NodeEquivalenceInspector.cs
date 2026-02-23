using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.NodeInspector
{
  public static class NodeEquivalenceInspector
  {
    /// <summary>
    /// 중복 위치에 있는 노드 그룹들을 찾아 반환합니다.
    /// </summary>
    /// <returns>중복된 노드 ID 리스트들의 리스트 (예: [[1, 2], [10, 11, 12]])</returns>
    public static List<List<int>> InspectEquivalenceNodes(FeModelContext context, double EquivalenceTolerance)
    {
      var resultGroups = new List<List<int>>();

      // 1. 좌표 정렬 (기존 로직 유지)
      var sortedNodes = context.Nodes
          .Select(n => new { ID = n.Key, n.Value.X, n.Value.Y, n.Value.Z })
          .OrderBy(n => n.X).ThenBy(n => n.Y).ThenBy(n => n.Z)
          .ToList();

      if (sortedNodes.Count < 2) return resultGroups;

      // 2. 그룹핑 로직
      // 현재 탐색 중인 중복 그룹 (첫 번째 노드 넣고 시작)
      var currentGroup = new List<int> { sortedNodes[0].ID };

      for (int i = 0; i < sortedNodes.Count - 1; i++)
      {
        var n1 = sortedNodes[i];
        var n2 = sortedNodes[i + 1];
        bool isCoincident = false;

        // X 좌표 차이가 오차보다 크면 계산할 필요 없음
        if (Math.Abs(n2.X - n1.X) <= EquivalenceTolerance)
        {
          // Y, Z 거리 제곱 확인
          double distSq = Math.Pow(n2.X - n1.X, 2) +
                          Math.Pow(n2.Y - n1.Y, 2) +
                          Math.Pow(n2.Z - n1.Z, 2);

          if (distSq < EquivalenceTolerance * EquivalenceTolerance)
          {
            isCoincident = true;
          }
        }

        if (isCoincident)
        {
          // 겹치면 현재 그룹에 n2 추가 (n1은 이미 들어있음)
          currentGroup.Add(n2.ID);
        }
        else
        {
          // 안 겹치면? 지금까지 모인 그룹 확인
          if (currentGroup.Count > 1)
          {
            // 중복 그룹이 형성되었으므로 결과에 추가 (복사본 저장)
            resultGroups.Add(new List<int>(currentGroup));
          }

          // 그룹 초기화: n2부터 다시 시작
          currentGroup.Clear();
          currentGroup.Add(n2.ID);
        }
      }

      // 마지막 남은 그룹 처리 (루프가 끝나서 추가 안 된 경우)
      if (currentGroup.Count > 1)
      {
        resultGroups.Add(currentGroup);
      }

      return resultGroups;
    }
  }
}
