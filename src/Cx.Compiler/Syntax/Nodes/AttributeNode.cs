namespace Cx.Compiler.Syntax.Nodes;

public sealed record AttributeDeclarationNode(
    Location Location,
    string Name,
    IReadOnlyList<string> Targets,
    IReadOnlyList<AttributeFieldNode> Fields) : TopLevelNode(Location);

public sealed record AttributeFieldNode(
    Location Location,
    string Name,
    string Type) : SyntaxNode(Location);

public sealed record AttributeApplicationNode(
    Location Location,
    string Name,
    IReadOnlyList<AttributeArgumentNode> Arguments) : SyntaxNode(Location);

public sealed record AttributeArgumentNode(
    Location Location,
    string? Name,
    string Value) : SyntaxNode(Location);
