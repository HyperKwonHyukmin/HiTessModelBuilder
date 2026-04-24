using Cmb.Core.Model;
using Cmb.Core.Model.Context;
using Microsoft.Extensions.Logging;

namespace Cmb.Pipeline.Core;

public sealed class StageContext
{
    public FeModel    Model      { get; }
    public RunOptions Options    { get; }
    public ILogger    Logger     { get; }

    private readonly List<Diagnostic> _diagnostics = [];
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public StageContext(FeModel model, RunOptions options, ILogger logger)
    {
        Model   = model;
        Options = options;
        Logger  = logger;
    }

    public void AddDiagnostic(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void AddDiagnostic(DiagnosticSeverity severity, string code, string message,
        int? elementId = null, int? nodeId = null)
        => _diagnostics.Add(new Diagnostic(severity, code, message, elementId, nodeId));

    public void AddTrace(TraceAction action, string stageName,
        int? elementId = null, int? nodeId = null,
        int? relatedElementId = null, int? relatedNodeId = null,
        string? note = null)
    {
        if (!Options.RecordTrace) return;
        Model.AddTrace(action, stageName, elementId, nodeId, relatedElementId, relatedNodeId, note);
    }
}
