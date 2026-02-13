using HiTessModelBuilder.Model.Geometry;

namespace HiTessModelBuilder.Model.Entities
{
  /// <summary>
  /// 구조물 CSV 데이터 중 핵심 6개 필드를 정의하는 추상 클래스
  /// </summary>
  public abstract class StructureEntity
  {
    // 1. Name
    public string Name { get; set; }

    // 2. Position (Center or Ref Point)
    public Point3D Pos { get; set; }

    // 3. Start Position
    public Point3D Poss { get; set; }

    // 4. End Position
    public Point3D Pose { get; set; }

    // 5. Size (Raw String, e.g., "ANG_65x65x6")
    public string SizeRaw { get; set; }

    // 6. Orientation (Direction Vector)
    public Vector3D Ori { get; set; }

    public override string ToString()
    {
      return $"[{GetType().Name}] {Name}, Size:{SizeRaw}, Ori:({Ori.X},{Ori.Y},{Ori.Z})";
    }
  }

  // L자 형태 
  public class AngStructure : StructureEntity 
  {
    //+       65.0    65.0    6.0     6.0     0.0    
    public double Width { get; set; }
    public double Height { get; set; }
    public double Thickness { get; set; }

    /// <summary>
    /// Nastran 출력용
    /// </summary>
    public double Dim1 => Width;
    public double Dim2 => Height;
    public double Dim3 => Thickness;

  }

  // Beam은 H형태
  public class BeamStructure : StructureEntity
  {
    // +          176.0    24.0   200.0     8.00.0      
    public double Width { get; set; }
    public double Height { get; set; }
    public double InnerThickness { get; set; }  
    public double OuterThickness { get; set; }

    /// <summary>
    /// Nastran 출력용
    /// </summary>
    public double Dim1 => Width - (2 * OuterThickness);
    public double Dim2 => 2 * OuterThickness;
    public double Dim3 => Height;
    public double Dim4 => InnerThickness;

  }

  // Bsc는 Channel 형태 
  public class BscStructure : StructureEntity 
  {
    //+           75.0   150.0     6.5    10.00.0     
    public double Height { get; set; }
    public double Width { get; set; }
    public double InnerThickness { get; set; }
    public double OuterThickness { get; set; }

    /// <summary>
    /// Nastran 출력용
    /// </summary>
    public double Dim1 => Width;
    public double Dim2 => Height;
    public double Dim3 => InnerThickness;
    public double Dim4 => OuterThickness;

  }

  // Bulb는 Bar 형태
  public class BulbStructure : StructureEntity 
  {
    //+           70.0    10.00.0     
    public double Width { get; set; }
    public double Thickness { get; set; }

    /// <summary>
    /// Nastran 출력용
    /// </summary>
    public double Dim1 => Width;
    public double Dim2 => Thickness;
  }

  // Rbar는 Rod 형태
  public class RbarStructure : StructureEntity 
  { 
    public double Diameter { get; set; }

    /// <summary>
    /// Nastran 출력용
    /// </summary>
    public double Dim1 => Math.Round(Diameter / 2,1);
  }
  public class UnknownStructure : StructureEntity { }
}
