using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;
using HiTessModelBuilder.Model.Geometry;
using System;
using System.Collections.Generic;

namespace HiTessModelBuilder.Services.Builders
{
  /// <summary>
  /// 파싱된 원시 구조물 데이터를 바탕으로 FE 모델(Nodes, Properties, Elements)을 생성합니다.
  /// </summary>
  public class RawFeModelBuilder
  {
    // 아키텍트 조언: 외부 조작을 막기 위해 private readonly 사용
    private readonly RawCsvDesignData _rawStructureDesignData;
    private readonly FeModelContext _feModelContext;
    public Dictionary<string, List<int>> pipeElementIDsByType = new();
    private readonly bool _debugPrint;

    public RawFeModelBuilder(
        RawCsvDesignData? StructureData,
        FeModelContext feModelContext,
        bool debugPrint = false)
    {
      _rawStructureDesignData = StructureData ?? throw new ArgumentNullException(nameof(StructureData));
      _feModelContext = feModelContext ?? throw new ArgumentNullException(nameof(feModelContext));
      _debugPrint = debugPrint;
    }

    /// <summary>
    /// 전체 FE 모델 생성을 실행합니다.
    /// </summary>
    public void Build()
    {
      if (_debugPrint) Console.WriteLine("\n[Builder] Starting FE Model Build...");

      // 1. 공통 Material 생성 (Steel)
      int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

      // 2. 각 타입별 Element 일괄 생성 (함수형 접근)
      BuildStruElements(_rawStructureDesignData.AngDesignList, materialID, "L", "ANGLE", "L",
          e => new[] { e.Dim1, e.Dim2, e.Dim3, e.Dim3 });

      BuildStruElements(_rawStructureDesignData.BeamDesignList, materialID, "H", "BEAM", "H",
          e => new[] { e.Dim1, e.Dim2, e.Dim3, e.Dim4 });

      BuildStruElements(_rawStructureDesignData.BscDesignList, materialID, "CHAN", "BSC", "CHAN",
          e => new[] { e.Dim1, e.Dim2, e.Dim3, e.Dim4 });

      BuildStruElements(_rawStructureDesignData.BulbDesignList, materialID, "BAR", "BULB", "BAR",
          e => new[] { e.Dim1, e.Dim2 });

      BuildStruElements(_rawStructureDesignData.FbarDesignList, materialID, "BAR", "FBAR", "BAR",
          e => new[] { e.Dim1, e.Dim2 });

      BuildStruElements(_rawStructureDesignData.RbarDesignList, materialID, "ROD", "RBAR", "ROD",
          e => new[] { e.Dim1 });

      BuildStruElements(_rawStructureDesignData.TubeDesignList, materialID, "TUBE", "TUBE", "TUBE",
          e => new[] { e.Dim1, e.Dim2 });

      PipeBuild();


      if (_debugPrint) Console.WriteLine("[Builder] FE Model Build Completed Successfully.");
    }


    private void BuildStruElements<T>(
        IEnumerable<T> designList,
        int materialID,
        string propertyShape,
        string rawType,
        string feType,
        Func<T, double[]> dimSelector) where T : StructureEntity
    {
      if (designList == null) return;

      foreach (var entity in designList)
      {
        // 1. Property 치수 추출 및 생성
        double[] inputDim = dimSelector(entity);
        int propertyID = _feModelContext.Properties.AddOrGet(propertyShape, inputDim, materialID);

        // 2. Node 생성 (방어적 코드: 인덱스 범위 확인)
        if (entity.Poss == null || entity.Poss.Length < 3 || entity.Pose == null || entity.Pose.Length < 3)
          continue;

        double[] barOrientation = GeometryUtils.CalculateBarOrientation(entity.Poss, entity.Pose);
        int nodeA_ID = _feModelContext.Nodes.AddOrGet(entity.Poss[0], entity.Poss[1], entity.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(entity.Pose[0], entity.Pose[1], entity.Pose[2]);

        string oriX = "0.0", oriY = "0.0", oriZ = "1.0"; // 기본값
        if (entity.Ori != null && entity.Ori.Length >= 3)
        {
          oriX = entity.Ori[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
          oriY = entity.Ori[1].ToString(System.Globalization.CultureInfo.InvariantCulture);
          oriZ = entity.Ori[2].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ★ [추가된 방어 코드] 시작 노드와 끝 노드가 같으면 (길이가 0이면) 생성 스킵
        if (nodeA_ID == nodeB_ID)
        {
          if (_debugPrint)
            Console.WriteLine($"[Warning] Skipped zero-length Element. ID: {entity.Name} (NodeID: {nodeA_ID})");
          continue;
        }

        // 3. 추가 정보(ExtraData) 매핑
        var extraData = new Dictionary<string, string>
                {
                    { "RawType", rawType },
                    { "FeType", feType },
                    { "ID", entity.Name },
                    { "OriX", oriX },
                    { "OriY", oriY },
                    { "OriZ", oriZ },
                    { "Classification", "Stru" }
                };

        // 4. Element 생성
        try
        {
          _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID }, propertyID, barOrientation, extraData);
        }
        catch (Exception ex)
        {
          if (_debugPrint)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] 구조 부재 생성 실패! Entity ID: {entity.Name}");
            Console.WriteLine($"  -> 상세 에러: {ex.Message}");
            Console.ResetColor();
          }
          continue; // 실패 시 프로그램 중단 없이 다음 부재로 넘어감
        }
      }
    }

