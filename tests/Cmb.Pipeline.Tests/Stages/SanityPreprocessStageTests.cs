using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using Cmb.Pipeline.Stages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Stages;

public class SanityPreprocessStageTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    private static StageContext MakeCtx(FeModel model, RunOptions? options = null)
        => new(model, options ?? RunOptions.Default, NullLogger.Instance);

    private static Node N(int id, double x, double y, double z)
        => new(id, new Point3(x, y, z));

    private static BeamElement E(int id, int start, int end)
        => new(id, start, end, 1, EntityCategory.Structure, Up);

    // ── Duplicate removal ─────────────────────────────────────────────────────

    [Fact]
    public void DuplicateElement_SameNodePair_SecondRemoved()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 1000, 0, 0));
        model.Elements.Add(E(10, 1, 2));
        model.Elements.Add(E(11, 1, 2)); // duplicate

        new SanityPreprocessStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(1);
        model.Elements[0].Id.Should().Be(10);
    }

    [Fact]
    public void DuplicateElement_ReversedNodeOrder_AlsoRemoved()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 1000, 0, 0));
        model.Elements.Add(E(10, 1, 2));
        model.Elements.Add(E(11, 2, 1)); // reversed — same topology

        new SanityPreprocessStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(1);
    }

    [Fact]
    public void DuplicateElement_EmitsDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 1000, 0, 0));
        model.Elements.Add(E(10, 1, 2));
        model.Elements.Add(E(11, 1, 2));

        var ctx = MakeCtx(model);
        new SanityPreprocessStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d => d.Code == "DUPLICATE_REMOVED");
    }

    [Fact]
    public void NoDuplicates_NoDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 1000, 0, 0));
        model.Nodes.Add(N(3, 2000, 0, 0));
        model.Elements.Add(E(10, 1, 2));
        model.Elements.Add(E(11, 2, 3));

        var ctx = MakeCtx(model);
        new SanityPreprocessStage().Execute(ctx);

        ctx.Diagnostics.Should().NotContain(d => d.Code == "DUPLICATE_REMOVED");
        model.Elements.Should().HaveCount(2);
    }

    // ── Short element removal ─────────────────────────────────────────────────

    [Fact]
    public void ShortElement_BelowTolerance_Removed()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 1, 0, 0)); // 1 mm — below default ShortElemMinMm (5 mm)
        model.Elements.Add(E(10, 1, 2));

        new SanityPreprocessStage().Execute(MakeCtx(model));

        model.Elements.Should().BeEmpty();
    }

    [Fact]
    public void ShortElement_EmitsDiagnostic()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 1, 0, 0));
        model.Elements.Add(E(10, 1, 2));

        var ctx = MakeCtx(model);
        new SanityPreprocessStage().Execute(ctx);

        ctx.Diagnostics.Should().ContainSingle(d => d.Code == "SHORT_REMOVED");
    }

    [Fact]
    public void NormalElement_AboveTolerance_NotRemoved()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 100, 0, 0)); // 100 mm — above default 5 mm
        model.Elements.Add(E(10, 1, 2));

        new SanityPreprocessStage().Execute(MakeCtx(model));

        model.Elements.Should().HaveCount(1);
    }

    [Fact]
    public void ShortTolerance_CustomValue_Respected()
    {
        var model = new FeModel();
        model.Nodes.Add(N(1, 0, 0, 0));
        model.Nodes.Add(N(2, 8, 0, 0)); // 8 mm
        model.Elements.Add(E(10, 1, 2));

        var opts = new RunOptions(new Tolerances(ShortElemMinMm: 10.0)); // threshold = 10 mm
        new SanityPreprocessStage().Execute(MakeCtx(model, opts));

        model.Elements.Should().BeEmpty(); // 8 mm < 10 mm → removed
    }

    [Fact]
    public void EmptyModel_ReturnsTrue()
    {
        var result = new SanityPreprocessStage().Execute(MakeCtx(new FeModel()));
        result.Should().BeTrue();
    }
}
