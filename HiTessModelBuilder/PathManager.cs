using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder
{
  public static class PathManager
  {
    // 규칙:
    // - Stru : 필수 (null 불가)
    // - Pipe : 옵션 (없으면 null)
    // - Equip: 옵션 (없으면 null)

    private static readonly (string Stru, string? Pipe, string? Equip) Case1 = (
      @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\Case_01\GSS_SKID-SS_SK-struData-READONLY_20250723.csv",
      null,
      null
    );

    private static readonly (string Stru, string? Pipe, string? Equip) Case2 = (
      @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\KangSangHoon_csv\3515\02\3515-35020-struData-READONLY_20250731.csv",
      @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\KangSangHoon_csv\3515\02\3515-35020-pipeData-READONLY_20250731.csv",
      null
    );

    private static readonly (string Stru, string? Pipe, string? Equip) Case3 = (
      @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\KangSangHoon_csv\3516_88k_VLAC_LPG DF\M07\3516-35070-struData-A503741_20251213.csv",
      null,
      null
    );

    private static readonly (string Stru, string? Pipe, string? Equip) Case4 = (
      @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\KangSangHoon_csv\3454\35020\3454-35020-struData-A505080_20251021.csv",
      null,
      null
    );

    private static readonly (string Stru, string? Pipe, string? Equip) Case5 = (
     @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\KangSangHoon_csv\3468_93k_VLAC_NH3 DF\M03\3468-35030-struData-A453412_20260108.csv",
     null,
     null
   );

    private static readonly (string Stru, string? Pipe, string? Equip) Case6 = (
     @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\KangSangHoon_csv\3496_98k_VLEC\35210\3496-35210-struData-A508372_20260108.csv",
     null,
     null
    );

    // 삼호 임시 테스트 
    private static readonly (string Stru, string? Pipe, string? Equip) Case7 = (
     @"C:\Users\HHI\Desktop\temp\846706--struData-D317616_20260223.csv",
     null,
     null
    );


    public static (string Stru, string? Pipe, string? Equip) Current = Case2;

    
  }
}