    // 제외 대상 : ATTA
    private void PipeBuild()
    {
      foreach (var pipeData in _rawStructureDesignData.PipeList)
      {
        // 1. 공통 데이터 추출 및 질량 변환
        bool isMassValid = double.TryParse(pipeData.Mass,
                                           System.Globalization.NumberStyles.Any,
                                           System.Globalization.CultureInfo.InvariantCulture,
                                           out double massValue);

        Dictionary<string, string?> extraData = new Dictionary<string, string?>();
        extraData["Name"] = pipeData.Name;
        extraData["Branch"] = pipeData.Branch;
        extraData["Rest"] = pipeData.Rest;
        extraData["Mass"] = pipeData.Mass;
        extraData["Category"] = "Pipe";
        extraData["Type"] = pipeData.Type;

        int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

        // =======================================================
        // A. 일반 배관, 엘보, 벤드 (InterPos 처리가 필요한 타입들)
        // =======================================================
        if (pipeData.Type == "TUBI" || pipeData.Type == "OLET" || pipeData.Type == "FLAN" ||
            pipeData.Type == "REDU" || pipeData.Type == "COUP" ||
            pipeData.Type == "ELBO" || pipeData.Type == "BEND")
        {
          double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 };
          int propertyID = _feModelContext.Properties.AddOrGet("TUBE", propertyDim, materialID);

          // 노드 체인 구성 (APos -> InterPos -> LPos)
          List<int> nodeChain = new List<int>();
          nodeChain.Add(_feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]));

          // InterPos가 존재한다면 중간 노드들 추가 (XYZ가 연속된 1차원 배열 형태라고 가정)
          if (pipeData.InterPos != null && pipeData.InterPos.Length >= 3)
          {
            for (int i = 0; i < pipeData.InterPos.Length; i += 3)
            {
              nodeChain.Add(_feModelContext.Nodes.AddOrGet(pipeData.InterPos[i], pipeData.InterPos[i + 1], pipeData.InterPos[i + 2]));
            }
          }

