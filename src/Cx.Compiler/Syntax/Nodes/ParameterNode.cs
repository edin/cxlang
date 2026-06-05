namespace Cx.Compiler.Syntax.Nodes;

public sealed record ParameterNode(
    Location Location,
    string Name,
    string Type,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsVariadic = false) : SyntaxNode(Location);
