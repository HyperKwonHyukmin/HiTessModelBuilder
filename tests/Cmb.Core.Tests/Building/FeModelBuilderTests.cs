using Cmb.Core.Building;
using Cmb.Core.Model;
using Cmb.Core.Model.Raw;
using FluentAssertions;

namespace Cmb.Core.Tests.Building;

public class FeModelBuilderTests
{
    private static readonly RawDesignData Empty = new([], [], [], []);

    // ── Material ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Always_AddsDefaultSteel()
    {
        var model = new FeModelBuilder().Build(Empty);

        model.Materials.Should().HaveCount(1);
        var mat = model.Materials[0];
        mat.Name.Should().Be("Steel");
        mat.E.Should().BeApproximately(206000.0, 1e-6);
        mat.Nu.Should().BeApproximately(0.3, 1e-9);
        mat.Rho.Should().BeApproximately(7.85e-9, 1e-18);
    }

    // ── Structure beams ───────────────────────────────────────────────────────

    [Fact]
    public void Build_TwoCollinearBeams_SharedNode()
    {
        var rows = new[]
        {
            Beam("A", "BEAM", [400, 200, 10, 16], [0, 0, 0], [1000, 0, 0]),
            Beam("B", "BEAM", [400, 200, 10, 16], [1000, 0, 0], [2000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Nodes.Should().HaveCount(3);
        model.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void Build_SameSection_SingleProperty()
    {
        var rows = new[]
        {
            Beam("A", "BEAM", [400, 200, 10, 16], [0, 0, 0],    [1000, 0, 0]),
            Beam("B", "BEAM", [400, 200, 10, 16], [2000, 0, 0], [3000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Sections.Should().HaveCount(1);
        model.Elements[0].PropertyId.Should().Be(model.Elements[1].PropertyId);
    }

    [Fact]
    public void Build_DifferentSections_TwoProperties()
    {
        var rows = new[]
        {
            Beam("A", "BEAM", [400, 200, 10, 16], [0, 0, 0],    [1000, 0, 0]),
            Beam("B", "ANG",  [75, 75, 9],         [2000, 0, 0], [3000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Sections.Should().HaveCount(2);
    }

    [Fact]
    public void Build_HSection_DimsConvertedToWebClearAndFlange()
    {
        // BEAM_400x200x10x16 → PBEAML H: [H−2TF, 2TF, BF, TW] = [368, 32, 200, 10]
        var rows = new[]
        {
            Beam("A", "BEAM", [400, 200, 10, 16], [0, 0, 0], [1000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var sec = model.Sections[0];
        sec.Kind.Should().Be(BeamSectionKind.H);
        sec.Dims.Should().HaveCount(4);
        sec.Dims[0].Should().BeApproximately(368.0, 1e-9); // H − 2×TF = 400 − 32
        sec.Dims[1].Should().BeApproximately(32.0,  1e-9); // 2 × TF
        sec.Dims[2].Should().BeApproximately(200.0, 1e-9); // BF
        sec.Dims[3].Should().BeApproximately(10.0,  1e-9); // TW
    }

    [Fact]
    public void Build_AngSection_DimsNormalizedToFour()
    {
        var rows = new[]
        {
            Beam("A", "ANG", [75, 75, 9], [0, 0, 0], [1000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var sec = model.Sections[0];
        sec.Kind.Should().Be(BeamSectionKind.L);
        sec.Dims.Should().HaveCount(4);
        sec.Dims[2].Should().Be(sec.Dims[3]); // T1 == T2 for equal-angle
    }

    [Fact]
    public void Build_ZeroLengthBeam_Skipped()
    {
        var rows = new[]
        {
            Beam("A", "BEAM", [400, 200, 10, 16], [0, 0, 0], [0, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Elements.Should().BeEmpty();
    }

    [Fact]
    public void Build_WeldEnd_NodeTagApplied()
    {
        var rows = new[]
        {
            new RawBeamRow("A", "BEAM", "BEAM_400x200x10x16", [400, 200, 10, 16],
                [0, 0, 0], [1000, 0, 0], [0, 0, 1], "end"),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var endNode = model.Nodes.First(n => n.Position.X > 500);
        endNode.HasTag(NodeTags.Weld).Should().BeTrue();
    }

    [Fact]
    public void Build_UnknownSectionType_Skipped()
    {
        var rows = new[]
        {
            Beam("A", "UNKNOWN", [100], [0, 0, 0], [1000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        model.Elements.Should().BeEmpty();
    }

    // ── Pipe ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_TubeSection_DimsAreRadii()
    {
        var rows = new[]
        {
            Pipe("T", "TUBI", pos: [0, 0, 500], aPos: [0, 0, 0], lPos: [0, 0, 1000],
                outDia: 73, thick: 3.05, mass: 0),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], rows, [], []));

        var sec = model.Sections[0];
        sec.Dims.Should().HaveCount(2);
        sec.Dims[0].Should().BeApproximately(36.5,  1e-9); // outer radius = 73/2
        sec.Dims[1].Should().BeApproximately(33.45, 1e-9); // inner radius = 73/2 - 3.05
    }

    [Fact]
    public void Build_RodSection_DimIsRadius()
    {
        var rows = new[]
        {
            Beam("R", "RBAR", [19], [0, 0, 0], [1000, 0, 0]),
        };
        var model = new FeModelBuilder().Build(new RawDesignData(rows, [], [], []));

        var sec = model.Sections[0];
        sec.Kind.Should().Be(BeamSectionKind.Rod);
        sec.Dims.Should().HaveCount(1);
        sec.Dims[0].Should().BeApproximately(9.5, 1e-9); // radius = 19/2
    }

    [Fact]
    public void Build_Ubolt_CreatesPointMassOnly()
    {
        var rows = new[]
        {
            Pipe("U", "UBOLT", pos: [77578, 8965, 37252], aPos: [77578, 8965, 37252], lPos: [77578, 8965, 37252],
                outDia: 0, thick: 0, mass: 3.0968),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], rows, [], []));

        model.Elements.Should().BeEmpty();
        model.PointMasses.Should().HaveCount(1);
        model.PointMasses[0].Mass.Should().BeApproximately(3.0968, 1e-4);
    }

    [Fact]
    public void Build_Tubi_CreatesBeamElement()
    {
        var rows = new[]
        {
            Pipe("T", "TUBI", pos: [76850, 8965, 37252], aPos: [76600, 8965, 37252], lPos: [77100, 8965, 37252],
                outDia: 73, thick: 3.05, mass: 2.6575),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], rows, [], []));

        model.Elements.Should().HaveCount(1);
        model.Elements[0].Category.Should().Be(EntityCategory.Pipe);
        // mass attached at Pos node
        model.PointMasses.Should().HaveCount(1);
    }

    [Fact]
    public void Build_Tee_CreatesThreeBeams()
    {
        var rows = new[]
        {
            new RawPipeRow("TEE", "TEE", "B1",
                Pos:      [90351, 2435, 37252],
                APos:     [90428, 2435, 37252],
                LPos:     [90275, 2435, 37252],
                Normal:   [0, 1, 0],
                InterPos: null,
                P3Pos:    [90351, 2435, 37319],
                Rest: null,
                OutDia: 73, Thick: 3.05, OutDia2: 48.3, Thick2: 2.77,
                Mass: 1.419, Remark: null),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], rows, [], []));

        // TEE: APos→center, center→LPos (main split at T-junction), center→P3Pos (branch)
        model.Elements.Should().HaveCount(3);
        model.Sections.Should().HaveCount(2); // different diameters for main vs branch
    }

    [Fact]
    public void Build_ValveZeroDia_CreatesPointMass()
    {
        var rows = new[]
        {
            Pipe("V", "VALV", pos: [90940, 2435, 38850], aPos: [91023, 2435, 38850], lPos: [90858, 2435, 38850],
                outDia: 0, thick: 0, mass: 14.3),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], rows, [], []));

        model.Elements.Should().BeEmpty();
        model.PointMasses.Should().HaveCount(1);
        model.PointMasses[0].Mass.Should().BeApproximately(14.3, 1e-9);
    }

    // ── Equipment ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Equip_CreatesPointMassAtCog()
    {
        var rows = new[]
        {
            new RawEquipRow("EQ1", [112065, -7685, 30988], [112100, -7685, 30988], 1.0),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], [], rows, []));

        model.PointMasses.Should().HaveCount(1);
        model.PointMasses[0].Mass.Should().BeApproximately(1.0, 1e-9);

        var cogNode = model.FindNode(model.PointMasses[0].NodeId)!;
        cogNode.Position.X.Should().BeApproximately(112100.0, 1e-6);
    }

    [Fact]
    public void Build_EquipZeroMass_Skipped()
    {
        var rows = new[]
        {
            new RawEquipRow("EQ0", [0, 0, 0], [0, 0, 0], 0.0),
        };
        var model = new FeModelBuilder().Build(new RawDesignData([], [], rows, []));

        model.PointMasses.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RawBeamRow Beam(string name, string type, double[] dims, double[] start, double[] end) =>
        new(name, type, $"{type}_{string.Join("x", dims)}", dims, start, end, [0, 0, 1], "");

    private static RawPipeRow Pipe(string name, string type,
        double[] pos, double[] aPos, double[] lPos,
        double outDia, double thick, double mass) =>
        new(name, type, "B1", pos, aPos, lPos, [0, 0, 1],
            null, null, null, outDia, thick, 0, 0, mass, null);
}
