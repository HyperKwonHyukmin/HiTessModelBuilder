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
    // GroupConnectStage: 이 간격 이하(CAD 오차)는 그룹을 병진 이동해 붙이고, 초과하면 제자리에서 RBE2 로 잇는다.
    double GroupConnectTranslateMaxMm = 10.0,
    double ExtendExtraMarginMm          = 100.0,
    double ExtendCoplanarTolMm          = 10.0,
    double ExtendSnapLateralMm          = 2.0,
    double ExtendObstacleTolMm          = 1.0,
    double ExtendMinElemLengthMm        = 5.0,
    int    ExtendMaxIterations          = 10,
    int    MaxConvergenceIterations     = 50,
    // ConnectivityConvergenceStage: GroupConnect 반복 한도. RBE 연결은 한 반복에 master 에 닿은 그룹만
    // 붙어(사슬처럼 이어진 소그룹은 한 겹씩) 병진 이동보다 반복이 더 필요하다(03 샘플 11회).
    int    GroupConnectMaxIterations    = 30,
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
    // FreeEndRbeStage: 자유단 부재의 바깥 축 방향 원뿔(cos ≥ AxialConeCos) 안 후보를 우선한다.
    // 최근접 후보보다 AxialPreferenceMm 이내로만 멀면 축 방향 후보를 고른다(0 = 순수 최근접).
    // 예) 수직 기둥 하단은 옆 앵글이 조금 더 가까워도 바로 아래 H빔에 얹힌다.
    double FreeEndRbeAxialConeCos       = 0.8,
    double FreeEndRbeAxialPreferenceMm  = 100.0,
    double FinalMeshProximityExtraMarginMm = 25.0,
    int    FinalMeshProximityMaxSourceElements = 20,
    // LoopCloseProximityStage: 같은 그룹이라도 빔/RBE 그래프상 이 홉 수 이상 떨어진 구조 부재만
    // 근접(loop-close) 연결한다. 작을수록 더 많이 연결(과연결 위험), 클수록 보수적.
    int    LoopCloseMinTopoHops = 3);

public sealed record RunOptions(
    Tolerances Tolerances,
    string?    StopAfterStage = null,
    bool       RecordTrace    = true,
    int        PipePidBase    = 101)
{
    public static RunOptions Default => new(new Tolerances());
}
