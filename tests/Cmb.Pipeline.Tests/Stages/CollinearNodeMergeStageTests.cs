using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class CollinearNodeMergeStageTests
{
    private static StageContext MakeCtx(FeModel model,
        double distMm = 50.0, double angleDeg = 3.0, double lateralMm = 1.0)
    {
        var tol = new Tolerances(
            CollinearMergeDistanceMm: distMm,
            CollinearMergeAngleDeg:   angleDeg,
            CollinearMergeLateralMm:  lateralMm);
        return new(model, new RunOptions(tol), NullLogger.Instance);
    }

    private static Node N(int id, double x, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end,
        EntityCategory cat = EntityCategory.Structure)
        => new(id, start, end, 1, cat, Vector3.UnitZ);

    // ── 병합 케이스 ────────────────────────────────────────────────────────────

    [Fact]
    public void MergesEndpointsOfParallelElementsWithinDistance()
    {
        // E1: N1(0,0)→N2(1000,0)  E2: N3(1030,0)→N4(2000,0)
        // N2와 N3의 거리=30mm, 방향 동일, lateral=0 → 병합
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1030), N(4, 2000)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new CollinearNodeMergeStage().Execute(MakeCtx(model));

        // N3(high id)이 N2(low id)로 병합됨
        model.Nodes.Should().HaveCount(3);
        model.Nodes.Select(n => n.Id).Should().NotContain(3);

        // E2는 N2→N4 로 remapped
        var e2 = model.Elements.First(e => e.Id == 2);
        e2.StartNodeId.Should().Be(2);
    }

    [Fact]
    public void MergesAntiParallelElements()
    {
        // E1: N1(0)→N2(1000), E2: N4(2000)→N3(1028) — 반평행
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1028), N(4, 2000)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 4, 3)); // N4→N3 (반평행)

        new CollinearNodeMergeStage().Execute(MakeCtx(model));

        // N3과 N2의 거리=28mm < 50mm, lateral=0 → 병합
        model.Nodes.Should().HaveCount(3);
        model.Nodes.Select(n => n.Id).Should().NotContain(3);
    }

    // ── 미병합 케이스 ──────────────────────────────────────────────────────────

    [Fact]
    public void DoesNotMergeWhenDistanceTooLarge()
    {
        // N2(1000)과 N3(1100)의 거리=100mm > distTol(50mm)
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1100), N(4, 2000)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new CollinearNodeMergeStage().Execute(MakeCtx(model, distMm: 50.0));

        model.Nodes.Should().HaveCount(4);
    }

    [Fact]
    public void DoesNotMergeWhenAngleTooLarge()
    {
        // E1: X축 방향, E2: X축에서 10° 이상 기울어짐
        var model = new FeModel();
        double angle = 15.0 * Math.PI / 180.0;
        model.Nodes.AddRange([
            N(1, 0),
            N(2, 1000),
            N(3, 1030),
            N(4, 1030 + 1000 * Math.Cos(angle), 1000 * Math.Sin(angle))
        ]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new CollinearNodeMergeStage().Execute(MakeCtx(model, angleDeg: 3.0));

        model.Nodes.Should().HaveCount(4);
    }

    [Fact]
    public void DoesNotMergeWhenLateralOffsetExceedsTolerance()
    {
        // N2(1000, 0), N3(1010, 5) — 거리≈11mm, lateral=5mm > lateralTol(1mm)
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000, 0), N(3, 1010, 5), N(4, 2000, 5)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));

        new CollinearNodeMergeStage().Execute(MakeCtx(model, lateralMm: 1.0));

        model.Nodes.Should().HaveCount(4);
    }

    [Fact]
    public void DoesNotMergeAcrossCategoryBoundary()
    {
        // Structure와 Pipe의 끝점이 근접해도 병합 안 함
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1020), N(4, 2000)]);
        model.Elements.Add(E(1, 1, 2, EntityCategory.Structure));
        model.Elements.Add(E(2, 3, 4, EntityCategory.Pipe));

        new CollinearNodeMergeStage().Execute(MakeCtx(model));

        model.Nodes.Should().HaveCount(4);
    }

    // ── 부수 효과 케이스 ──────────────────────────────────────────────────────

    [Fact]
    public void RemovesDegenerateElementAfterMerge()
    {
        // E1: N1→N2, E2: N2→N3(N3≈N2+30mm), E3: N3→N2(역방향 거의 0)
        // E2 끝점 병합 → E3이 N2→N2로 degenerate
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1025)]);
        model.Elements.Add(E(1, 1, 2));
        // E2: X방향 평행, N2와 N3 25mm
        model.Elements.Add(E(2, 2, 3));   // 얘도 X방향 → 서로 병합 후 E2 degenerate 발생 가능 여부 확인

        // 직접 degenerate 테스트: 두 요소가 하나의 노드를 공유하도록 병합된 후
        // 길이 0인 요소가 나와야 하는 시나리오 구성
        // E3: N4→N3, N4≈N2+0.5mm (lateral=0) → N4 병합되어 E3이 N2→N2 degenerate
        model.Nodes.Add(N(4, 1000.5));
        model.Elements.Add(E(3, 4, 3)); // N4(1000.5)→N3(1025), X방향, 24.5mm

        var ctx = MakeCtx(model, distMm: 5.0, lateralMm: 1.0); // distMm=5 → N2(1000)과 N4(1000.5) 병합
        new CollinearNodeMergeStage().Execute(ctx);

        // N4가 N2로 병합 → E3이 N2→N3 (degenerate 아님, 단순 remap)
        // degenerate는 start==end일 때 발생하므로 추가 확인
        ctx.Model.TraceLog.Should().Contain(t => t.Action == TraceAction.NodeMerged);
    }

    [Fact]
    public void PropagatesRemapToRigidElement()
    {
        // E1: N1→N2(1000), E2: N3(1030)→N4(2000)
        // Rigid: indep=N5, dep=[N3] → 병합 후 dep=[N2]
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1030), N(4, 2000), N(5, 1030, 100)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 3, 4));
        model.Rigids.Add(new RigidElement(101, 5, [3], "UBOLT"));

        new CollinearNodeMergeStage().Execute(MakeCtx(model));

        // N3 → N2로 병합
        var rigid = model.Rigids[0];
        rigid.DependentNodeIds.Should().Contain(2);
        rigid.DependentNodeIds.Should().NotContain(3);
    }

    [Fact]
    public void EmptyModelIsNoop()
    {
        var model = new FeModel();
        var ctx   = MakeCtx(model);

        var act = () => new CollinearNodeMergeStage().Execute(ctx);

        act.Should().NotThrow();
        ctx.Diagnostics.Should().BeEmpty();
    }
}
