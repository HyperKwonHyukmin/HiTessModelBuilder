using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class DanglingShortRemoveStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, double threshold = 50.0)
    {
        var tol = new Tolerances(DanglingShortLengthMm: threshold);
        return new(model, new RunOptions(tol), NullLogger.Instance);
    }

    private static Node N(int id, double x, double y = 0, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Up);

    // ── 삭제 케이스 ────────────────────────────────────────────────────────────

    [Fact]
    public void RemovesDanglingShortElement()
    {
        // 구성:  N1--E1--N2--E2--N3
        //                   |
        //                   E3
        //                   |
        //                   N4  ← degree=1, 30mm 꼬투리
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 2000), N(4, 1000, 30)]);
        model.Elements.Add(E(1, 1, 2)); // 1000mm
        model.Elements.Add(E(2, 2, 3)); // 1000mm
        model.Elements.Add(E(3, 2, 4)); // 30mm, degree-1 at N4

        new DanglingShortRemoveStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2);
        model.Elements.Select(e => e.Id).Should().NotContain(3);
    }

    [Fact]
    public void RemovesDiagnosticWhenSomethingRemoved()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1000, 20)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 2, 3)); // 20mm, degree-1 at N3

        var ctx = MakeCtx(model);
        new DanglingShortRemoveStage().Execute(ctx);

        ctx.Diagnostics.Should().Contain(d => d.Code == "DANGLING_SHORT_REMOVED");
    }

    // ── 유지 케이스 ────────────────────────────────────────────────────────────

    [Fact]
    public void KeepsShortElementWithBothEndsConnected()
    {
        // 사각형 루프: 모든 노드 degree=2 → 짧아도 삭제 안 함
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 30), N(3, 30, 30), N(4, 0, 30)]);
        model.Elements.Add(E(1, 1, 2)); // 30mm
        model.Elements.Add(E(2, 2, 3)); // 30mm
        model.Elements.Add(E(3, 3, 4)); // 30mm
        model.Elements.Add(E(4, 4, 1)); // 30mm

        new DanglingShortRemoveStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(4);
    }

    [Fact]
    public void KeepsLongElementWithFreeEnd()
    {
        // 길이 100mm, degree-1 끝점 있음 → threshold(50mm) 이상이므로 유지
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1000, 100)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 2, 3)); // 100mm, degree-1 at N3

        new DanglingShortRemoveStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void CustomThresholdRespected()
    {
        // threshold=20mm 으로 설정 → 30mm 꼬투리는 유지
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1000, 30)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 2, 3)); // 30mm, degree-1 at N3

        new DanglingShortRemoveStage().Execute(MakeCtx(model, threshold: 20.0));

        model.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void MultiplePassesAreIdempotent()
    {
        // 한 번 정리 후 재실행해도 변화 없음
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0), N(2, 1000), N(3, 1000, 10)]);
        model.Elements.Add(E(1, 1, 2));
        model.Elements.Add(E(2, 2, 3)); // 꼬투리 → 1회차에서 제거

        var stage = new DanglingShortRemoveStage();
        stage.Execute(MakeCtx(model));
        int countAfterFirst = model.Elements.Count;

        stage.Execute(MakeCtx(model));
        model.Elements.Should().HaveCount(countAfterFirst);
    }

    [Fact]
    public void EmptyModelIsNoop()
    {
        var model = new FeModel();
        var ctx   = MakeCtx(model);

        var act = () => new DanglingShortRemoveStage().Execute(ctx);

        act.Should().NotThrow();
        ctx.Diagnostics.Should().BeEmpty();
    }
}
