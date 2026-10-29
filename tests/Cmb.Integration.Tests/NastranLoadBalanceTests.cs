using FluentAssertions;

namespace Cmb.Integration.Tests;

// 해석 후 자중 합력과 지점 반력 합력을 대조 — 하중이 AUTOSPC 로 빠지면 경고한다.
[Collection("Console")]
public class NastranLoadBalanceTests
{
    private static string F06(double oloadFz, double spcFz) => $@"
0                                                  OLOAD    RESULTANT
  SUBCASE/    LOAD
  DAREA ID    TYPE       T1            T2            T3            R1            R2            R3
             TOTALS  0.000000E+00  0.000000E+00 {oloadFz:E6}  0.0  0.0  0.0
0                                                  SPCFORCE RESULTANT
  SUBCASE/    LOAD
  DAREA ID    TYPE       T1            T2            T3            R1            R2            R3
             TOTALS  0.000000E+00  0.000000E+00 {spcFz:E6}  0.0  0.0  0.0
";

    private static string Capture(Action act)
    {
        var old = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { act(); } finally { Console.SetOut(old); }
        return sw.ToString();
    }

    [Fact]
    public void Balanced_NoWarning()
    {
        var output = Capture(() => NastranRunner.ReportLoadBalanceFromF06(F06(-1.0e6, 1.0e6)));
        output.Should().Contain("불균형 0.00%").And.NotContain("NASTRAN_LOAD_IMBALANCE");
    }

    [Fact]
    public void ReactionShort_WarnsImbalance()
    {
        var output = Capture(() => NastranRunner.ReportLoadBalanceFromF06(F06(-1.0e6, 0.9e6)));
        output.Should().Contain("NASTRAN_LOAD_IMBALANCE");
    }
}
