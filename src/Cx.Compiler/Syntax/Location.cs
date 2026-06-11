namespace Cx.Compiler.Syntax;

public sealed record Location(SourceFile File, int Position, int Line, int Column)
{
    public static Location Unknown { get; } = new(SourceFile.Unknown, 0, 1, 1);

    public static Location Synthetic(string name) => new(SourceFile.Synthetic(name), 0, 1, 1);
}
