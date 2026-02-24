using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.ElementInspector;
using System;
using System.Collections.Generic;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// 길이가 설정값 미만이면서 한쪽 끝이 허공에 떠 있는 꼬투리(Dangling) 요소를 찾아 삭제합니다.
  /// </summary>
  public static class ElementDanglingShortRemoveModifier
  {
    public sealed record Options(
        double LengthThreshold = 50.0,  // 삭제할 꼬투리 요소의 최대 길이 기준 (매직넘버 제거)
        bool PipelineDebug = false,     // 파이프라인 단계별 요약 정보 출력 여부
        bool VerboseDebug = false       // 개별 요소 삭제 상세 출력 여부
    );

    public static void Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      // 1. 노드별 연결 개수(Degree) 계산
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      var shortEle = new List<int>();

      // 2. 조건에 맞는 꼬투리 요소 탐색
      foreach (var kv in context.Elements)
      {
        if (kv.Value.NodeIDs.Count < 2) continue;

        int n0 = kv.Value.NodeIDs[0];
        int n1 = kv.Value.NodeIDs[1];

        if (!nodeDegree.TryGetValue(n0, out int d0) || !nodeDegree.TryGetValue(n1, out int d1))
          continue;

        // 양 끝 노드 중 하나라도 자유단(연결 1개)인지 확인
        if (d0 == 1 || d1 == 1)
        {
          // GeometryTypes의 Point3D 연산자 오버로딩을 사용하여 길이 계산
          var p0 = context.Nodes[n0];
          var p1 = context.Nodes[n1];
          double len = (p0 - p1).Magnitude();

          if (len < opt.LengthThreshold)
          {
            shortEle.Add(kv.Key);
          }
        }
      }

      // 3. 요소 삭제 실행
      int removedCount = 0;
      foreach (int id in shortEle)
      {
        if (context.Elements.Contains(id))
        {
          context.Elements.Remove(id);
          removedCount++;

          if (opt.VerboseDebug)
            log($"   -> [삭제] E{id} (길이 미달 꼬투리 요소)");
        }
      }

      // 4. 파이프라인 디버그 로그 출력
      if (opt.PipelineDebug)
      {
        if (removedCount > 0)
        {
          Console.ForegroundColor = ConsoleColor.Cyan;
          Console.WriteLine($"[정리] 꼬투리 요소 제거 : 길이 {opt.LengthThreshold} 미만의 불필요한 꼬투리 요소 {removedCount}개가 삭제되었습니다.");
          Console.ResetColor();
        }
        else
        {
          Console.WriteLine($"[통과] 꼬투리 요소 제거 : 조건에 맞는 불필요한 요소가 없습니다.");
        }
      }
    }
  }
}
