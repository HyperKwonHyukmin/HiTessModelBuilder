using HiTessModelBuilder.Model.Entities;
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
    private readonly RawStructureDesignData _rawStructureDesignData;
    private readonly FeModelContext _feModelContext;
    private readonly bool _debugPrint;

    public RawFeModelBuilder(
        RawStructureDesignData rawStructureDesignData,
        FeModelContext feModelContext,
        bool debugPrint = false)
    {
      _rawStructureDesignData = rawStructureDesignData ?? throw new ArgumentNullException(nameof(rawStructureDesignData));
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
      BuildElements(_rawStructureDesignData.AngDesignList, materialID, "L", "ANGLE", "L",
          e => new[] { e.Dim1, e.Dim2, e.Dim3, e.Dim3 });

      BuildElements(_rawStructureDesignData.BeamDesignList, materialID, "H", "BEAM", "H",
          e => new[] { e.Dim1, e.Dim2, e.Dim3, e.Dim4 });

      BuildElements(_rawStructureDesignData.BscDesignList, materialID, "CHAN", "BSC", "CHAN",
          e => new[] { e.Dim1, e.Dim2, e.Dim3, e.Dim4 });

      BuildElements(_rawStructureDesignData.BulbDesignList, materialID, "BAR", "BULB", "BAR",
          e => new[] { e.Dim1, e.Dim2 });

      BuildElements(_rawStructureDesignData.RbarDesignList, materialID, "ROD", "RBAR", "ROD",
          e => new[] { e.Dim1 });

      BuildElements(_rawStructureDesignData.TubeDesignList, materialID, "TUBE", "TUBE", "TUBE",
          e => new[] { e.Dim1 , e.Dim2});

      if (_debugPrint) Console.WriteLine("[Builder] FE Model Build Completed Successfully.");
    }

    /// <summary>
    /// 반복되는 Node, Property, Element 생성 로직을 처리하는 제네릭 헬퍼 메서드입니다.
    /// </summary>
    private void BuildElements<T>(
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

        int nodeA_ID = _feModelContext.Nodes.AddOrGet(entity.Poss[0], entity.Poss[1], entity.Poss[2]);
        int nodeB_ID = _feModelContext.Nodes.AddOrGet(entity.Pose[0], entity.Pose[1], entity.Pose[2]);

        // 3. 추가 정보(ExtraData) 매핑
        var extraData = new Dictionary<string, string>
                {
                    { "RawType", rawType },
                    { "FeType", feType },
                    { "ID", entity.Name }
                };

        // 4. Element 생성
        _feModelContext.Elements.AddNew(new List<int> { nodeA_ID, nodeB_ID }, propertyID, extraData);
      }
    }
  }
}
