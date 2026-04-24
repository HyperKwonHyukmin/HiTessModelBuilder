namespace Cmb.Core.Model;

public sealed record BeamSection(int Id, BeamSectionKind Kind, double[] Dims, int MaterialId)
{
    public bool DimensionsEqual(BeamSection other)
    {
        if (Kind != other.Kind) return false;
        if (Dims.Length != other.Dims.Length) return false;
        for (var i = 0; i < Dims.Length; i++)
            if (Math.Abs(Dims[i] - other.Dims[i]) > 1e-9) return false;
        return true;
    }
}
