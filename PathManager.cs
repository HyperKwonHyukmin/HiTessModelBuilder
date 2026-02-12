using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HiTessModelBuilder
{
  public static class PathManager
  {
    // ✅ 규칙:
    // - Stru : 필수 (null 불가)
    // - Pipe : 옵션 (없으면 null)
    // - Equip: 옵션 (없으면 null)

    // Case 1: Stru만 있는 케이스
    private static readonly (string Stru, string? Pipe, string? Equip) Case1 = (
      @"C:\Coding\Csharp\Projects\HiTessModelBuilder\HiTessModelBuilder\csv\Case_01\GSS_SKID-SS_SK-struData-READONLY_20250716.csv",
      null,
      null
    );    


    public static (string Stru, string? Pipe, string? Equip) Current = Case1;

    
  }
}
