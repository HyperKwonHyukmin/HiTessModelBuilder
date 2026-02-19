namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// 구조물 CSV 한 줄에서 만들어지는 공통 엔티티
  /// </summary>
  public abstract class StructureEntity
  {
    // 0. Name
    public string Name { get; set; } = string.Empty;

    // Size prefix (ANG/BEAM/...)
    public string Type { get; set; } = string.Empty;

    // 원본 Size 문자열 (예: "ANG_65x65x6")
    public string SizeText { get; set; } = string.Empty;

    // 숫자 치수 배열 (예: [65,65,6])
    public double[] SizeDims { get; set; } = Array.Empty<double>();

    // 좌표/방향
    public double[] Poss { get; set; } = Array.Empty<double>();
    public double[] Pose { get; set; } = Array.Empty<double>();
    public double[] Ori { get; set; } = Array.Empty<double>();

    /// <summary>
    /// SizeDims를 타입별 속성(Width, Height...)에 반영
    /// </summary>
    public virtual void ApplyDims(double[] dims) { }

    public override string ToString()
    {
      var dims = SizeDims == null ? "" : string.Join("x", SizeDims.Select(d => d.ToString("0.###")));
      var ori = (Ori != null && Ori.Length >= 3) ? $"({Ori[0]:0.###},{Ori[1]:0.###},{Ori[2]:0.###})" : "(?)";
      return $"[{GetType().Name}] Name={Name}, Type={Type}, Size={SizeText}, Dims={dims}, Ori={ori}";
    }
  }

  public sealed class AngDesignData : StructureEntity
  {
    public double Width { get; set; }
    public double Height { get; set; }
    public double Thickness { get; set; }

    public double Dim1 => Width;
    public double Dim2 => Height;
    public double Dim3 => Thickness;

    public override void ApplyDims(double[] dims)
    {
      // 예: [65,65,6] 또는 [65,65,6,6,0] 같은 변형도 있을 수 있어 유연하게
      if (dims == null || dims.Length < 3) return;
      Width = dims[0];
      Height = dims[1];
      Thickness = dims[2];
    }
  }

  public sealed class BeamDesignData : StructureEntity
  {
    public double Width { get; set; }
    public double Height { get; set; }
    public double InnerThickness { get; set; }
    public double OuterThickness { get; set; }

    // Nastran 출력용
    public double Dim1 => Width - (2 * OuterThickness);
    public double Dim2 => 2 * OuterThickness;
    public double Dim3 => Height;
    public double Dim4 => InnerThickness;

    public override void ApplyDims(double[] dims)
    {
      // 예: [176, 24, 200, 8] 가정
      if (dims == null || dims.Length < 4) return;
      Width = dims[0];
      InnerThickness = dims[1];
      Height = dims[2];
      OuterThickness = dims[3];
    }
  }

  public sealed class BscDesignData : StructureEntity
  {
    public double Height { get; set; }
    public double Width { get; set; }
    public double InnerThickness { get; set; }
    public double OuterThickness { get; set; }

    public double Dim1 => Width;
    public double Dim2 => Height;
    public double Dim3 => InnerThickness;
    public double Dim4 => OuterThickness;

    public override void ApplyDims(double[] dims)
    {
      // 예: [75,150,6.5,10]
      if (dims == null || dims.Length < 4) return;
      Height = dims[0];
      Width = dims[1];
      InnerThickness = dims[2];
      OuterThickness = dims[3];
    }
  }

  public sealed class BulbDesignData : StructureEntity
  {
    public double Width { get; set; }
    public double Thickness { get; set; }

    public double Dim1 => Width;
    public double Dim2 => Thickness;

    public override void ApplyDims(double[] dims)
    {
      // 예: [70,10]
      if (dims == null || dims.Length < 2) return;
      Width = dims[0];
      Thickness = dims[1];
    }
  }

  public sealed class RbarDesignData : StructureEntity
  {
    public double Diameter { get; set; }
    public double Dim1 => Math.Round(Diameter / 2, 1);

    public override void ApplyDims(double[] dims)
    {
      if (dims == null || dims.Length < 1) return;
      Diameter = dims[0];
    }
  }

  public sealed class UnknownDesignData : StructureEntity
  {
    // 필요하면 여기서 추가 필드(원본 라인 등) 붙여도 됨
    public string RawLine { get; set; } = string.Empty;
  }
  
}
