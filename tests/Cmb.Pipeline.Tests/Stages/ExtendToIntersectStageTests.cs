using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class ExtendToIntersectStageTests
{
    private static StageContext MakeCtx(FeModel model,
        double margin    = 100.0,
        double coplTol   = 10.0,
        double snapTol   = 2.0,
        int    maxIter   = 10)
    {
        var tol = new Tolerances(
            ExtendExtraMarginMm  : margin,
            ExtendCoplanarTolMm  : coplTol,
            ExtendSnapLateralMm  : snapTol,
            ExtendMaxIterations  : maxIter);
        return new(model, new RunOptions(tol), NullLogger.Instance);
    }

    private static Node N(int id, double x, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int s, int e,
        EntityCategory cat = EntityCategory.Structure, int propId = 1)
        => new(id, s, e, propId, cat, Vector3.UnitZ);

    // H200×100 단면: Dims=[100,8,200,12] → MaxCrossSectionDim = 200
    private static BeamSection HSection(int id = 1)
        => new(id, BeamSectionKind.H, [100, 8, 200, 12], 1);

    // ── pSeg 모드 (lateral = 0) ──────────────────────────────────────────────

    [Fact]
    public void ExtendsFreeEndOntoTargetSegment_pSeg()
    {
        // E1: N1(0)→N2(1000) — free end at N2
        // B:  N3(1080,-200)→N4(1080, 200) — vertical at X=1080
        // lateral=0, s=80mm, margin=100 → MaxDim+margin=200+100=300 > 80 → hit
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1080, -200), N(4, 1080, 200)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1080, 1e-3);
        n2.Position.Y.Should().BeApproximately(0,    1e-3);

        model.Nodes.Should().HaveCount(4);
    }

    // ── pRay 모드 (lateral > snapTol but ≤ coplTol) ─────────────────────────

    [Fact]
    public void ExtendsUsingRayWhenLateralWithinCoplanarButAboveSnap()
    {
        // E1: N1(0)→N2(1000), free end N2
        // B:  N3(1080,5,-200)→N4(1080,5,200) — Y=5mm offset
        // lateral=5 > snapTol(2) but ≤ coplTol(10) → pRay: N2 → (1080,0,0)
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1080, 5, -200), N(4, 1080, 5, 200)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model, snapTol: 2.0, coplTol: 10.0));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1080, 1e-3);
        n2.Position.Y.Should().BeApproximately(0,    1e-3); // pRay: 방향 보존, Y=0
    }

    // ── 미연장 케이스 ─────────────────────────────────────────────────────────

    [Fact]
    public void DoesNotExtendWhenLateralExceedsCoplanarTol()
    {
        // lateral=20mm > coplTol(10) → hit=false, N2 stays
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1080, 20, -200), N(4, 1080, 20, 200)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model, coplTol: 10.0));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1000, 1e-9);
    }

    [Fact]
    public void DoesNotExtendPipe()
    {
        // A는 Pipe → 연장 대상 제외
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1080, -200), N(4, 1080, 200)]);
        model.Elements.Add(E(1, 1, 2, EntityCategory.Pipe));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1000, 1e-9);
    }

    [Fact]
    public void DoesNotExtendWhenBeyondSearchRadius()
    {
        // MaxDim=200, margin=10 → searchRadius=210
        // Target at X=1300 → s=300 > 210 → 미연장
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1300, -200), N(4, 1300, 200)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model, margin: 10.0));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1000, 1e-9);
    }

    // ── 부수 효과 케이스 ──────────────────────────────────────────────────────

    [Fact]
    public void SelectsNearestCandidateWhenTwoTargetsExist()
    {
        // E1: N1(0)→N2(1000) free end N2
        // 두 타겟: E2 at X=1040 (s=40), E3 at X=1080 (s=80)
        // 알고리즘은 가장 가까운 E2(s=40)에 먼저 연결해야 함
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([
            N(1, 0), N(2, 1000),
            N(3, 1040, -200), N(4, 1040, 200),  // 가까운 타겟
            N(5, 1080, -200), N(6, 1080, 200)   // 먼 타겟
        ]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));
        model.Elements.Add(E(3, 5, 6));

        new ExtendToIntersectStage().Execute(MakeCtx(model));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1040, 1e-3); // 가까운 쪽에 연결
    }

    [Fact]
    public void ExtendsBothParallelFreeEnds()
    {
        // E1: N1(0,0)→N2(1000,0)  free end N2
        // E2: N3(0,50)→N4(1000,50) free end N4
        // Target E3: N5(1080,-200)→N6(1080,200)
        // N2의 ray: (1000,0)→+X, 타겟 세그먼트와 교차점 = (1080,0), lateral=0 → pSeg
        // N4의 ray: (1000,50)→+X, 타겟 세그먼트와 교차점 = (1080,50), lateral=0 → pSeg
        // 두 자유단 모두 X=1080으로 이동해야 함 (각각의 Y 유지)
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([
            N(1, 0, 0), N(2, 1000, 0),
            N(3, 0, 50), N(4, 1000, 50),
            N(5, 1080, -200), N(6, 1080, 200)
        ]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));
        model.Elements.Add(E(3, 5, 6));

        new ExtendToIntersectStage().Execute(MakeCtx(model));

        var n2 = model.Nodes.First(n => n.Id == 2);
        n2.Position.X.Should().BeApproximately(1080, 1e-3);
        n2.Position.Y.Should().BeApproximately(0, 1e-3);

        var n4 = model.Nodes.First(n => n.Id == 4);
        n4.Position.X.Should().BeApproximately(1080, 1e-3);
        n4.Position.Y.Should().BeApproximately(50, 1e-3);
    }

    [Fact]
    public void PreservesBoundaryNodeTagAfterExtend()
    {
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.Add(N(1, 0));
        var n2 = new Node(2, new Point3(1000, 0, 0), NodeTags.Boundary);
        model.Nodes.Add(n2);
        model.Nodes.AddRange([N(3, 1080, -200), N(4, 1080, 200)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model));

        var moved = model.Nodes.First(n => n.Id == 2);
        moved.HasTag(NodeTags.Boundary).Should().BeTrue();
        moved.Position.X.Should().BeApproximately(1080, 1e-3);
    }

    [Fact]
    public void RecordsNodeMovedTrace()
    {
        var model = new FeModel();
        model.Sections.Add(HSection());
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1080, -200), N(4, 1080, 200)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new ExtendToIntersectStage().Execute(MakeCtx(model));

        model.TraceLog.Should().Contain(t => t.Action == TraceAction.NodeMoved
                                          && t.StageName == "ExtendToIntersect");
    }

    [Fact]
    public void EmptyModelIsNoop()
    {
        var model = new FeModel();
        var ctx   = MakeCtx(model);

        var act = () => new ExtendToIntersectStage().Execute(ctx);

        act.Should().NotThrow();
        ctx.Diagnostics.Should().BeEmpty();
    }
}
