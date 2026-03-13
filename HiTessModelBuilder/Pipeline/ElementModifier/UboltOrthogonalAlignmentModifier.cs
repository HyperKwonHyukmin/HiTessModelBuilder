using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// BOX 타입 UBOLT의 Independent Node를 인접 배관의 방향과 직교하는 
  /// 전역 좌표계(X, Y, Z) 상의 지배적 그리드 라인에 정렬합니다.
  /// </summary>
  public static class UboltOrthogonalAlignmentModifier
  {
    public sealed record Options(
        double GridTolerance = 5.0,  // 동일 그리드로 간주할 허용 오차 (mm)
        bool PipelineDebug = true
    );

    public static void Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      // 1. 대상 UBOLT 추출 (BOX 타입)
      var targetUbolts = context.Rigids.Where(kv =>
          kv.Value.ExtraData != null &&
          kv.Value.ExtraData.TryGetValue("Type", out var type) && type == "UBOLT" &&
          kv.Value.ExtraData.TryGetValue("Remark", out var remark) && remark == "BOX").ToList();

      if (!targetUbolts.Any()) return;

      // 2. 전역 노드 좌표 그룹화 (그리드 맵 생성)
      var xGrid = context.Nodes.GroupBy(n => Math.Round(n.Value.X / opt.GridTolerance) * opt.GridTolerance)
                               .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.ToList());
      var yGrid = context.Nodes.GroupBy(n => Math.Round(n.Value.Y / opt.GridTolerance) * opt.GridTolerance)
                               .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.ToList());
      var zGrid = context.Nodes.GroupBy(n => Math.Round(n.Value.Z / opt.GridTolerance) * opt.GridTolerance)
                               .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.ToList());

      int alignedCount = 0;

      foreach (var ubolt in targetUbolts)
      {
        int indepId = ubolt.Value.IndependentNodeID;
        Point3D pIndep = context.Nodes[indepId];

        // 3. 인디펜던트 노드를 포함하는 배관 요소 찾기 및 방향 벡터 계산
        var ownerPipe = context.Elements
            .Select(kv => kv.Value) // KeyValuePair에서 Element 객체만 추출
            .FirstOrDefault(e =>
                e.NodeIDs.Contains(indepId) &&
                e.ExtraData.TryGetValue("Classification", out var cls) &&
                cls == "Pipe");

        if (ownerPipe == null) continue;

        var pA = context.Nodes[ownerPipe.NodeIDs.First()];
        var pB = context.Nodes[ownerPipe.NodeIDs.Last()];
        Vector3D pipeDir = (pB - pA).Normalize();

        // 4. 방향 벡터에 따른 직교 축 결정 및 좌표 보정
        // 예: [1,0,0] (X방향 배관) -> Y 또는 Z 좌표를 가장 지배적인 그리드로 이동
        double newX = pIndep.X;
        double newY = pIndep.Y;
        double newZ = pIndep.Z;

        // X축 배관일 때: Y, Z 좌표를 인근의 '가장 노드가 많은(지배적인)' 그리드로 스냅
        if (Math.Abs(pipeDir.X) > 0.9)
        {
          newY = FindBestGrid(pIndep.Y, yGrid);
          newZ = FindBestGrid(pIndep.Z, zGrid);
        }
        // Y축 배관일 때: X, Z 좌표 스냅
        else if (Math.Abs(pipeDir.Y) > 0.9)
        {
          newX = FindBestGrid(pIndep.X, xGrid);
          newZ = FindBestGrid(pIndep.Z, zGrid);
        }
        // Z축 배관일 때: X, Y 좌표 스냅
        else if (Math.Abs(pipeDir.Z) > 0.9)
        {
          newX = FindBestGrid(pIndep.X, xGrid);
          newY = FindBestGrid(pIndep.Y, yGrid);
        }

        // 5. 노드 좌표 업데이트
        if (newX != pIndep.X || newY != pIndep.Y || newZ != pIndep.Z)
        {
          context.Nodes.AddWithID(indepId, newX, newY, newZ);
          alignedCount++;
        }
      }

      if (opt.PipelineDebug) log($"[완료] UBOLT 직교 정렬: {alignedCount}개의 노드가 지배적 그리드에 맞춰 보정되었습니다.");
    }

    /// <summary>
    /// 특정 좌표값 근처에서 가장 많은 노드가 포함된 지배적 그리드 좌표를 찾습니다.
    /// </summary>
    private static double FindBestGrid(double currentVal, Dictionary<double, List<KeyValuePair<int, Point3D>>> gridMap)
    {
      // 현재 값에서 50mm 이내의 그리드 중 노드 수가 가장 많은 그리드 선택
      var candidates = gridMap.Keys.Where(k => Math.Abs(k - currentVal) < 50.0);
      if (!candidates.Any()) return currentVal;

      return candidates.OrderByDescending(k => gridMap[k].Count).First();
    }
  }
}