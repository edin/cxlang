using Cx.Compiler.Syntax;

namespace Cx.Compiler.Diagnostics;

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public void Report(Location location, string message) =>
        _diagnostics.Add(new Diagnostic(location, message, DiagnosticSeverity.Error));

    public void Warn(Location location, string message) =>
        _diagnostics.Add(new Diagnostic(location, message, DiagnosticSeverity.Warning));
}
