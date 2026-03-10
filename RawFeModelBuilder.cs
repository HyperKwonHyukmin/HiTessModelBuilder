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
        // [신규 추가] 엔티티의 Weld 정보를 읽어 전역 컨텍스트에 용접 노드로 등록
        string weldInfo = entity.Weld?.ToLowerInvariant() ?? "";
        if (weldInfo == "start") _feModelContext.WeldNodes.Add(nodeA_ID);
        if (weldInfo == "end") _feModelContext.WeldNodes.Add(nodeB_ID);

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
    // 제외 대상 : ATTA (기존 주석 유지)
    private void PipeBuild()
    {
      // 1. 배관 전담 빌더 인스턴스 생성
      var pipeBuilder = new PipeModelBuilder(_feModelContext, pipeElementIDsByType, _debugPrint);

      // 2. 파싱된 배관 리스트를 전달하여 빌드 실행
      pipeBuilder.Build(_rawStructureDesignData.PipeList);
    }   
  }
}
