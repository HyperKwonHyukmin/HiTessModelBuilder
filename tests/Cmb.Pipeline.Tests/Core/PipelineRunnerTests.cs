using Cmb.Core.Geometry;
using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Cmb.Pipeline.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cmb.Pipeline.Tests.Core;

public class PipelineRunnerTests
{
    private static FeModel EmptyModel() => new();

    private static RunOptions DefaultOptions => RunOptions.Default;

    // ── FakeStage helper ──────────────────────────────────────────────────────

    private sealed class FakeStage(string name, bool returns = true, Action<StageContext>? side = null) : IPipelineStage
    {
        public string Name => name;
        public bool Execute(StageContext ctx) { side?.Invoke(ctx); return returns; }
    }

    private sealed class ThrowingStage(string name) : IPipelineStage
    {
        public string Name => name;
        public bool Execute(StageContext ctx) => throw new InvalidOperationException("stage exploded");
    }

    // ── Empty stages ──────────────────────────────────────────────────────────

    [Fact]
    public void Run_EmptyStages_ReturnsSucceeded()
    {
        var report = PipelineRunner.Run(EmptyModel(), [], DefaultOptions, NullLogger.Instance);

        report.Succeeded.Should().BeTrue();
        report.Stages.Should().BeEmpty();
        report.StoppedAfterStage.Should().BeNull();
    }

    // ── Passing stages ────────────────────────────────────────────────────────

    [Fact]
    public void Run_AllPassingStages_ReportSucceeded()
    {
        var stages = new IPipelineStage[]
        {
            new FakeStage("S1"),
            new FakeStage("S2"),
            new FakeStage("S3"),
        };
        var report = PipelineRunner.Run(EmptyModel(), stages, DefaultOptions, NullLogger.Instance);

        report.Succeeded.Should().BeTrue();
        report.Stages.Should().HaveCount(3);
        report.Stages.All(s => s.Succeeded).Should().BeTrue();
    }

    // ── Failing stage aborts pipeline ─────────────────────────────────────────

    [Fact]
    public void Run_FailingStage_AbortsSubsequentStages()
    {
        bool thirdExecuted = false;
        var stages = new IPipelineStage[]
        {
            new FakeStage("S1"),
            new FakeStage("S2", returns: false),
            new FakeStage("S3", side: _ => thirdExecuted = true),
        };
        var report = PipelineRunner.Run(EmptyModel(), stages, DefaultOptions, NullLogger.Instance);

        report.Succeeded.Should().BeFalse();
        report.Stages.Should().HaveCount(2);
        thirdExecuted.Should().BeFalse();
    }

    // ── Exception handling ────────────────────────────────────────────────────

    [Fact]
    public void Run_ThrowingStage_RecordsErrorDiagnosticAndAborts()
    {
        var stages = new IPipelineStage[]
        {
            new ThrowingStage("BOOM"),
            new FakeStage("S2"),
        };
        var report = PipelineRunner.Run(EmptyModel(), stages, DefaultOptions, NullLogger.Instance);

        report.Succeeded.Should().BeFalse();
        report.Stages.Should().HaveCount(1);
        report.AllDiagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    // ── StopAfterStage ────────────────────────────────────────────────────────

    [Fact]
    public void Run_StopAfterStage_StopsAfterNamedStage()
    {
        bool thirdExecuted = false;
        var stages = new IPipelineStage[]
        {
            new FakeStage("S1"),
            new FakeStage("S2"),
            new FakeStage("S3", side: _ => thirdExecuted = true),
        };
        var options = DefaultOptions with { StopAfterStage = "S2" };
        var report  = PipelineRunner.Run(EmptyModel(), stages, options, NullLogger.Instance);

        report.Succeeded.Should().BeTrue();
        report.StoppedAfterStage.Should().Be("S2");
        report.Stages.Should().HaveCount(2);
        thirdExecuted.Should().BeFalse();
    }

    [Fact]
    public void Run_StopAfterStage_CaseInsensitive()
    {
        var stages  = new IPipelineStage[] { new FakeStage("NodeEquivalenceStage") };
        var options = DefaultOptions with { StopAfterStage = "nodeequivalencestage" };
        var report  = PipelineRunner.Run(EmptyModel(), stages, options, NullLogger.Instance);

        report.StoppedAfterStage.Should().Be("NodeEquivalenceStage");
    }

    // ── RecordTrace = false ───────────────────────────────────────────────────

    [Fact]
    public void Run_RecordTraceFalse_NoTraceAdded()
    {
        var model = EmptyModel();
        var stages = new IPipelineStage[]
        {
            new FakeStage("S1", side: ctx =>
                ctx.AddTrace(TraceAction.NodeMerged, "S1", nodeId: 1)),
        };
        var options = DefaultOptions with { RecordTrace = false };
        PipelineRunner.Run(model, stages, options, NullLogger.Instance);

        model.TraceLog.Should().BeEmpty();
    }

    [Fact]
    public void Run_RecordTraceTrue_TraceAdded()
    {
        var model = EmptyModel();
        var stages = new IPipelineStage[]
        {
            new FakeStage("S1", side: ctx =>
                ctx.AddTrace(TraceAction.ElementSplit, "S1", elementId: 5, relatedElementId: 1)),
        };
        PipelineRunner.Run(model, stages, DefaultOptions, NullLogger.Instance);

        model.TraceLog.Should().HaveCount(1);
        model.TraceLog[0].Action.Should().Be(TraceAction.ElementSplit);
    }

    // ── onStageComplete callback ──────────────────────────────────────────────

    [Fact]
    public void Run_OnStageComplete_CalledForEachStage()
    {
        var called = new List<string>();
        var stages = new IPipelineStage[] { new FakeStage("A"), new FakeStage("B") };
        PipelineRunner.Run(EmptyModel(), stages, DefaultOptions, NullLogger.Instance,
            (name, _, _) => called.Add(name));

        called.Should().Equal("A", "B");
    }
}
