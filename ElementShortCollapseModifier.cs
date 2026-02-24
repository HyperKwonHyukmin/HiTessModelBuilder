using HiTessModelBuilder.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 허용 오차보다 짧은 요소를 붕괴(Collapse)시켜 양 끝 노드를 하나로 병합(Merge)합니다.
  /// 미세 요소로 인한 해석 에러(Singularity)를 방지하는 위상 힐링(Healing) 작업을 수행합니다.
  /// </summary>
  public static class ElementShortCollapseModifier
  {
    public sealed record Options(
        double Tolerance = 1.0,         // 이 길이보다 짧은 요소를 병합 대상으로 간주
        bool PipelineDebug = false,     // 파이프라인 단계별 요약 정보 출력 여부
        bool VerboseDebug = false       // 개별 병합 상세 출력 여부
    );

    public static void Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var elements = context.Elements;
      var nodes = context.Nodes;

      int collapsedCount = 0;
      int removedDegenerateCount = 0;

      // 컬렉션 수정 중 오류를 방지하기 위해 스냅샷 생성
      foreach (var eid in elements.Keys.ToList())
      {
        if (!elements.Contains(eid)) continue;

        var e = elements[eid];
        if (e.NodeIDs.Count < 2) continue;

        int n1 = e.NodeIDs[0];
        int n2 = e.NodeIDs[1];

        if (!nodes.Contains(n1) || !nodes.Contains(n2)) continue;

        // [최적화] Point3D 연산자 오버로딩 활용하여 길이 계산
        double len = (nodes[n1] - nodes[n2]).Magnitude();

        if (len < opt.Tolerance)
        {
          // n1은 살리고, n2는 삭제(n1으로 통폐합)
          int keep = n1;
          int remove = n2;

          // 1. 타겟이 된 짧은 요소 자체는 삭제
          elements.Remove(eid);
          collapsedCount++;

          // 2. 삭제될 노드(remove)를 참조하고 있던 이웃 요소들 찾기
          var neighbors = elements.Where(kv => kv.Value.NodeIDs.Contains(remove)).ToList();

          foreach (var neighbor in neighbors)
          {
            var neighborEid = neighbor.Key;
            var neighborEle = neighbor.Value;

            // 기존 노드 리스트에서 'remove'를 'keep'으로 교체
            var newNodeIds = neighborEle.NodeIDs
                .Select(id => id == remove ? keep : id)
                .ToList();

            // [중요 방어 로직] 노드 교체 후 요소의 양 끝 노드가 같아진다면? (길이 0 요소됨)
            if (newNodeIds.Distinct().Count() < 2)
            {
              // 찌그러진(Degenerate) 요소가 되므로 삭제
              elements.Remove(neighborEid);
              removedDegenerateCount++;
              continue;
            }

            // 정상적인 요소라면 속성 유지한 채 노드만 업데이트 (기존 삭제 후 신규 생성)
            var extraData = neighborEle.ExtraData?.ToDictionary(k => k.Key, v => v.Value);
            int propId = neighborEle.PropertyID;

            elements.Remove(neighborEid);
            elements.AddWithID(neighborEid, newNodeIds, propId, extraData);
          }

          // 3. 더 이상 쓰이지 않는 노드 삭제
          if (nodes.Contains(remove))
            nodes.Remove(remove);

          if (opt.VerboseDebug)
            log($"   -> [병합] E{eid} 삭제됨. 노드 N{remove}가 N{keep}으로 통폐합되었습니다.");
        }
      }

      // 4. 파이프라인 디버그 로그 출력
      if (opt.PipelineDebug)
      {
        if (collapsedCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          Console.WriteLine($"[복구] 미세 요소 병합 : {opt.Tolerance} 미만의 짧은 요소 {collapsedCount}개를 붕괴시켜 꿰맸습니다. (과정 중 찌그러진 요소 {removedDegenerateCount}개 추가 삭제)");
          Console.ResetColor();
        }
        else
        {
          Console.WriteLine($"[통과] 미세 요소 병합 : 허용치 미만의 짧은 요소가 없습니다.");
        }
      }
    }
  }
}
