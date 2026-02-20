using System.Collections.Generic;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// CSV에서 파싱된 모든 구조물 데이터를 타입별로 분류하여 보관하는 컨테이너 클래스입니다.
  /// </summary>
  public class RawStructureDesignData
  {
    /// <summary>Angle(ㄱ형강) 데이터 리스트</summary>
    public List<AngDesignData> AngDesignList { get; init; }

    /// <summary>Beam(H/I형강) 데이터 리스트</summary>
    public List<BeamDesignData> BeamDesignList { get; init; }

    /// <summary>Bsc(Box) 데이터 리스트</summary>
    public List<BscDesignData> BscDesignList { get; init; }

    /// <summary>Bulb(구평형강) 데이터 리스트</summary>
    public List<BulbDesignData> BulbDesignList { get; init; }

    /// <summary>Round Bar(환봉) 데이터 리스트</summary>
    public List<RbarDesignData> RbarDesignList { get; init; }

    /// <summary>분류되지 않은 데이터 리스트</summary>
    public List<UnknownDesignData> UnknownDesignList { get; init; }

    public RawStructureDesignData(
        List<AngDesignData> angDesignList,
        List<BeamDesignData> beamDesignList,
        List<BscDesignData> bscDesignList,
        List<BulbDesignData> bulbDesignList,
        List<RbarDesignData> rbarDesignList,
        List<UnknownDesignData> unknownDesignList)
    {
      AngDesignList = angDesignList;
      BeamDesignList = beamDesignList;
      BscDesignList = bscDesignList;
      BulbDesignList = bulbDesignList;
      RbarDesignList = rbarDesignList;
      UnknownDesignList = unknownDesignList;
    }
  }
}
