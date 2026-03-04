using HiTessModelBuilder.Model.Entities;
using HiTessModelBuilder.Pipeline.Utils;
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
          e => new[] { e.Dim1 , e.Dim2});

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
        // 1. Property 치수 추출 및 생
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
        _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID }, propertyID, barOrientation,extraData);
      }
    }

    private void PipeBuild()
    {
      foreach(var pipeData in _rawStructureDesignData.PipeList)
      {
        Dictionary<string, string?> extraData = new Dictionary<string, string?>();
        extraData["Name"] = pipeData.Name;
        extraData["Branch"] = pipeData.Branch;
        extraData["Rest"] = pipeData.Rest;
        extraData["Mass"] = pipeData.Mass;

        if ((pipeData.Type == "TUBI") || (pipeData.Type == "OLET") || (pipeData.Type == "FLAN") || (pipeData.Type == "REDU"))
        {
          Console.WriteLine(pipeData);
          // extraData 정의 : 추가 정보는 여기에 다 때려넣기 
          extraData["Type"] = pipeData.Type;
          int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

          double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 };
          int propertyID = _feModelContext.Properties.AddOrGet("TUBE", propertyDim, materialID);

          // 1. 방향 벡터(Direction Vector) 계산: 료점(LPos) - 시작점(APos)
          double dx = pipeData.LPos[0] - pipeData.APos[0];
          double dy = pipeData.LPos[1] - pipeData.APos[1];
          double dz = pipeData.LPos[2] - pipeData.APos[2];          

          // 2. 직교 벡터(Orientation Vector) 계산
          double[] barOrientation = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.LPos);
          int startNodeID = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNodeID = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);
          int elementID = _feModelContext.Elements.AddNew(new List<int> { startNodeID, endNodeID }, propertyID, barOrientation, extraData);

          if (pipeElementIDsByType.ContainsKey(pipeData.Type))
            pipeElementIDsByType[pipeData.Type].Add(elementID);

          // 3. 디버그 출력: 방향 벡터와 직교 벡터를 나란히 출력하여 비교
          //double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
          //if (len > 1e-12) { dx /= len; dy /= len; dz /= len; }    
          //Console.WriteLine($"[Element {elementID}]");
          //Console.WriteLine($"  -> Dir(검지) : {dx:F3}, {dy:F3}, {dz:F3}");
          //Console.WriteLine($"  -> Ori(엄지) : {barOrientation[0]:F3}, {barOrientation[1]:F3}, {barOrientation[2]:F3}");
        }

        else if (pipeData.Type == "TRAP")
        {
          extraData["Type"] = pipeData.Type;
          int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

          double[] propertyDim = new double[] { 20.0, 10.0 }; // Trap은 자동 할당이 어려워서 고정
          int propertyID = _feModelContext.Properties.AddOrGet("TUBE", propertyDim, materialID);

          double[] barOrientation1 = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.Pos);
          double[] barOrientation2 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.LPos);
          int startNodeID = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int betweenNodeID = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int endNodeID = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);
          int elementID1 = _feModelContext.Elements.AddNew(
            new List<int> { startNodeID, betweenNodeID }, propertyID, barOrientation1, extraData);
          int elementID2 = _feModelContext.Elements.AddNew(
            new List<int> { betweenNodeID, endNodeID }, propertyID, barOrientation2, extraData);

          if (pipeElementIDsByType.ContainsKey(pipeData.Type))
          {
            pipeElementIDsByType[pipeData.Type].Add(elementID1);
            pipeElementIDsByType[pipeData.Type].Add(elementID2);
          }

        }

        else if (pipeData.Type == "TEE")
        {
          extraData["Type"] = pipeData.Type;
          int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

          double[] propertyDim1 = new double[] { pipeData.Dim1, pipeData.Dim2 };
          double[] propertyDim2 = new double[] { pipeData.Dim3, pipeData.Dim4 };
          int propertyID1 = _feModelContext.Properties.AddOrGet("TUBE", propertyDim1, materialID);
          int propertyID2 = _feModelContext.Properties.AddOrGet("TUBE", propertyDim2, materialID);

          double[] barOrientation1 = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.LPos);
          double[] barOrientation2 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.P3Pos);

          int betweenNodeID = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int startNodeID = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNodeID = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);
          int p3PosNodeID = _feModelContext.Nodes.AddOrGet(pipeData.P3Pos[0], pipeData.P3Pos[1], pipeData.P3Pos[2]);

          try
          {
            int elementID1 = _feModelContext.Elements.AddNew(
            new List<int> { startNodeID, betweenNodeID }, propertyID1, barOrientation1, extraData);
            int elementID2 = _feModelContext.Elements.AddNew(
              new List<int> { betweenNodeID, endNodeID }, propertyID1, barOrientation1, extraData);
            int elementID3 = _feModelContext.Elements.AddNew(
              new List<int> { betweenNodeID, p3PosNodeID }, propertyID2, barOrientation2, extraData);

            if (pipeElementIDsByType.ContainsKey(pipeData.Type))
            {
              pipeElementIDsByType[pipeData.Type].Add(elementID1);
              pipeElementIDsByType[pipeData.Type].Add(elementID2);
              pipeElementIDsByType[pipeData.Type].Add(elementID3);
            }
          }
          catch (Exception ex)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] 배관 요소 생성 실패! Pipe Name: {pipeData.Name}");
            Console.WriteLine($"  -> 상세 에러: {ex.Message}");
            Console.ResetColor();

            // 에러를 무시하고 다음 배관으로 넘어갈지, 아니면 프로그램을 중단할지에 따라 선택
            continue; // 넘어가려면 continue; 중단하려면 throw; 사용
          }
        }         
        

        else if ((pipeData.Type == "ELBO")|| (pipeData.Type == "BEND"))
        {
          extraData["Type"] = pipeData.Type;
          int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

          double[] propertyDim = new double[] { pipeData.Dim1, pipeData.Dim2 }; 
          int propertyID = _feModelContext.Properties.AddOrGet("TUBE", propertyDim, materialID);

          double[] barOrientation1 = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.Pos);
          double[] barOrientation2 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.LPos);
          int betweenNodeID = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int startNodeID = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNodeID = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

          int elementID1 = _feModelContext.Elements.AddNew(
            new List<int> { startNodeID, betweenNodeID }, propertyID, barOrientation1, extraData);
          int elementID2 = _feModelContext.Elements.AddNew(
            new List<int> { betweenNodeID, endNodeID }, propertyID, barOrientation2, extraData);

          if (pipeElementIDsByType.ContainsKey(pipeData.Type))
          {
            pipeElementIDsByType[pipeData.Type].Add(elementID1);
            pipeElementIDsByType[pipeData.Type].Add(elementID2);
          }
        }

        else if (pipeData.Type == "VALV") 
        {
          extraData["Type"] = pipeData.Type;
          int materialID = _feModelContext.Materials.AddOrGet("Steel", 206000, 0.3, 7.85e-09);

          double[] propertyDim = new double[] { 2.0, 1.0 };
          int propertyID = _feModelContext.Properties.AddOrGet("TUBE", propertyDim, materialID);

          double[] barOrientation1 = GeometryUtils.CalculateBarOrientation(pipeData.APos, pipeData.Pos);
          double[] barOrientation2 = GeometryUtils.CalculateBarOrientation(pipeData.Pos, pipeData.LPos);
          int betweenNodeID = _feModelContext.Nodes.AddOrGet(pipeData.Pos[0], pipeData.Pos[1], pipeData.Pos[2]);
          int startNodeID = _feModelContext.Nodes.AddOrGet(pipeData.APos[0], pipeData.APos[1], pipeData.APos[2]);
          int endNodeID = _feModelContext.Nodes.AddOrGet(pipeData.LPos[0], pipeData.LPos[1], pipeData.LPos[2]);

          int elementID1 = _feModelContext.Elements.AddNew(
            new List<int> { startNodeID, betweenNodeID }, propertyID, barOrientation1, extraData);
          int elementID2 = _feModelContext.Elements.AddNew(
            new List<int> { betweenNodeID, endNodeID }, propertyID, barOrientation2, extraData);

          if (pipeElementIDsByType.ContainsKey(pipeData.Type))
          {
            pipeElementIDsByType[pipeData.Type].Add(elementID1);
            pipeElementIDsByType[pipeData.Type].Add(elementID2);
          }
        }

        else if (pipeData.Type == "UBOLT")
        {

        }


      }
    }
  }
}
