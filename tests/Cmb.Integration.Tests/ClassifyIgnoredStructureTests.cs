using FluentAssertions;

namespace Cmb.Integration.Tests;

// 입력 감사(00_InputAudit.json) 의 구조 행 분류 사유 검증.
// F-8(1): 단면 치수(size)에 0 이하 값이 섞이면 유효 단면을 만들 수 없으므로
// 배관 outDia<=0 과 동일하게 'error' 로 감사되어야 한다(기존엔 조용히 통과 = pipe 와 비대칭).
public class ClassifyIgnoredStructureTests
{
    private static CsvAuditRawRow StructureRow(string poss, string pose, string size) =>
        new(2, 1, "=test", "dummy-raw-line", new Dictionary<string, string>
        {
            ["poss"] = poss,
            ["pose"] = pose,
            ["size"] = size,
        });

    private const string PointA = "X 90072mm Y 4908mm Z 38610mm";
    private const string PointB = "X 90072mm Y 4908mm Z 39616mm";

    [Fact]
    public void Structure_WithZeroDimension_IsError()
    {
        var (status, reasonCode, _) =
            InputAuditJson.ClassifyIgnoredStructure(StructureRow(PointA, PointB, "BEAM_0x200x10x16"));

        status.Should().Be("error");
        reasonCode.Should().Be("structure_dim_nonpositive");
    }

    [Fact]
    public void Structure_WithNegativeDimension_IsError()
    {
        var (status, reasonCode, _) =
            InputAuditJson.ClassifyIgnoredStructure(StructureRow(PointA, PointB, "FBAR_-5x16"));

        status.Should().Be("error");
        reasonCode.Should().Be("structure_dim_nonpositive");
    }

    [Fact]
    public void Structure_WithValidDimensions_IsConverted()
    {
        var (status, _, _) =
            InputAuditJson.ClassifyIgnoredStructure(StructureRow(PointA, PointB, "BEAM_400x200x10x16"));

        status.Should().Be("converted");
    }
}
