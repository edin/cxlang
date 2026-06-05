namespace Cx.Compiler.Syntax.Nodes;

public sealed record ModuleDeclarationNode(
    Location Location,
    string Name) : TopLevelNode(Location);