          nodeChain.Add(_feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]));

          // 체인을 따라 연속된 Element 생성
          for (int i = 0; i < nodeChain.Count - 1; i++)
          {
            int n1 = nodeChain[i];
            int n2 = nodeChain[i + 1];
            if (n1 == n2) continue; // 길이 0 방어

            var p1 = _feModelContext.Nodes[n1];
            var p2 = _feModelContext.Nodes[n2];
            double[] barOri = GeometryUtils.CalculateBarOrientation(new[] { p1.X, p1.Y, p1.Z }, new[] { p2.X, p2.Y, p2.Z });

            try
            {
              int eid = _feModelContext.Elements.AddNew(new List<int> { n1, n2 }, propertyID, barOri, extraData);
              if (pipeElementIDsByType.ContainsKey(pipeData.Type)) pipeElementIDsByType[pipeData.Type].Add(eid);
            }
            catch (Exception ex) { LogError(pipeData, ex); }
          }

          // FLAN 등 질량이 있는 경우 Pos 위치에 PointMass 추가
          if (isMassValid && massValue > 0.0)
          {
            int posNode = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
            _feModelContext.PointMasses.AddNew(posNode, massValue, extraData);
          }
        }

        // =======================================================
        // B. TEE (분기가 3개인 배관)
        // =======================================================
        else if (pipeData.Type == "TEE")
        {
          double[] propertyDim1 = new double[] { pipeData.Dim1, pipeData.Dim2 };
          int propertyID1 = _feModelContext.Properties.AddOrGet("TUBE", propertyDim1, materialID);

          int centerNode = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int startNode = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNode = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

          double[] ori1 = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.Pos);
          double[] ori2 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.LPos);

          CreateElementSafe(startNode, centerNode, propertyID1, ori1, extraData, pipeData);
          CreateElementSafe(centerNode, endNode, propertyID1, ori2, extraData, pipeData);

          if (pipeData.P3Pos != null && pipeData.P3Pos.Length >= 3)
          {
            double[] propertyDim2 = new double[] { pipeData.Dim3, pipeData.Dim4 };
            int propertyID2 = _feModelContext.Properties.AddOrGet("TUBE", propertyDim2, materialID);
            int p3Node = _feModelContext.Nodes.AddOrGet(pipeData.P3Pos[0], pipeData.P3Pos[1], pipeData.P3Pos[2]);
            double[] ori3 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.P3Pos);

            CreateElementSafe(centerNode, p3Node, propertyID2, ori3, extraData, pipeData);
          }
        }

        // =======================================================
        // C. VTWA (Tee와 비슷하나 3개의 요소를 교차 생성 + 질량)
        // =======================================================
        else if (pipeData.Type == "VTWA")
        {
          double[] VTWA_Dim = { 35.0, 7.0 };
          int propertyID = _feModelContext.Properties.AddOrGet("TUBE", VTWA_Dim, materialID);

          int centerNode = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int startNode = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNode = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

          double[] ori1 = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.Pos);
          double[] ori2 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.LPos);

          CreateElementSafe(startNode, centerNode, propertyID, ori1, extraData, pipeData);
          CreateElementSafe(centerNode, endNode, propertyID, ori2, extraData, pipeData);

          if (pipeData.P3Pos != null && pipeData.P3Pos.Length >= 3)
          {
            int p3Node = _feModelContext.Nodes.AddOrGet(pipeData.P3Pos[0], pipeData.P3Pos[1], pipeData.P3Pos[2]);
            double[] ori3 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.P3Pos);
            CreateElementSafe(centerNode, p3Node, propertyID, ori3, extraData, pipeData);
          }

          if (isMassValid && massValue > 0.0)
            _feModelContext.PointMasses.AddNew(centerNode, massValue, extraData);
        }

        // =======================================================
        // D. [수정됨] VALV, TRAP, FILT, EXP (Rigid + Mass 전략)
        // =======================================================
        else if (pipeData.Type == "VALV" || pipeData.Type == "TRAP" || pipeData.Type == "FILT" || pipeData.Type == "EXP")
        {
          // 1. 노드 생성 (Pos가 RBE의 중심, APos/LPos가 연결단)
          int centerNode = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int startNode = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNode = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

          if (startNode == endNode)
          {
            if (_debugPrint)
            {
              Console.ForegroundColor = ConsoleColor.Yellow;
              Console.WriteLine($"[Warning] 길이가 0인 {pipeData.Type} 발견. 생성 스킵 -> Name: {pipeData.Name}");
              Console.ResetColor();
            }
            continue; // 찌그러진 데이터 방어
          }

          try
          {
            // 2. 강체(RBE2) 생성: Pos(마스터) -> APos, LPos(슬레이브)
            int rbeId = _feModelContext.Rigids.AddNew(
                independentNodeID: centerNode,
                dependentNodeIDs: new List<int> { startNode, endNode },
                cm: "123456",
                extraData: extraData
            );

            if (pipeElementIDsByType.ContainsKey(pipeData.Type))
            {
              pipeElementIDsByType[pipeData.Type].Add(rbeId);
            }

            // 3. 중심 노드에 질량(PointMass) 부여
            if (isMassValid && massValue > 0.0)
            {
              _feModelContext.PointMasses.AddNew(centerNode, massValue, extraData);
            }
          }
          catch (Exception ex)
          {
            LogError(pipeData, ex);
          }
        }

        // =======================================================
        // E. ATTA, EXP (Point Mass 전용)
        // =======================================================
        else if (pipeData.Type == "ATTA" || pipeData.Type == "EXP")
        {
          if (isMassValid && massValue > 10.0) // 기존에 명시하신 10kg 이상 조건 유지
          {
            var validPipeNodes = _feModelContext.GetNodesUsedInPipeElements();
            var targetPos = new HiTessModelBuilder.Model.Geometry.Point3D(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);

            int closestPipeNodeId = _feModelContext.Nodes.FindClosestValidNode(targetPos, validPipeNodes, tolerance: 100.0);

            if (closestPipeNodeId != -1)
            {
              _feModelContext.PointMasses.AddNew(closestPipeNodeId, massValue, extraData);
            }
            else if (_debugPrint)
            {
              Console.ForegroundColor = ConsoleColor.Yellow;
              Console.WriteLine($"[Warning] 주변에 부착할 배관 노드 없음. ATTA 생략: {pipeData.Name}");
              Console.ResetColor();
            }
          }
        }
      }
    }

    // --- 가독성을 위한 Helper Method (RawFeModelBuilder 클래스 내부에 추가) ---
    private void CreateElementSafe(int n1, int n2, int propId, double[] ori, Dictionary<string, string?> extra, PipeEntity pipe)
    {
      if (n1 == n2) return;
      try
      {
        int eid = _feModelContext.Elements.AddNew(new List<int> { n1, n2 }, propId, ori, extra);
        if (pipeElementIDsByType.ContainsKey(pipe.Type)) pipeElementIDsByType[pipe.Type].Add(eid);
      }
      catch (Exception ex) { LogError(pipe, ex); }
    }

    private void LogError(PipeEntity pipe, Exception ex)
    {
      if (!_debugPrint) return;
      Console.ForegroundColor = ConsoleColor.Red;
      Console.WriteLine($"[Error] {pipe.Type} 생성 실패! Name: {pipe.Name} / 에러: {ex.Message}");
      Console.ResetColor();
    }
  }
}
