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
        m.Sections.Add(new BeamSection(10, BeamSectionKind.H, [400.0, 200.0, 10.0, 16.0], 1));
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
        propLines[0].Should().Contain("       H"); // right-aligned type
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

    // ── SPC1 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Spc1_NodeIdPresent()
    {
        var m = MinimalModel();
        var bdf = WriteBdf(m, [42]);

        bdf.Should().Contain("SPC1");
        bdf.Should().Contain("      42");
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
        bdf.Should().Contain("$ HiTessModelBuilder: PipePidBase=1000");
    }

    [Fact]
    public void Write_StructureSection_EmitsStructureBanner()
    {
        var bdf = WriteBdf(MinimalModel());
        // MinimalModel: PID 10 (< 1000) → Structure 그룹
        bdf.Should().Contain("$ ---- STRUCTURE SECTIONS (PID < 1000) ----");
        bdf.Should().NotContain("$ ---- PIPE SECTIONS");
    }

    [Fact]
    public void Write_PipeSection_EmitsPipeBanner()
    {
        var m = new FeModel();
        m.Materials.Add(Material.DefaultSteel);
        m.Sections.Add(new BeamSection(1000, BeamSectionKind.Tube, [50.0, 47.0], 1));
        m.Nodes.Add(new Node(1, new Point3(0.0, 0.0, 0.0)));
        m.Nodes.Add(new Node(2, new Point3(1000.0, 0.0, 0.0)));
        m.Elements.Add(new BeamElement(1, 1, 2, 1000, EntityCategory.Pipe, Vector3.UnitZ));

        var bdf = WriteBdf(m);

        bdf.Should().Contain("$ ---- PIPE SECTIONS (PID >= 1000) ----");
        bdf.Should().NotContain("$ ---- STRUCTURE SECTIONS");
    }

    [Fact]
    public void Write_StructureBeforePipe_BannerOrderingPreserved()
    {
        var m = MinimalModel(); // PID 10 (Structure)
        m.Sections.Add(new BeamSection(1000, BeamSectionKind.Tube, [50.0, 47.0], 1));
        m.Elements.Add(new BeamElement(2, 1, 2, 1000, EntityCategory.Pipe, Vector3.UnitZ));

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
}
