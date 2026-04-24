namespace Cmb.Pipeline.Core;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string             Code,
    string             Message,
    int?               ElementId = null,
    int?               NodeId    = null);
