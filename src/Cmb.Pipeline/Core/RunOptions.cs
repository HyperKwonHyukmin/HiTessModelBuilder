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
    double DanglingShortLengthMm    = 50.0,
    double CollinearMergeDistanceMm = 50.0,
    double CollinearMergeAngleDeg   = 3.0,
    double CollinearMergeLateralMm  = 1.0,
    double GroupConnectLocalTolMm    = 40.0,
    double GroupConnectExtraMarginMm = 50.0,
    double ExtendExtraMarginMm      = 100.0,
    double ExtendCoplanarTolMm      = 10.0,
    double ExtendSnapLateralMm      = 2.0,
    int    ExtendMaxIterations      = 10,
    int    MaxConvergenceIterations = 50);

public sealed record RunOptions(
    Tolerances Tolerances,
    string?    StopAfterStage = null,
    bool       RecordTrace    = true)
{
    public static RunOptions Default => new(new Tolerances());
}
