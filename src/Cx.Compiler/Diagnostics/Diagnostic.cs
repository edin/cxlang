using Cx.Compiler.Syntax;

namespace Cx.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record Diagnostic(Location Location, string Message, DiagnosticSeverity Severity)
{
    public override string ToString() =>
        $"{Location.File.Path}:{Location.Line}:{Location.Column}: {Severity.ToString().ToLowerInvariant()}: {Message}";
}
