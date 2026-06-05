namespace Cx.Compiler.Syntax.Nodes;

public sealed record EnumNode(
    Location Location,
    string Name,
    IReadOnlyList<EnumMemberNode> Members,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false) : TopLevelNode(Location);

public sealed record EnumMemberNode(
    Location Location,
    string Name,
    string? Value,
    IReadOnlyList<AttributeApplicationNode> Attributes) : SyntaxNode(Location);
