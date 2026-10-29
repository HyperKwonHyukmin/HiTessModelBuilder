using Cmb.Core.Building;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Core.Model.Raw;
using FluentAssertions;

namespace Cmb.Core.Tests.Building;

public class FeModelBuilderPipeTests
{
    // ── BEND / ELBO ───────────────────────────────────────────────────────────

    [Fact]
    public void Bend_WithInterPos_CreatesPolylineElements()
    {
        // InterPos 3점(9 doubles) → chain: APos→p0→p1→p2→LPos → 4개 요소
        var row = MakePipe("B", "BEND",
            pos:      [500, 0, 500],
            aPos:     [0,   0, 0],
            lPos:     [1000, 0, 0],
            interPos: [250, 0, 250,  500, 0, 500,  750, 0, 250]);
        var model = Build(row);

        model.Elements.Should().HaveCount(4);
        model.Elements.All(e => e.Category == EntityCategory.Pipe).Should().BeTrue();
    }

    [Fact]
    public void Elbo_WithInterPosSixDoubles_CreatesThreeElements()
    {
        // InterPos 2점(6 doubles) → chain: APos→p0→p1→LPos → 3개 요소
        var row = MakePipe("E", "ELBO",
            pos:      [500, 0, 500],
            aPos:     [0,   0, 0],
            lPos:     [1000, 0, 0],
            interPos: [300, 0, 300,  700, 0, 300]);
        var model = Build(row);

        model.Elements.Should().HaveCount(3);
    }

    [Fact]
    public void Bend_WithPosAndNoInterPos_CreatesTwoElements()
    {
        // InterPos 없고 Pos(꺾임점) 있음 → APos→Pos→LPos → 2개 요소
        var row = MakePipe("B", "BEND",
            pos:  [500, 0, 500],
            aPos: [0,   0, 0],
            lPos: [1000, 0, 0]);
        var model = Build(row);

        model.Elements.Should().HaveCount(2);
        // 꺾임 절점(Pos) 포함 총 3개 노드
        model.Nodes.Should().HaveCount(3);
    }

    [Fact]
    public void Bend_NoPosNoInterPos_CreatesSingleElement()
    {
        // Pos도 InterPos도 없으면 APos→LPos 직선 하나
        var row = new RawPipeRow("B", "BEND", "X",
            Pos:      [],
            APos:     [0, 0, 0],
            LPos:     [1000, 0, 0],
            Normal:   [0, 0, 1],
            InterPos: null,
            P3Pos:    null,
            Rest:     null,
            OutDia: 73, Thick: 3.05, OutDia2: 0, Thick2: 0, Mass: 0, Remark: null);
        var model = Build(row);

        model.Elements.Should().HaveCount(1);
    }

    [Fact]
    public void Tubi_WithNoInterPos_CreatesSingleElement()
    {
        // TUBI는 Pos를 꺾임점으로 쓰지 않음 → 항상 APos→LPos 단일 요소
        var row = MakePipe("T", "TUBI",
            pos:  [500, 0, 500],
            aPos: [0,   0, 0],
            lPos: [1000, 0, 0]);
        var model = Build(row);

        model.Elements.Should().HaveCount(1);
    }

    [Fact]
    public void Tubi_WithInterPos_CreatesPolyline()
    {
        var row = MakePipe("T", "TUBI",
            pos:      [0, 0, 0],
            aPos:     [0, 0, 0],
            lPos:     [3000, 0, 0],
            interPos: [1000, 0, 0,  2000, 0, 0]);
        var model = Build(row);

        model.Elements.Should().HaveCount(3);
    }

    // ── TEE ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Tee_SplitsMainPipeAtCenter()
    {
        // TEE: APos→Pos, Pos→LPos, Pos→P3Pos = 3개 요소
        var row = new RawPipeRow("T", "TEE", "B1",
            Pos:      [500, 0, 0],
            APos:     [0,   0, 0],
            LPos:     [1000, 0, 0],
            Normal:   [0, 0, 1],
            InterPos: null,
            P3Pos:    [500, 500, 0],
            Rest:     null,
            OutDia: 73, Thick: 3.05, OutDia2: 48.3, Thick2: 2.77,
            Mass: 0, Remark: null);
        var model = Build(row);

        model.Elements.Should().HaveCount(3);
        // 중심 노드(Pos)가 3개 요소 모두에 참조되어야 함
        var centerNode = model.Nodes.First(n => Math.Abs(n.Position.X - 500) < 0.01 && Math.Abs(n.Position.Y) < 0.01);
        var elemsUsingCenter = model.Elements.Count(e =>
            e.StartNodeId == centerNode.Id || e.EndNodeId == centerNode.Id);
        elemsUsingCenter.Should().Be(3);
    }

