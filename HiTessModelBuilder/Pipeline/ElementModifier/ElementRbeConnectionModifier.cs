using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Geometry;
using HiTessModelBuilder.Pipeline.ElementInspector;
using HiTessModelBuilder.Pipeline.NodeInspector;
using HiTessModelBuilder.Pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HiTessModelBuilder.Pipeline.ElementModifier
{
  /// <summary>
  /// [Stage 7: RBE2 강체 연결]
  /// 연장이나 병진 이동으로도 해결되지 않은 잔여 Free Node를 타겟 부재에 수선의 발(Projection)을 내려
  /// 신규 노드를 생성하고, 두 노드 사이를 강체(RBE2)로 연결합니다.
  /// </summary>
  public static class ElementRbeConnectionModifier
  {
    public sealed record Options(
        double ExtraMargin = 5.0, // RBE 연결을 허용할 추가 마진
        bool PipelineDebug = true,
        bool VerboseDebug = true
    );

    public static int Run(FeModelContext context, Options? opt = null, Action<string>? log = null)
    {
      opt ??= new Options();
      log ??= Console.WriteLine;

      var nodes = context.Nodes;
      var elements = context.Elements;
      var properties = context.Properties;

      // 1. 아직도 남아있는 Free Node(Degree = 1) 도출
      var nodeDegree = NodeDegreeInspector.BuildNodeDegree(context);
      var freeNodes = nodeDegree.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();

      if (freeNodes.Count == 0) return 0;

      if (opt.VerboseDebug)
      {
        log($"\n==================================================");
        log($"[수정 시작] ElementRbeConnectionModifier (RBE2 강체 브릿지 생성)");
        log($" -> 잔여 Free Node 개수: {freeNodes.Count}개");
        log($"==================================================\n");
      }

      int rbeCreatedCount = 0;
      var newRbeElements = new List<(int n1, int n2, int sourceEid, int targetEid)>();

      // ★ [Phase 4-3] ElementSpatialHash로 Free Node별 전체 요소 전수 탐색 제거
      // inflate = ExtraMargin + 500mm (단면 최대 치수 상한 추정치)
      double hashInflate = opt.ExtraMargin + 500.0;
      var spatialHash = new ElementSpatialHash(elements, nodes, hashInflate * 2, hashInflate);

      // 단면 치수 캐시 (PropertyDimensionHelper 중복 호출 제거)
      var propDimCache = new Dictionary<int, double>();

      // 2. 각 Free Node에 대해 SearchDim 내에 있는 최적의 Target Element 탐색
      foreach (var freeNodeId in freeNodes)
      {
        var pFree = nodes[freeNodeId];

        double bestDist = double.MaxValue;
        Point3D bestProjPoint = default;
        int bestTargetEid = -1;

        // Free Node 주변의 후보 요소만 탐색
        var queryBB = BoundingBox.FromSegment(pFree, pFree, hashInflate);
        foreach (var targetEid in spatialHash.QueryBBox(queryBB))
        {
          var targetElem = elements[targetEid];
          if (targetElem.NodeIDs.Count < 2) continue;
          if (targetElem.NodeIDs.Contains(freeNodeId)) continue; // 자기 자신 제외

          var pA = nodes[targetElem.NodeIDs.First()];
          var pB = nodes[targetElem.NodeIDs.Last()];

          // 타겟 부재의 SearchDim 계산 (캐시 활용)
          var prop = properties[targetElem.PropertyID];
          if (!propDimCache.TryGetValue(targetElem.PropertyID, out double searchDim))
          {
            searchDim = PropertyDimensionHelper.GetMaxCrossSectionDim(prop);
            propDimCache[targetElem.PropertyID] = searchDim;
          }
          double allowedDist = searchDim + opt.ExtraMargin;

          // 수선의 발(Projection Point)과 최단 거리 계산
          double dist = ProjectionUtils.DistancePointToSegment(pFree, pA, pB, out Point3D projPoint, out double t);

          // 허용 반경 이내이고, 가장 가까우며, 수선의 발이 선분 내부(0 <= t <= 1)에 떨어지는 경우만 채택
          if (dist <= allowedDist && dist < bestDist && t >= -1e-4 && t <= 1.0001)
          {
            bestDist = dist;
            bestProjPoint = projPoint;
            bestTargetEid = targetEid;
          }
        }

        // 3. 최적의 타겟을 찾았다면 연결 예약
        if (bestTargetEid != -1 && bestDist > 1e-4)
        {
          // 수선의 발 위치에 새로운 Node 생성 (이미 있으면 가져옴)
          int projNodeId = nodes.AddOrGet(bestProjPoint.X, bestProjPoint.Y, bestProjPoint.Z);

          // RBE 생성을 위한 데이터 저장 (생성 도중 컬렉션이 변경되는 것을 방지)
          newRbeElements.Add((freeNodeId, projNodeId, -1, bestTargetEid));
        }
      }

      // 4. 예약된 RBE2 요소들을 FeModelContext의 Rigids 컬렉션에 추가
      foreach (var rbe in newRbeElements)
      {
        // rbe.n2 (수선의 발, 타겟 부재 위) = Independent Node (GN)
        // rbe.n1 (기존 Free Node) = Dependent Node (GM)
        var extraData = new Dictionary<string, string>
        {
            { "Category", "Auto-Healing" },
            { "Type", "RBE_BRIDGE" },
            { "Desc", $"FreeNode N{rbe.n1} to Target E{rbe.targetEid}" },
            { "Classification", "Bridge"}
        };
        context.Rigids.AddNew(independentNodeID: rbe.n2, dependentNodeIDs: new[] { rbe.n1 }, cm: "123456", extraData: extraData);
        rbeCreatedCount++;

        if (opt.VerboseDebug)
        {
          Console.ForegroundColor = ConsoleColor.Green;
          log($"[RBE2 생성 완료] Free Node N{rbe.n1}가 E{rbe.targetEid}에 수선의 발을 내려 강체 연결되었습니다.");
          Console.ResetColor();
          log($"   - 생성된 수선의 발 Node (Independent): N{rbe.n2}");
        }
      }

      return rbeCreatedCount;
    }

  }
}