using System;
using System.Linq;
using HiTessModelBuilder.Model.Entities;

namespace HiTessModelBuilder.Pipeline.Utils
{
  /// <summary>
  /// 요소의 물리적 단면(Property) 속성으로부터 치수 정보를 계산하고 추출하는 유틸리티 클래스입니다.
  /// </summary>
  public static class PropertyDimensionHelper
  {
    /// <summary>
    /// 주어진 Property의 형상(Type)을 기반으로 가장 큰 단면 치수(반경 또는 폭/높이)를 반환합니다.
    /// 교차 및 연장(Extend) 허용 오차 계산 시 사용됩니다.
    /// </summary>
    /// <param name="prop">치수를 추출할 Property 객체</param>
    /// <returns>단면의 최대 치수 (double)</returns>
    public static double GetMaxCrossSectionDim(Property prop)
    {
      var dim = prop.Dim;
      if (dim == null || dim.Count == 0) return 0.0;

      string type = prop.Type.ToUpper();
      return type switch
      {
        "L" => Math.Max(dim.ElementAtOrDefault(0), dim.ElementAtOrDefault(1)),
        "H" => Math.Max(dim.ElementAtOrDefault(0), dim.ElementAtOrDefault(2)),
        "TUBE" => dim.ElementAtOrDefault(0),
        "ROD" => dim.ElementAtOrDefault(0),
        "BAR" => Math.Max(dim.ElementAtOrDefault(0), dim.ElementAtOrDefault(1)),
        "CHAN" => dim.Max(),
        _ => dim.Max()
      };
    }
  }
}