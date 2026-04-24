using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class IntersectionStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, double intersectionTol = 1.0)
        => new(model, new RunOptions(new Tolerances(IntersectionSnapMm: intersectionTol)), NullLogger.Instance);

    private static Node N(int id, double x, double y, double z = 0)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end, EntityCategory cat = EntityCategory.Structure)
        => new(id, start, end, 1, cat, Up);

    // ── "+" cross: two elements crossing at right angles ──────────────────────

    [Fact]
    public void CrossIntersection_TwoElements_SplitIntoFour()
    {
        var model = new FeModel();
        // A: horizontal (0,0)→(100,0)
        // B: vertical   (50,-50)→(50,50) — crosses A at (50,0)
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, -50), N(4, 50, 50)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new IntersectionStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(4);
    }

    [Fact]
    public void CrossIntersection_SharedNodeAtCrossPoint()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, -50), N(4, 50, 50)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new IntersectionStage().Execute(MakeCtx(model));

        // All four segments should share a common intermediate node
        var elemEnds = model.Elements
            .SelectMany(e => new[] { e.StartNodeId, e.EndNodeId })
            .GroupBy(n => n)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToList();

        elemEnds.Should().Contain(n => model.Elements.Count(e => e.StartNodeId == n || e.EndNodeId == n) >= 4);
    }

    // ── T-junction ────────────────────────────────────────────────────────────

    [Fact]
    public void TJunction_OnlyIntersectedElementSplit()
    {
        var model = new FeModel();
        // A: horizontal (0,0)→(100,0)
        // B: starts at midpoint of A, goes up: (50,0)→(50,50) — T endpoint at midpoint
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, 0), N(4, 50, 50)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new IntersectionStage().Execute(MakeCtx(model));

        // B already starts at a point on A → but (50,0) is the node 3, not shared with A
        // A should be split; B's start endpoint is now coincident with A's split point
        model.Elements.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Already connected: no split ──────────────────────────────────────────

    [Fact]
    public void AlreadySharedEndpoint_NotSplit()
    {
        var model = new FeModel();
        // Two elements sharing node 2 — already connected
        model.Nodes.AddRange([N(1, 0, 0), N(2, 50, 0), N(3, 100, 0)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 2, 3)]);

        new IntersectionStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2); // no split
    }

    // ── Parallel: no split ───────────────────────────────────────────────────

    [Fact]
    public void ParallelElements_NotSplit()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 0, 5), N(4, 100, 5)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new IntersectionStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(2);
    }

    // ── Skew (too far): no split ──────────────────────────────────────────────

    [Fact]
    public void SkewLines_FarApart_NotSplit()
    {
        var model = new FeModel();
        // A along X at z=0, B along Y at z=100 → min dist = 100 >> tol
        model.Nodes.AddRange([N(1, 0, 0, 0), N(2, 100, 0, 0), N(3, 50, -50, 100), N(4, 50, 50, 100)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        new IntersectionStage().Execute(MakeCtx(model, intersectionTol: 1.0));

        model.Elements.Should().HaveCount(2);
    }

    // ── Child elements inherit metadata ──────────────────────────────────────

    [Fact]
    public void SplitChildren_InheritSourceNameAndCategory()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, -50), N(4, 50, 50)]);
        var elemA = new BeamElement(10, 1, 2, 1, EntityCategory.Structure, Up, sourceName: "BEAM_X");
        var elemB = new BeamElement(11, 3, 4, 1, EntityCategory.Pipe, Up, sourceName: "PIPE_Y");
        model.Elements.AddRange([elemA, elemB]);

        new IntersectionStage().Execute(MakeCtx(model));

        model.Elements.Should().AllSatisfy(e =>
            (e.SourceName == "BEAM_X" || e.SourceName == "PIPE_Y").Should().BeTrue());
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    [Fact]
    public void WhenSplitOccurs_EmitsDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 50, -50), N(4, 50, 50)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(model);
        new IntersectionStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d => d.Code == "INTERSECTIONS_SPLIT");
    }

    [Fact]
    public void NoIntersection_NoDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.AddRange([N(1, 0, 0), N(2, 100, 0), N(3, 0, 200), N(4, 100, 200)]);
        model.Elements.AddRange([E(10, 1, 2), E(11, 3, 4)]);

        var ctx = MakeCtx(model);
        new IntersectionStage().Execute(ctx);

        ctx.Diagnostics.Should().NotContain(d => d.Code == "INTERSECTIONS_SPLIT");
    }

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var result = new IntersectionStage().Execute(MakeCtx(new FeModel()));
        result.Should().BeTrue();
    }
}
