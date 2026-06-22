namespace Cmb.Pipeline.Core;

public sealed record Tolerances(
    double NodeMergeMm              = 1.0,
    double IntersectionSnapMm       = 5.0,
    double ZeroLengthMm             = 0.01,
    double UboltSnapMaxDistMm       = 300.0,
    double ShortElemMinMm           = 5.0,
    double SpatialCellSizeMm        = 200.0,
    double MeshingMaxLengthStructure = 2000.0,
    double MeshingMaxLengthPipe     = 1000.0,
    double NodeOnSegmentTolMm       = 0.5,
    double DanglingShortLengthMm    = 50.0,
    double CollinearMergeDistanceMm = 50.0,
    double CollinearMergeAngleDeg   = 3.0,
    double CollinearMergeLateralMm  = 1.0,
    double InlineRigidEndpointSnapLateralMm = 2.0,
    double GroupConnectLocalTolMm    = 40.0,
    double GroupConnectExtraMarginMm = 50.0,
    double ExtendExtraMarginMm          = 100.0,
    double ExtendCoplanarTolMm          = 10.0,
    double ExtendSnapLateralMm          = 2.0,
    double ExtendObstacleTolMm          = 1.0,
    double ExtendMinElemLengthMm        = 5.0,
    int    ExtendMaxIterations          = 10,
    int    MaxConvergenceIterations     = 50,
    double FreeEndRbeExtraMarginMm      = 25.0,
    double FreeEndRbeEndpointMarginMm   = 10.0,
    double RigidProximityMergeMm        = 50.0,
    double RigidFreeNodeSnapMm          = 100.0,
    double RigidArmObstacleDistanceMm   = 1.0,
    double GroupConnectNoiseLengthMm    = 500.0,
    double GroupConnectNoiseMaxDimMm    = 100.0,
    double BoxUboltMaxSearchMm          = 2000.0,
    double BoxUboltAxialSafetyMm        = 10.0,
    double PointMassReattachMaxDistMm   = 500.0,
    double PointMassReattachWarnDistMm  = 200.0,
    double FreeEndRbeMaxSearchMm        = 1000.0,
    double FinalMeshProximityExtraMarginMm = 25.0,
    int    FinalMeshProximityMaxSourceElements = 20,
    // LoopCloseProximityStage: 같은 그룹이라도 빔/RBE 그래프상 이 홉 수 이상 떨어진 구조 부재만
    // 근접(loop-close) 연결한다. 작을수록 더 많이 연결(과연결 위험), 클수록 보수적.
    int    LoopCloseMinTopoHops = 3);

public sealed record RunOptions(
    Tolerances Tolerances,
    string?    StopAfterStage = null,
    bool       RecordTrace    = true,
    int        PipePidBase    = 1000)
{
    public static RunOptions Default => new(new Tolerances());
}
