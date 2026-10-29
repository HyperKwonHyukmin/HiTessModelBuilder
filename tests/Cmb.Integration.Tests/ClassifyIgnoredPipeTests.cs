using System.Globalization;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// 입력 감사(00_InputAudit.json) 의 배관 행 분류 사유 검증.
// 핵심 회귀: line-pipe 피팅(TEE 등)이 양 끝점 동일(길이 0)로 제외될 때,
// 사유가 'mass' 가 아니라 '길이 0(형상 없음)' 으로 보고되어야 한다.
// (line-pipe 타입은 애초에 point mass 로 변환되지 않으므로 mass 값은 무관)
public class ClassifyIgnoredPipeTests
{
    private static CsvAuditRawRow PipeRow(string type, string apos, string lpos, double outDia, double mass) =>
        new(2, 1, "=test", "dummy-raw-line", new Dictionary<string, string>
        {
            ["type"]   = type,
            ["apos"]   = apos,
            ["lpos"]   = lpos,
            ["outDia"] = outDia.ToString(CultureInfo.InvariantCulture),
            ["mass"]   = mass.ToString(CultureInfo.InvariantCulture),
        });

    private static CsvAuditRawRow PipeRowWithThick(string type, string apos, string lpos, double outDia, double thick, double mass) =>
        new(2, 1, "=test", "dummy-raw-line", new Dictionary<string, string>
        {
            ["type"]   = type,
            ["apos"]   = apos,
            ["lpos"]   = lpos,
            ["outDia"] = outDia.ToString(CultureInfo.InvariantCulture),
            ["thick"]  = thick.ToString(CultureInfo.InvariantCulture),
            ["mass"]   = mass.ToString(CultureInfo.InvariantCulture),
        });

    private const string PointA = "X 34300mm Y -8955mm Z 8696mm";
    private const string PointB = "X 33711mm Y -8955mm Z 8696mm";

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5977)]
    public void Ubolt_WithoutBeamGeometry_IsConvertedToRbe(double mass)
    {
        var (status, reasonCode, reason) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRow("UBOLT", PointA, PointA, outDia: 0, mass));

        status.Should().Be("converted");
        reasonCode.Should().Be("ubolt_rbe");
        reason.Should().Contain("RBE2");
    }

    [Fact]
    public void ZeroLengthTee_WithZeroMass_IsIgnoredForZeroLength_NotForMass()
    {
        var (status, reasonCode, reason) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRow("TEE", PointA, PointA, outDia: 318.5, mass: 0));

        status.Should().Be("ignored");
        reasonCode.Should().Be("zero_length_pipe");
        reason.ToLowerInvariant().Should().Contain("zero length");
    }

    [Fact]
    public void ZeroLengthTee_WithPositiveMass_IsStillIgnoredForZeroLength()
    {
        // line-pipe 피팅은 mass 가 있어도 point mass 로 변환되지 않는다 → 길이 0 이면 여전히 제외.
        var (status, reasonCode, _) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRow("TEE", PointA, PointA, outDia: 318.5, mass: 12.0));

        status.Should().Be("ignored");
        reasonCode.Should().Be("zero_length_pipe");
    }

    [Fact]
    public void ZeroLengthFlange_WithZeroMass_KeepsZeroLengthWithoutMassReason()
    {
        // FLAN 은 line-pipe 가 아니라 길이 0 + 질량 0 일 때만 제외(질량이 있으면 point mass 로 수용).
        var (status, reasonCode, _) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRow("FLAN", "X 1mm Y 2mm Z 3mm", "X 1mm Y 2mm Z 3mm", outDia: 318.5, mass: 0));

        status.Should().Be("ignored");
        reasonCode.Should().Be("zero_length_pipe_without_mass");
    }

    [Fact]
    public void NormalTubi_WithDistinctEndpoints_IsConverted()
    {
        var (status, _, _) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRow("TUBI", PointA, "X 33711mm Y -8955mm Z 8696mm", outDia: 318.5, mass: 31.688));

        status.Should().Be("converted");
    }

    // ── H-1: 관 두께가 외경 절반 이상이면 내경(음수)이 만들어져 PBEAML TUBE 가 조용히 깨진다 ──

    [Fact]
    public void LinePipe_WallThicknessAtLeastHalfOutDia_IsErrorForNegativeInnerRadius()
    {
        // thick(30) >= outDia(50)/2 → 내부 반경 = 25 - 30 = -5 < 0 (음수 PBEAML)
        var (status, reasonCode, _) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRowWithThick("TUBI", PointA, PointB, outDia: 50, thick: 30, mass: 0));

        status.Should().Be("error");
        reasonCode.Should().Be("pipe_wall_too_thick");
    }

    [Fact]
    public void LinePipe_WallThicknessExactlyHalfOutDia_IsError()
    {
        // thick(25) == outDia(50)/2 → 내부 반경 = 0 (퇴화된 단면)
        var (status, reasonCode, _) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRowWithThick("TUBI", PointA, PointB, outDia: 50, thick: 25, mass: 0));

        status.Should().Be("error");
        reasonCode.Should().Be("pipe_wall_too_thick");
    }

    [Fact]
    public void LinePipe_NormalWallThickness_IsConverted()
    {
        // thick(3.05) < outDia(73)/2 → 정상
        var (status, _, _) =
            InputAuditJson.ClassifyIgnoredPipe(PipeRowWithThick("TUBI", PointA, PointB, outDia: 73, thick: 3.05, mass: 0));

        status.Should().Be("converted");
    }
}
