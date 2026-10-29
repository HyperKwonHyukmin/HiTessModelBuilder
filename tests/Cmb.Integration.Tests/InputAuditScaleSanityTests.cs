using Cmb.Core.Model.Raw;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// F-8(2): 모델 좌표 범위가 mm 가 아니라 meter 로 입력된 듯 지나치게 작으면 InputAudit 에 경고를 남긴다.
public class InputAuditScaleSanityTests
{
    private static RawBeamRow Beam(double[] start, double[] end) =>
        new("B", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16], start, end, [0, 0, 1], "");

    private static readonly CsvInputPaths NoPaths = new(null, null, null);

    [Fact]
    public void Build_TinyExtentLikeMeters_EmitsScaleWarning()
    {
        // 전체 범위가 5 단위 → mm 라면 5mm 짜리 모델(비현실적) → meter 입력 의심.
        var data = new RawDesignData([Beam([0, 0, 0], [5, 0, 0])], [], [], []);

        var doc = InputAuditJson.Build(NoPaths, data);

        doc.Summary.CoordinateScaleWarning.Should().NotBeNull();
        doc.Summary.CoordinateScaleWarning!.Should().Contain("mm");
    }

    [Fact]
    public void Build_NormalMmExtent_NoScaleWarning()
    {
        // 5000mm(5m) 범위 → 정상 mm 모델 → 경고 없음.
        var data = new RawDesignData([Beam([0, 0, 0], [5000, 0, 0])], [], [], []);

        var doc = InputAuditJson.Build(NoPaths, data);

        doc.Summary.CoordinateScaleWarning.Should().BeNull();
    }

    [Fact]
    public void Build_NoCoordinates_NoScaleWarning()
    {
        var data = new RawDesignData([], [], [], []);

        var doc = InputAuditJson.Build(NoPaths, data);

        doc.Summary.CoordinateScaleWarning.Should().BeNull();
    }
}
