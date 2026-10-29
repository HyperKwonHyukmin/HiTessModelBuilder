using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Io.Nastran;
using FluentAssertions;

namespace Cmb.Io.Tests.Nastran;

public class BdfWriterTests
{
    private static FeModel MinimalModel()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        // BEAM_400x200x10x16 → 내부 저장형(H) [H−2TF, 2TF, BF, TW] = [368, 32, 200, 10]
        m.Sections.Add(new BeamSection(10, BeamSectionKind.H, [368.0, 32.0, 200.0, 10.0], 1));
        m.Nodes.Add(new Node(1, new Point3(0.0, 0.0, 0.0)));
        m.Nodes.Add(new Node(2, new Point3(1000.0, 0.0, 0.0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        return m;
    }

    private static string WriteBdf(FeModel model, IEnumerable<int>? spc = null)
    {
        using var sw = new StringWriter();
        BdfWriter.Write(model, sw, spc);
        return sw.ToString();
    }

    // ── GRID ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Grid_FieldWidths()
    {
        var lines = WriteBdf(MinimalModel()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var gridLine = lines.First(l => l.StartsWith("GRID"));

        // Each field is exactly 8 chars; at minimum 6 fields for GRID
        gridLine.Length.Should().BeGreaterThanOrEqualTo(48);
        gridLine[..8].Trim().Should().Be("GRID");
    }

    [Fact]
    public void Write_Grid_NegativeCoordinate()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Nodes.Add(new Node(1, new Point3(100.0, -7685.0, 30988.0)));

        var bdf = WriteBdf(m);
        bdf.Should().Contain("-7685.0");
    }

    // ── CBEAM ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Cbeam_ContainsEidPidNodes()
    {
        var bdf = WriteBdf(MinimalModel());
        var cbeamLine = bdf.Split(Environment.NewLine).First(l => l.StartsWith("CBEAM"));

        cbeamLine.Should().Contain("       1"); // element id
        cbeamLine.Should().Contain("      10"); // property id
        cbeamLine.Should().Contain("BGG");      // OFFT flag
    }

    [Fact]
    public void Write_Cbeam_RecalculatesOrientationAwayFromBeamAxis()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Tube, [100.0, 5.0], 1));
        m.Nodes.Add(new Node(1, new Point3(0.0, 0.0, 0.0)));
        m.Nodes.Add(new Node(2, new Point3(0.0, 0.0, 100.0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Pipe, Vector3.UnitZ));

        var cbeamLine = WriteBdf(m).Split(Environment.NewLine).First(l => l.StartsWith("CBEAM"));

        cbeamLine.Should().Contain("     0.0     1.0     0.0");
    }

