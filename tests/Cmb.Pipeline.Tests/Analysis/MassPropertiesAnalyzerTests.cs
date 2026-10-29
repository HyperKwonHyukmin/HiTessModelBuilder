using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Analysis;
using FluentAssertions;

namespace Cmb.Pipeline.Tests.Analysis;

/// <summary>
/// MassPropertiesAnalyzer 는 원본(유체 포함)과 유체 비움(배관 rho=강재) 두 변형을 지원한다.
/// AnalyzeFluidEmpty 는 Studio 로드 직후의 기본 표시값과 일치해야 하며, WorkBench 화면과의
/// 값 어긋남 재현을 막는다.
/// </summary>
public class MassPropertiesAnalyzerTests
{
    private const double SteelRho = 7.85e-9;
    private const double FluidAugmentedRho = 15.7e-9; // 강재 + 유체 밀도(임의 값)

    [Fact]
    public void Analyze_UsesMaterialRho_AsIs()
    {
        var model = BuildStructureAndPipeModel();

        var mp = MassPropertiesAnalyzer.Analyze(model);

        // Structure Rod: L=1000, A=π·25² mm², rho=SteelRho → 강재 질량
        double structureArea = System.Math.PI * 25.0 * 25.0;
        double structureMass = 1000.0 * structureArea * SteelRho;
        // Pipe Tube: L=1000, A=π·(50²-40²) mm², rho=FluidAugmentedRho
        double pipeArea = System.Math.PI * (50.0 * 50.0 - 40.0 * 40.0);
        double pipeMass = 1000.0 * pipeArea * FluidAugmentedRho;

        mp.BeamMassTon.Should().BeApproximately(structureMass + pipeMass, 1e-12);
        mp.PointMassTon.Should().Be(0);
        mp.TotalMassTon.Should().BeApproximately(structureMass + pipeMass, 1e-12);
    }

    [Fact]
    public void AnalyzeFluidEmpty_SubstitutesPipeRhoWithSteel()
    {
        var model = BuildStructureAndPipeModel();

        var mp = MassPropertiesAnalyzer.AnalyzeFluidEmpty(model);

        // Structure 질량은 그대로, Pipe 만 rho = SteelRho 로 재계산.
        double structureArea = System.Math.PI * 25.0 * 25.0;
        double structureMass = 1000.0 * structureArea * SteelRho;
        double pipeArea = System.Math.PI * (50.0 * 50.0 - 40.0 * 40.0);
        double pipeMassDry = 1000.0 * pipeArea * SteelRho;

        mp.BeamMassTon.Should().BeApproximately(structureMass + pipeMassDry, 1e-12);
        mp.TotalMassTon.Should().BeApproximately(structureMass + pipeMassDry, 1e-12);
    }

    [Fact]
    public void AnalyzeFluidEmpty_LighterThanAnalyze_WhenPipeHasFluid()
    {
        var model = BuildStructureAndPipeModel();

        var withFluid    = MassPropertiesAnalyzer.Analyze(model);
        var withoutFluid = MassPropertiesAnalyzer.AnalyzeFluidEmpty(model);

        withoutFluid.TotalMassTon.Should().BeLessThan(withFluid.TotalMassTon);
    }

    [Fact]
    public void AnalyzeFluidEmpty_DoesNotMutateModelMaterials()
    {
        var model = BuildStructureAndPipeModel();
        double originalPipeRho = model.Materials.First(m => m.Id == 2).Rho;

        _ = MassPropertiesAnalyzer.AnalyzeFluidEmpty(model);

        model.Materials.First(m => m.Id == 2).Rho.Should().Be(originalPipeRho);
    }

    [Fact]
    public void AnalyzeFluidEmpty_MatchesAnalyze_WhenNoPipeElements()
    {
        var model = new FeModel();
        model.Nodes.Add(new Node(1, new Point3(0, 0, 0)));
        model.Nodes.Add(new Node(2, new Point3(1000, 0, 0)));
        model.Materials.Add(new Material(1, "Steel", 200000, 0.3, SteelRho));
        model.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        model.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));

        var full  = MassPropertiesAnalyzer.Analyze(model);
        var empty = MassPropertiesAnalyzer.AnalyzeFluidEmpty(model);

        empty.TotalMassTon.Should().BeApproximately(full.TotalMassTon, 1e-15);
        empty.BeamMassTon.Should().BeApproximately(full.BeamMassTon, 1e-15);
    }

    private static FeModel BuildStructureAndPipeModel()
    {
        var m = new FeModel();
        m.Nodes.Add(new Node(1, new Point3(0,    0, 0)));
        m.Nodes.Add(new Node(2, new Point3(1000, 0, 0)));
        m.Nodes.Add(new Node(3, new Point3(0,    0, 1000)));
        m.Nodes.Add(new Node(4, new Point3(1000, 0, 1000)));

        m.Materials.Add(new Material(1, "Steel",       200000, 0.3, SteelRho));
        m.Materials.Add(new Material(2, "Steel+Fluid", 200000, 0.3, FluidAugmentedRho));

        // Structure: Rod dims=[R], Material 1 (강재)
        m.Sections.Add(new BeamSection(10, BeamSectionKind.Rod, [25.0], 1));
        // Pipe: Tube dims=[R_out, R_in], Material 2 (강재+유체)
        m.Sections.Add(new BeamSection(20, BeamSectionKind.Tube, [50.0, 40.0], 2));

        m.Elements.Add(new BeamElement(1, 1, 2, 10, EntityCategory.Structure, Vector3.UnitZ));
        m.Elements.Add(new BeamElement(2, 3, 4, 20, EntityCategory.Pipe,      Vector3.UnitZ));
        return m;
    }
}
