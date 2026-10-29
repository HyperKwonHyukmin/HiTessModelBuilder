using Cmb.Io.Csv;
using FluentAssertions;

namespace Cmb.Integration.Tests;

// C-2: CSV 내부 공백 행이 있어도 리더의 parseFailed 사유가 InputAudit rowAudit 에서
// 물리적 데이터 행과 올바르게 매칭되어야 한다.
// (리더 LineNumber 와 InputAudit dataRowNumber 의 카운팅 스킴 불일치 회귀 방지)
public class InputAuditBlankLineMatchTests
{
    [Fact]
    public void BuildAudit_InteriorBlankLine_ParseFailedReasonStillMatchesRow()
    {
        var struPath = Path.Combine(Path.GetTempPath(), "cmb-blank-" + Guid.NewGuid().ToString("N") + ".csv");
        // header / 정상 / 공백 / 불량(컬럼 부족)
        File.WriteAllText(struPath, string.Join("\n",
            "name,type,pos,poss,pose,size,stru,ori",
            "GOOD,GENSEC,X 0mm Y 0mm Z 0mm,X 0mm Y 0mm Z 0mm,X 0mm Y 0mm Z 100mm,FBAR_65x16,PARENT,1.0 0.0 0.0",
            "",
            "BAD,X Y Z"));
        try
        {
            var data = new CsvDesignLoader().Load(struPath, null, null);
            var paths = new CsvInputPaths(struPath, null, null);

            var doc = InputAuditJson.Build(paths, data);

            // 불량 행(물리적 데이터 행 3)은 parseFailed 로 감사되고 리더가 만든 사유(컬럼 수 부족)를 담아야 한다.
            var badRow = doc.RowAudit.Single(r => r.Name == "BAD");
            badRow.DataRowNumber.Should().Be(3);
            badRow.Status.Should().Be("parseFailed");
            badRow.Reason.Should().Contain("컬럼 수 부족");

            // 공백 행은 blank 로, 정상 행은 converted 로 분류된다.
            doc.RowAudit.Should().ContainSingle(r => r.Status == "blank");
            doc.RowAudit.Single(r => r.Name == "GOOD").Status.Should().Be("converted");
        }
        finally
        {
            File.Delete(struPath);
        }
    }
}
