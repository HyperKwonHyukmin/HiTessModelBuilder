using System;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// 배관(Pipe) CSV 한 줄에서 만들어지는 단일 데이터 엔티티입니다.
  /// </summary>
  public class PipeEntity // abstract 제거, 단일 클래스로 통일
  {
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double[] Pos { get; set; } = Array.Empty<double>();
    public double[] APos { get; set; } = Array.Empty<double>();
    public double[] LPos { get; set; } = Array.Empty<double>();
    public string Branch { get; set; } = string.Empty;
    public double OutDia { get; set; }
    public double Thick { get; set; }
    public double[] Normal { get; set; } = Array.Empty<double>();
    public double[]? InterPos { get; set; }
    public double[]? P3Pos { get; set; }
    public double OutDia2 { get; set; }
    public double Thick2 { get; set; }
    public string? Rest { get; set; } 
    public string? Mass { get; set; }
    public string Remark { get; set; }  

    // ==========================================
    // FE 모델 생성을 위한 편의 프로퍼티 (선택적 사용)
    // ==========================================
    // 메인 배관용 치수 (반지름 계산)
    public double Dim1 => Math.Round(OutDia / 2.0, 3);
    public double Dim2 => Math.Round((OutDia / 2.0) - Thick, 3);

    // TEE 분기관용 치수 (반지름 계산)
    public double Dim3 => Math.Round(OutDia2 / 2.0, 3);
    public double Dim4 => Math.Round((OutDia2 / 2.0) - Thick2, 3);

    public override string ToString()
    {
      // :0.1 대신 :F1 (소수점 1자리 고정) 또는 :0.0 을 사용해야 합니다!
      var normal = (Normal != null && Normal.Length >= 3) ? $"({Normal[0]:F1}, {Normal[1]:F1}, {Normal[2]:F1})" : "(?)";
      return $"[PipeEntity] Name={Name}, Type={Type}, OD={OutDia}, t={Thick}, Normal={normal}";
    }
  }
}
