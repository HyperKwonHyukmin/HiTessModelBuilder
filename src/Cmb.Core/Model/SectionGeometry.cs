namespace Cmb.Core.Model;

public static class SectionGeometry
{
    /// <summary>
    /// 단면 종류별로 3D 볼륨의 최대 치수를 반환합니다 (mm).
    /// 1D centerline 추출 시 발생하는 용접 오프셋 추정에 사용됩니다.
    /// </summary>
    public static double MaxCrossSectionDim(this BeamSection s)
    {
        if (s.Dims is null || s.Dims.Length == 0) return 0.0;
        return s.Kind switch
        {
            BeamSectionKind.H       => Math.Max(At(s, 0), At(s, 2)), // flange width vs web height
            BeamSectionKind.L       => Math.Max(At(s, 0), At(s, 1)),
            BeamSectionKind.Tube    => At(s, 0),                      // outer diameter
            BeamSectionKind.Rod     => At(s, 0),
            BeamSectionKind.Bar     => Math.Max(At(s, 0), At(s, 1)),
            BeamSectionKind.Box     => Math.Max(At(s, 0), At(s, 1)),
            BeamSectionKind.Channel => s.Dims.Max(),
            _                       => s.Dims.Max(),
        };
    }

    private static double At(BeamSection s, int i)
        => i < s.Dims.Length ? s.Dims[i] : 0.0;
}