    [Fact]
    public void Tee_WithoutP3Pos_CreatesTwoMainElements()
    {
        // P3Pos 없으면 메인관 2개만 (APos→center, center→LPos)
        var row = new RawPipeRow("T", "TEE", "B1",
            Pos:      [500, 0, 0],
            APos:     [0,   0, 0],
            LPos:     [1000, 0, 0],
            Normal:   [0, 0, 1],
            InterPos: null,
            P3Pos:    null,
            Rest:     null,
            OutDia: 73, Thick: 3.05, OutDia2: 0, Thick2: 0,
            Mass: 0, Remark: null);
        var model = Build(row);

        model.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void Tee_BranchUsesOutDia2Section()
    {
        // 분기관 단면은 OutDia2/Thick2 기반이어야 함
        var row = new RawPipeRow("T", "TEE", "B1",
            Pos:      [500, 0, 0],
            APos:     [0,   0, 0],
            LPos:     [1000, 0, 0],
            Normal:   [0, 0, 1],
            InterPos: null,
            P3Pos:    [500, 500, 0],
            Rest:     null,
            OutDia: 73, Thick: 3.05, OutDia2: 48.3, Thick2: 2.77,
            Mass: 0, Remark: null);
        var model = Build(row);

        // 메인 단면과 분기 단면이 별도로 생성
        model.Sections.Should().HaveCount(2);
        var mainSec   = model.Sections.OrderByDescending(s => s.Dims[0]).First();
        var branchSec = model.Sections.OrderByDescending(s => s.Dims[0]).Last();
        mainSec.Dims[0].Should().BeApproximately(73.0 / 2.0, 1e-9);   // R_out of main
        branchSec.Dims[0].Should().BeApproximately(48.3 / 2.0, 1e-9); // R_out of branch
    }

    [Fact]
    public void Tee_BranchFallsBackToMainDimsWhenOutDia2IsZero()
    {
        var row = new RawPipeRow("T", "TEE", "B1",
            Pos:      [500, 0, 0],
            APos:     [0,   0, 0],
            LPos:     [1000, 0, 0],
            Normal:   [0, 0, 1],
            InterPos: null,
            P3Pos:    [500, 500, 0],
            Rest:     null,
            OutDia: 73, Thick: 3.05, OutDia2: 0, Thick2: 0,
            Mass: 0, Remark: null);
        var model = Build(row);

        // OutDia2=0 → 분기가 메인과 동일 단면 → 단면 1개
        model.Sections.Should().HaveCount(1);
        model.Elements.Should().HaveCount(3);
    }

    // ── 인라인 장비 (VALV / TRAP 등) ─────────────────────────────────────────

    [Fact]
    public void Valv_WithOutDia_CreatesOnlyPointMass()
    {
        // VALV는 OutDia > 0이어도 PointMass만 생성
        var row = MakePipe("V", "VALV",
            pos:  [500, 0, 0],
            aPos: [0,   0, 0],
            lPos: [1000, 0, 0],
            outDia: 73, thick: 3.05, mass: 14.3);
        var model = Build(row);

        model.Elements.Should().BeEmpty();
        model.PointMasses.Should().HaveCount(1);
        // CSV mass(kg) → tonne 변환 (×0.001)
        model.PointMasses[0].Mass.Should().BeApproximately(0.0143, 1e-9);
    }

    [Theory]
    [InlineData("TRAP")]
    [InlineData("FILT")]
    [InlineData("EXP")]
    public void InlineEquip_AlwaysCreatesPointMassOnly(string type)
    {
        var row = MakePipe("X", type,
            pos: [500, 0, 0], aPos: [0, 0, 0], lPos: [1000, 0, 0],
            outDia: 50, thick: 3, mass: 5.0);
        var model = Build(row);

        model.Elements.Should().BeEmpty();
        model.PointMasses.Should().HaveCount(1);
    }

    // ── H-1: 관 두께 >= 외경/2 → 음수 PBEAML 내부 반경 방지 (형상 드롭) ────────

    [Fact]
    public void LinePipe_WallThicknessAtLeastHalfOutDia_DropsGeometryInsteadOfNegativeSection()
    {
        // thick(30) >= outDia(50)/2 → 내부 반경 -5. 조용한 음수 단면 대신 형상을 드롭한다.
        var row = MakePipe("TUBI", "TUBI",
            pos:  [500, 0, 0],
            aPos: [0,   0, 0],
            lPos: [1000, 0, 0],
            outDia: 50, thick: 30);
        var model = Build(row);

        model.Elements.Should().BeEmpty();
        model.Sections.Should().BeEmpty();
    }

    [Fact]
    public void NonLinePipeFlange_WallTooThick_KeepsMassAsPointMassWithoutBadSection()
    {
        // FLAN 은 line-pipe 가 아니므로, 형상은 드롭하되 질량은 point mass 로 보존한다.
        var row = MakePipe("F", "FLAN",
            pos:  [500, 0, 0],
            aPos: [0,   0, 0],
            lPos: [1000, 0, 0],
            outDia: 50, thick: 30, mass: 5.0);
        var model = Build(row);

        model.Elements.Should().BeEmpty();
        model.Sections.Should().BeEmpty();
        model.PointMasses.Should().HaveCount(1);
        model.PointMasses[0].Mass.Should().BeApproximately(0.005, 1e-9);
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────

    private static FeModel Build(RawPipeRow row) =>
        new FeModelBuilder().Build(new RawDesignData([], [row], [], []));

    private static RawPipeRow MakePipe(
        string name, string type,
        double[] pos, double[] aPos, double[] lPos,
        double[]? interPos = null,
        double outDia = 73, double thick = 3.05, double mass = 0) =>
        new(name, type, "B1",
            Pos:      pos,
            APos:     aPos,
            LPos:     lPos,
            Normal:   [0, 0, 1],
            InterPos: interPos,
            P3Pos:    null,
            Rest:     null,
            OutDia: outDia, Thick: thick, OutDia2: 0, Thick2: 0,
            Mass: mass, Remark: null);
}
