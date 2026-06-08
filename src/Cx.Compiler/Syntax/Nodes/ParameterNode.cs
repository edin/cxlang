namespace Cx.Compiler.Syntax.Nodes;

public sealed record ParameterNode(
    Location Location,
    string Name,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsVariadic = false,
    TypeNode? TypeNode = null) : SyntaxNode(Location)
{
    [Obsolete("Use TypeNode instead of the string compatibility property.")]
    public string Type => TypeNode?.TypeName ?? string.Empty;
}
