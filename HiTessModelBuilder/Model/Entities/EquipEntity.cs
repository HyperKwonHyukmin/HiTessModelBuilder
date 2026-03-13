using System;

namespace HiTessModelBuilder.Model.Entities
{
  public class EquipEntity
  {
    public string Name { get; set; } = string.Empty;

    // 지지대(Mounting) 좌표 배열 (다수의 다리가 올 수 있으므로 배열 처리)
    public double[] Pos { get; set; } = Array.Empty<double>();

    // 무게 중심(Center of Gravity) 좌표 (X, Y, Z)
    public double[] Cog { get; set; } = Array.Empty<double>();

    // 추가 지지대 또는 Bounding Box 좌표 배열
    public double[] InterPos { get; set; } = Array.Empty<double>();

    public double Mass { get; set; }  // 건조 중량 (Dry Weight)
    public double Wvol { get; set; }  // 내부 유체 중량 (Operating Fluid Weight)

    // 총 운전 하량(Operating Weight) = Mass + Wvol
    public double OperatingMass => Mass + Wvol;
  }
}