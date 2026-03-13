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
    private readonly bool _forceUboltRigid;
    private readonly bool _debugPrint;

    public RawFeModelBuilder(
        RawCsvDesignData? StructureData,
        FeModelContext feModelContext,
        bool forceUboltRigid = false,
        bool debugPrint = false)
    {
      _rawStructureDesignData = StructureData ?? throw new ArgumentNullException(nameof(StructureData));
      _feModelContext = feModelContext ?? throw new ArgumentNullException(nameof(feModelContext));
      _forceUboltRigid = forceUboltRigid;
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
      EquipBuild();

      // ★ [사각지대 1] 파싱은 되었으나 지원하지 않는 타입이라 생성에서 누락된 부재 로그 출력
      if (_rawStructureDesignData.UnknownDesignList != null)
      {
        foreach (var unknown in _rawStructureDesignData.UnknownDesignList)
        {
          Console.ForegroundColor = ConsoleColor.Yellow;
          Console.WriteLine($"[생성 누락] 지원하지 않는 형상 타입({unknown.Type})으로 생성이 취소되었습니다. Name: '{unknown.Name}'");
          Console.ResetColor();
        }
      }

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
        {
          // ★ [사각지대 3] 좌표 데이터 불량 누락 로그 추가
          Console.ForegroundColor = ConsoleColor.Yellow;
          Console.WriteLine($"[생성 누락] 시작/끝 좌표 데이터 불량으로 생성이 취소되었습니다. Name: '{entity.Name}'");
          Console.ResetColor();
          continue;
        }

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

        if (nodeA_ID == nodeB_ID)
        {
          Console.ForegroundColor = ConsoleColor.Yellow;
          if (_debugPrint)
            Console.WriteLine($"[생성 누락] 시작점과 끝점이 같아(길이 0) 부재 생성이 취소되었습니다. Name: '{entity.Name}'");
          Console.ResetColor();
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
          // ★ _debugPrint 조건 제거! 실패 원인 무조건 출력
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"[생성 실패] 구조 부재 생성 중 예외 발생! Name: '{entity.Name}' (사유: {ex.Message})");
          Console.ResetColor();
          continue;
        }
      }
    }

    private void PipeBuild()
    {
      // 1. 배관 전담 빌더 인스턴스 생성
      // [수정됨] useFluidDensity 파라미터를 true로 전달하여 내부 유체 질량 보정을 활성화합니다.
      bool useFluidDensity = true;
      var pipeBuilder = new PipeModelBuilder(_feModelContext, pipeElementIDsByType, useFluidDensity, _forceUboltRigid, _debugPrint);

      // 2. 파싱된 배관 리스트를 전달하여 빌드 실행
      pipeBuilder.Build(_rawStructureDesignData.PipeList);
    }

    private void EquipBuild()
    {
      if (_rawStructureDesignData.EquipList == null || _rawStructureDesignData.EquipList.Count == 0) return;

      // 장비가 허공에 매달리지 않도록, 현재 구조물 및 배관에 사용 중인 '유효한 노드' 목록을 가져옵니다.
      var validNodes = _feModelContext.GetNodesUsedInElements();
      int equipCount = 0;

      foreach (var eq in _rawStructureDesignData.EquipList)
      {
        if (eq.Cog == null || eq.Cog.Length < 3) continue;

        var extraData = new Dictionary<string, string> { { "Name", eq.Name }, { "Classification", "Equip" } };
        var cogPos = new Point3D(eq.Cog[0], eq.Cog[1], eq.Cog[2]);

        // [Case 1] InterPos가 없는 경우: 장비 COG 위치에 직접 Point Mass만 생성 (equip_example 라인 40 참조)
        if (eq.InterPos == null || eq.InterPos.Length == 0)
        {
          // 10mm 이내의 기존 노드 탐색
          int targetNode = _feModelContext.Nodes.FindClosestValidNode(cogPos, validNodes, tolerance: 10.0);
          if (targetNode != -1)
          {
            double massInTon = eq.OperatingMass * 0.001; // [수정됨] kg -> ton 변환
            _feModelContext.PointMasses.AddNew(targetNode, massInTon, extraData);
            equipCount++;
          }
        }
        // [Case 2] InterPos가 있는 경우: COG 노드를 만들고 주변 다리(Dependent)를 찾아 RBE2로 연결
        else
        {
          var dependentNodes = new HashSet<int>();

          // 다리(Mounting Points)들을 순회하며 10mm 이내 노드 찾기
          for (int i = 0; i <= eq.InterPos.Length - 3; i += 3)
          {
            var mntPos = new Point3D(eq.InterPos[i], eq.InterPos[i + 1], eq.InterPos[i + 2]);
            int depNode = _feModelContext.Nodes.FindClosestValidNode(mntPos, validNodes, tolerance: 10.0);

            if (depNode != -1)
            {
              dependentNodes.Add(depNode);
            }
          }

          // 연결할 다리가 1개라도 있다면 RBE와 Mass 생성
          if (dependentNodes.Count > 0)
          {
            int cogNodeId = _feModelContext.Nodes.AddOrGet(cogPos.X, cogPos.Y, cogPos.Z);

            double massInTon = eq.OperatingMass * 0.001; // [수정됨] kg -> ton 변환
            _feModelContext.PointMasses.AddNew(cogNodeId, massInTon, extraData);
            _feModelContext.Rigids.AddNew(cogNodeId, dependentNodes, "123456", extraData);
            equipCount++;
          }
        }
      }

      if (_debugPrint)
        Console.WriteLine($"[Build] 장비(Equipment) {equipCount}개 연결 및 생성 완료.");
    }
  }
}
