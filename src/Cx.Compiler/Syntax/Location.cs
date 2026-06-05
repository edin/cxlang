namespace Cx.Compiler.Syntax;

public sealed record Location(SourceFile File, int Position, int Line, int Column);
