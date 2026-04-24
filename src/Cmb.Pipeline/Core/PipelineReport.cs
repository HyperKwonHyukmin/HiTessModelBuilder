namespace Cmb.Pipeline.Core;

public sealed class StageReport
{
    public string               Name        { get; }
    public bool                 Succeeded   { get; }
    public TimeSpan             Elapsed     { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public StageReport(string name, bool succeeded, TimeSpan elapsed, IReadOnlyList<Diagnostic> diagnostics)
    {
        Name        = name;
        Succeeded   = succeeded;
        Elapsed     = elapsed;
        Diagnostics = diagnostics;
    }
}

public sealed class PipelineReport
{
    public bool                     Succeeded        { get; }
    public string?                  StoppedAfterStage { get; }
    public IReadOnlyList<StageReport> Stages          { get; }
    public IReadOnlyList<Diagnostic>  AllDiagnostics  { get; }

    public PipelineReport(bool succeeded, string? stoppedAfterStage, IReadOnlyList<StageReport> stages)
    {
        Succeeded         = succeeded;
        StoppedAfterStage = stoppedAfterStage;
        Stages            = stages;
        AllDiagnostics    = stages.SelectMany(s => s.Diagnostics).ToList().AsReadOnly();
    }
}
