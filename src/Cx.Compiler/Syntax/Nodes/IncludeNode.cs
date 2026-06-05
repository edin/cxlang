namespace Cx.Compiler.Syntax.Nodes;

public sealed record IncludeNode(
    Location Location,
    string Path,
    bool IsSystem) : TopLevelNode(Location);
