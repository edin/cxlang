namespace Cx.Compiler.Syntax;

public sealed record SourceFile(string Path, string Text)
{
    public static SourceFile Unknown { get; } = new("<unknown>", string.Empty);

    public static SourceFile Synthetic(string name) => new(name, string.Empty);
}
