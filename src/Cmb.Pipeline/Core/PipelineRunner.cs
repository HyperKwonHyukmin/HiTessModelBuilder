using System.Diagnostics;
using Cmb.Core.Model.Context;
using Microsoft.Extensions.Logging;

namespace Cmb.Pipeline.Core;

public static class PipelineRunner
{
    public static PipelineReport Run(
        FeModel                                                                    model,
        IReadOnlyList<IPipelineStage>                                              stages,
        RunOptions                                                                 options,
        ILogger                                                                    logger,
        Action<string, FeModel, IReadOnlyList<Diagnostic>, long, StageDeltaInfo>? onStageComplete = null)
    {
        var stageReports     = new List<StageReport>();
        string? stoppedAfter = null;
        bool pipelineOk      = true;
        string prevStageName = "initial";

        foreach (var stage in stages)
        {
            logger.LogInformation("Stage [{Name}] starting", stage.Name);

            var ctx        = new StageContext(model, options, logger);
            int elemBefore = model.Elements.Count;
            int nodeBefore = model.Nodes.Count;
            var sw         = Stopwatch.StartNew();
            bool ok        = false;

            try
            {
                ok = stage.Execute(ctx);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stage [{Name}] threw an exception", stage.Name);
                ctx.AddDiagnostic(DiagnosticSeverity.Error, "STAGE_EXCEPTION",
                    $"{stage.Name}: {ex.Message}");
                ok = false;
            }

            sw.Stop();
            var delta = new StageDeltaInfo(prevStageName,
                elemBefore, model.Elements.Count,
                nodeBefore, model.Nodes.Count);
            stageReports.Add(new StageReport(stage.Name, ok, sw.Elapsed, ctx.Diagnostics));

            logger.LogInformation("Stage [{Name}] {Result} in {Elapsed:F0}ms",
                stage.Name, ok ? "succeeded" : "FAILED", sw.Elapsed.TotalMilliseconds);

            onStageComplete?.Invoke(stage.Name, model, ctx.Diagnostics,
                (long)sw.Elapsed.TotalMilliseconds, delta);

            prevStageName = stage.Name;

            if (!ok)
            {
                pipelineOk = false;
                break;
            }

            if (options.StopAfterStage is not null &&
                string.Equals(stage.Name, options.StopAfterStage, StringComparison.OrdinalIgnoreCase))
            {
                stoppedAfter = stage.Name;
                break;
            }
        }

        return new PipelineReport(pipelineOk, stoppedAfter, stageReports);
    }
}