    // CSV ori 에서 온 방향(수직 기둥의 v=+X)을 기본 규칙(v=Y)으로 덮어쓰지 않는다.
    [Fact]
    public void Write_Cbeam_KeepsStoredOrientation_ForVerticalColumn()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(10, BeamSectionKind.H, [368.0, 32.0, 200.0, 10.0], 1));
        m.Nodes.Add(new Node(1, new Point3(0.0, 0.0, 0.0)));
        m.Nodes.Add(new Node(2, new Point3(0.0, 0.0, 1000.0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitX));

        var cbeamLine = WriteBdf(m).Split(Environment.NewLine).First(l => l.StartsWith("CBEAM"));

        cbeamLine.Should().Contain("     1.0     0.0     0.0");
    }

    [Fact]
    public void Write_Cbeam_OrientationZeroFieldsAreWrittenAsRealValues()
    {
        var cbeamLine = WriteBdf(MinimalModel()).Split(Environment.NewLine).First(l => l.StartsWith("CBEAM"));
        var fields = Enumerable.Range(0, cbeamLine.Length / 8)
            .Select(i => cbeamLine.Substring(i * 8, 8).Trim())
            .ToArray();

        fields[5].Should().Be("0.0");
        fields[6].Should().Be("0.0");
        fields[7].Should().Be("1.0");
    }

    // ── PBEAML ────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Pbeaml_TwoLines()
    {
        var lines = WriteBdf(MinimalModel())
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        var propLines = lines.Where(l => l.StartsWith("PBEAML") || (l.StartsWith("        ") && !l.TrimStart().StartsWith("$")))
                             .ToList();

        // Should have header line + dims line
        propLines.Should().HaveCountGreaterThanOrEqualTo(2);
        propLines[0].Should().Contain("       I"); // right-aligned type: BEAM(H)은 표준 I 단면으로 출력
    }

    [Fact]
    public void Write_Pbeaml_HSectionEmittedAsIWithSixDims()
    {
        // 내부 저장형(H) [368, 32, 200, 10] → I [H=400, BF=200, BF=200, TW=10, TF=16, TF=16]
        var lines = WriteBdf(MinimalModel()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        int hdrIdx = lines.ToList().FindIndex(l => l.StartsWith("PBEAML"));
        hdrIdx.Should().BeGreaterThanOrEqualTo(0);

        var header = lines[hdrIdx];
        header.Should().EndWith("       I"); // 마지막 필드 = TYPE "I"

        var dimsLine = lines[hdrIdx + 1];
        var fields = Enumerable.Range(0, dimsLine.Length / 8)
            .Select(i => dimsLine.Substring(i * 8, 8).Trim())
            .ToArray();

        // 필드[0]=연속(공백), [1..6]=DIM1..DIM6, [7]=NSM
        fields[1].Should().Be("400.0"); // DIM1 = H
        fields[2].Should().Be("200.0"); // DIM2 = 하부 플랜지 폭
        fields[3].Should().Be("200.0"); // DIM3 = 상부 플랜지 폭
        fields[4].Should().Be("10.0");  // DIM4 = 웹 두께
        fields[5].Should().Be("16.0");  // DIM5 = 하부 플랜지 두께
        fields[6].Should().Be("16.0");  // DIM6 = 상부 플랜지 두께
        fields[7].Should().Be("0.0");   // NSM
    }

    [Fact]
    public void Write_PbeamlDimsLine_ContainsNsm()
    {
        var lines = WriteBdf(MinimalModel()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var dimsLine = lines.First(l => l.StartsWith("        ") && l.Contains("0.0"));
        dimsLine.Should().NotBeNull();
    }

    // ── MAT1 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Mat1_ContainsEAndNu()
    {
        var bdf = WriteBdf(MinimalModel());
        bdf.Should().Contain("206000.0");
        bdf.Should().Contain("0.3");
    }

    [Fact]
    public void Write_Mat1_RhoInNastranScientific()
    {
        var bdf = WriteBdf(MinimalModel());
        // 7.85e-9 → "7.85-9" Nastran notation
        bdf.Should().Contain("7.85-9");
    }

    // ── CONM2 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Conm2_MassPresent()
    {
        var m = MinimalModel();
        m.PointMasses.Add(new PointMass(1, 1, 14.3));

        var bdf = WriteBdf(m);
        bdf.Should().Contain("CONM2");
        bdf.Should().Contain("14.3");
    }

    [Fact]
    public void Write_Conm2_SmallMassKeepsThreeDecimalPrecision()
    {
        var m = MinimalModel();
        m.PointMasses.Add(new PointMass(1, 1, 0.043));

        var bdf = WriteBdf(m);

        bdf.Should().Contain("0.043");
    }

    [Fact]
    public void Write_Conm2_IncludesOffsetFields()
    {
        var m = MinimalModel();
        m.PointMasses.Add(new PointMass(10606, 1, 1.24));

        var bdf = WriteBdf(m);
        var conmLine = bdf.Split(Environment.NewLine).First(l => l.StartsWith("CONM2"));

        // EID, G, CID, M 뒤에 오프셋 X1/X2/X3(=0.0) 까지 8필드 모두 출력되어야 한다.
        var tokens = conmLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        tokens.Should().Equal("CONM2", "10606", "1", "0", "1.24", "0.0", "0.0", "0.0");
    }

    // ── RBE2 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Rbe2_ThreeDependents_SingleLine()
    {
        var m = MinimalModel();
        m.Nodes.Add(new Node(3, new Point3(500.0, 100.0, 0.0)));
        m.Nodes.Add(new Node(4, new Point3(500.0, -100.0, 0.0)));
        m.Rigids.Add(new RigidElement(1, 1, [2, 3, 4], "TEST"));

        var bdf = WriteBdf(m);
        var rbeLine = bdf.Split(Environment.NewLine).First(l => l.StartsWith("RBE2"));
        rbeLine.Should().Contain("123456");
    }

    [Fact]
    public void Write_Rbe2_UsesRigidCm()
    {
        var m = MinimalModel();
        m.Rigids.Add(new RigidElement(1, 1, [2], "UBOLT", cm: "123"));

        var bdf = WriteBdf(m);
        var rbeLine = bdf.Split(Environment.NewLine).First(l => l.StartsWith("RBE2"));

        rbeLine.Should().Contain("     123");
        rbeLine.Should().NotContain("123456");
    }

    // ── M-3: RBE2 연속 카드('+') 골든 테스트 ──────────────────────────────────
    // small field 한 줄에는 dep 5개까지(RBE2 id indep cm + dep1..dep5), 6번째부터 '+' 연속 라인.

    private static string[] RbeCardLines(FeModel m)
    {
        var lines = WriteBdf(m).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        int idx = Array.FindIndex(lines, l => l.StartsWith("RBE2"));
        idx.Should().BeGreaterThanOrEqualTo(0);
        // RBE2 헤더 라인 + 뒤따르는 연속('+') 라인들을 수집
        var card = new List<string> { lines[idx] };
        for (int i = idx + 1; i < lines.Length && lines[i].TrimStart().StartsWith("+"); i++)
            card.Add(lines[i]);
        return card.ToArray();
    }

    private static string[] Fields8(string line) =>
        Enumerable.Range(0, line.Length / 8)
            .Select(i => line.Substring(i * 8, 8).Trim())
            .ToArray();

    private static FeModel ModelWithRbe(int rbeId, int indep, IEnumerable<int> deps)
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Nodes.Add(new Node(indep, new Point3(0, 0, 0)));
        int x = 100;
        foreach (int d in deps.Concat([indep]).Distinct())
            if (d != indep) m.Nodes.Add(new Node(d, new Point3(x += 100, 0, 0)));
        m.Rigids.Add(new RigidElement(rbeId, indep, deps, "TEST"));
        return m;
    }

    [Fact]
    public void Write_Rbe2_SevenDependents_UsesContinuationLine()
    {
        // indep=1, deps=3..9 (7개) → 라인1: dep 3..7 (5개) + '+', 연속: dep 8,9
        var m = ModelWithRbe(100, 1, [3, 4, 5, 6, 7, 8, 9]);

        var card = RbeCardLines(m);
        card.Should().HaveCount(2, "dep 7개는 헤더 라인 + 연속 라인 1개로 나뉜다");

        card[0].TrimEnd().Should().EndWith("+", "헤더 라인 끝에 연속 표시가 있어야 한다");
        card[1].TrimStart().Should().StartWith("+", "연속 라인은 '+' 로 시작해야 한다");

        // 헤더 라인: [RBE2, id, indep, cm, dep1..dep5, +]
        var f0 = Fields8(card[0]);
        f0[0].Should().Be("RBE2");
        f0[1].Should().Be("100");
        f0[2].Should().Be("1");
        f0[3].Should().Be("123456");
        f0[4..9].Should().Equal("3", "4", "5", "6", "7");

        // 연속 라인: [+, dep6, dep7]
        var f1 = Fields8(card[1]);
        f1[0].Should().Be("+");
        f1[1..3].Should().Equal("8", "9");

        // 모든 dep 가 정확히 한 번씩 출력된다.
        var allDeps = f0[4..9].Concat(f1[1..]).Where(s => s.Length > 0).ToArray();
        allDeps.Should().Equal("3", "4", "5", "6", "7", "8", "9");
    }

    [Fact]
    public void Write_Rbe2_TenDependents_ContinuationHoldsRemainderInOrder()
    {
        // indep=1, deps=3..12 (10개) → 라인1: 3..7 (5개), 연속: 8..12 (5개)
        var m = ModelWithRbe(200, 1, [3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);

        var card = RbeCardLines(m);
        card.Should().HaveCount(2);

        var f0 = Fields8(card[0]);
        f0[4..9].Should().Equal("3", "4", "5", "6", "7");

        var f1 = Fields8(card[1]);
        f1[0].Should().Be("+");
        f1[1..6].Should().Equal("8", "9", "10", "11", "12");

        // dep 총 10개가 순서대로 온전히 보존된다.
        var allDeps = f0[4..9].Concat(f1[1..]).Where(s => s.Length > 0).ToArray();
        allDeps.Should().Equal("3", "4", "5", "6", "7", "8", "9", "10", "11", "12");
    }

    [Fact]
    public void Write_Rbe2_FiveDependents_StaysSingleLineNoContinuation()
    {
        // dep 5개 이하는 연속 라인이 생기지 않는다(경계 확인).
        var m = ModelWithRbe(300, 1, [3, 4, 5, 6, 7]);

        var card = RbeCardLines(m);
        card.Should().HaveCount(1);
        card[0].TrimEnd().Should().NotEndWith("+");
    }

    // ── SPC1 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Spc1_NodeIdPresent()
    {
        var m = MinimalModel();
        var bdf = WriteBdf(m, [42]);

        bdf.Should().Contain("SPC1");
        bdf.Should().Contain("      42");
    }

    [Fact]
    public void Write_NoAnalysis_OmitsSpcCaseControlLine()
    {
        // 해석(analysis) 없이 출력하는 최종 모델 BDF 에는 SPC Case Control 라인이 없어야 한다.
        // (SPCFORCES = ALL 은 결과 출력 요청이므로 SPC 경계조건과 무관 — 제외 대상 아님)
        var bdf = WriteBdf(MinimalModel());

        bdf.Split(Environment.NewLine)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("SPC =") || l.StartsWith("SPC="))
            .Should().BeEmpty();
    }

    [Fact]
    public void Write_NoAnalysis_OmitsSpc1Cards()
    {
        // spcNodeIds 가 비어 있으면 SPC1 bulk 카드도 출력되지 않아야 한다.
        var bdf = WriteBdf(MinimalModel(), Array.Empty<int>());
        bdf.Should().NotContain("SPC1");
    }

    [Fact]
    public void Write_NoAnalysis_StillEmitsAnalysisStaticsCaseControl()
    {
        // 해석(analysis) 없이 출력하는 형상 전용 BDF 에도 ANALYSIS = STATICS 는 SUBCASE 에 포함한다.
        // ANALYSIS=STATICS 는 SOL 종류(정적해석) 명시이므로 존재하지 않는 set 을 참조하지 않아
        // dangling 위험이 없음 → SPC=/LOAD= 와 달리 일반 출력에도 항상 남긴다.
        var bdf = WriteBdf(MinimalModel());

        bdf.Split(Environment.NewLine)
            .Select(l => l.Trim())
            .Should().Contain("ANALYSIS = STATICS");
    }

    [Fact]
    public void Write_WithAnalysis_EmitsSpcCaseControlAndCards()
    {
        // --run-nastran 해석 경로(analysis != null)에서는 SPC Case Control + SPC1 카드를 유지한다.
        using var sw = new StringWriter();
        BdfWriter.Write(MinimalModel(), sw, new[] { 2 }, new AnalysisCase());
        var bdf = sw.ToString();

        bdf.Split(Environment.NewLine).Select(l => l.Trim())
            .Should().Contain(l => l.StartsWith("SPC ="));
        bdf.Should().Contain("SPC1");
    }

    // ── Document structure ────────────────────────────────────────────────────

    [Fact]
    public void Write_AlwaysEndsWithEnddata()
    {
        var bdf = WriteBdf(MinimalModel());
        bdf.TrimEnd().Should().EndWith("ENDDATA");
    }

    [Fact]
    public void Write_ContainsBeginBulk()
    {
        var bdf = WriteBdf(MinimalModel());
        bdf.Should().Contain("BEGIN BULK");
    }

    // ── Pipe / Structure PID 분리 marker ──────────────────────────────────────

    [Fact]
    public void Write_AlwaysEmitsPipePidBaseMarker()
    {
        var bdf = WriteBdf(MinimalModel());
        bdf.Should().Contain("$ HiTessModelBuilder: PipePidBase=101");
    }

    [Fact]
    public void Write_StructureSection_EmitsStructureBanner()
    {
        var bdf = WriteBdf(MinimalModel());
        // MinimalModel: PID 10 (< 101) → Structure 그룹
        bdf.Should().Contain("$ ---- STRUCTURE SECTIONS (PID < 101) ----");
        bdf.Should().NotContain("$ ---- PIPE SECTIONS");
    }

    [Fact]
    public void Write_PipeSection_EmitsPipeBanner()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(101, BeamSectionKind.Tube, [50.0, 47.0], 1));
        m.Nodes.Add(new Node(1, new Point3(0.0, 0.0, 0.0)));
        m.Nodes.Add(new Node(2, new Point3(1000.0, 0.0, 0.0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 101, EntityCategory.Pipe, Vector3.UnitZ));

        var bdf = WriteBdf(m);

        bdf.Should().Contain("$ ---- PIPE SECTIONS (PID >= 101) ----");
        bdf.Should().NotContain("$ ---- STRUCTURE SECTIONS");
    }

    [Fact]
    public void Write_StructureBeforePipe_BannerOrderingPreserved()
    {
        var m = MinimalModel(); // PID 10 (Structure)
        m.Sections.Add(new BeamSection(101, BeamSectionKind.Tube, [50.0, 47.0], 1));
        m.Elements.Add(new BeamElement(2, 1, 2, 101, EntityCategory.Pipe, Vector3.UnitZ));

        var bdf = WriteBdf(m);
        int struBanner = bdf.IndexOf("$ ---- STRUCTURE SECTIONS", StringComparison.Ordinal);
        int pipeBanner = bdf.IndexOf("$ ---- PIPE SECTIONS", StringComparison.Ordinal);

        struBanner.Should().BeGreaterThan(0);
        pipeBanner.Should().BeGreaterThan(struBanner);
    }

    [Fact]
    public void Write_CustomPipePidBase_ReflectedInBanner()
    {
        using var sw = new StringWriter();
        BdfWriter.Write(MinimalModel(), sw, pipePidBase: 500);
        var bdf = sw.ToString();

        bdf.Should().Contain("$ HiTessModelBuilder: PipePidBase=500");
        bdf.Should().Contain("(PID < 500)");
    }

    // 8자 필드 안에서 유효숫자를 최대로 — kg 단위 반올림·0.1 mm 반올림으로 질량·단면이 틀어지지 않게
    [Fact]
    public void Write_SmallMassAndThinTubeDims_KeepPrecision()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(101, BeamSectionKind.Tube, [36.5, 33.45], 1));
        m.Nodes.Add(new Node(1, new Point3(0.0, 0.0, 0.0)));
        m.Nodes.Add(new Node(2, new Point3(1000.0, 0.0, 0.0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 101, EntityCategory.Pipe, Vector3.UnitZ));
        m.PointMasses.Add(new PointMass(5, 1, 0.0013478));

        var bdf = WriteBdf(m);

        bdf.Should().Contain("   33.45");
        bdf.Split(Environment.NewLine).First(l => l.StartsWith("CONM2")).Should().Contain("0.001348");
    }
    // ── 호선 주석 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Write_HullNo_IsFirstLineComment()
    {
        var m = MinimalModel();
        m.HullNo = "3454";
        var first = WriteBdf(m).Split(Environment.NewLine)[0];
        first.Should().Be("$ HiTessModelBuilder: HullNo=3454");
    }

    [Fact]
    public void Write_NoHullNo_NoHullComment()
    {
        WriteBdf(MinimalModel()).Should().NotContain("HullNo=");
    }

    [Theory]
    [InlineData("3454\nSOL 200")]
    [InlineData("34$54")]
    [InlineData("호선")]
    public void Write_InvalidHullNo_IsNotWritten(string raw)
    {
        var m = MinimalModel();
        m.HullNo = raw;
        WriteBdf(m).Should().NotContain("HullNo=");
    }

    [Theory]
    [InlineData(" 3454 ", "3454")]
    [InlineData("5ppp", "5PPP")]
    [InlineData("", null)]
    [InlineData("123456789012345678901", null)]
    public void NormalizeHullNo_TrimsUppercasesAndRejects(string raw, string? expected)
    {
        FeModel.NormalizeHullNo(raw).Should().Be(expected);
    }
}
