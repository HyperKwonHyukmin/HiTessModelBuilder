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

  // --- 구체 클래스 (Size 타입에 따른 다형성 구현) ---
  public class AngStructure : StructureEntity { }
  public class BeamStructure : StructureEntity { }
  public class BscStructure : StructureEntity { }
  public class BulbStructure : StructureEntity { }
  public class RbarStructure : StructureEntity { }
  public class UnknownStructure : StructureEntity { }
}
