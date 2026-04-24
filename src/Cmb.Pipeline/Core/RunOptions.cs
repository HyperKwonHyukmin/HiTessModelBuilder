namespace Cmb.Pipeline.Core;

public sealed record Tolerances(
    double NodeMergeMm              = 1.0,
    double IntersectionSnapMm       = 5.0,
    double ZeroLengthMm             = 0.01,
    double UboltSnapMaxDistMm       = 50.0,
    double ShortElemMinMm           = 5.0,
    double SpatialCellSizeMm        = 200.0,
    double MeshingMaxLengthStructure = 2000.0,
    double MeshingMaxLengthPipe     = 1000.0,
    double NodeOnSegmentTolMm       = 0.5,
    int    MaxConvergenceIterations = 50);

public sealed record RunOptions(
    Tolerances Tolerances,
    string?    StopAfterStage = null,
    bool       RecordTrace    = true)
{
    public static RunOptions Default => new(new Tolerances());
}
