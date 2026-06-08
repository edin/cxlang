namespace Cx.Compiler.Syntax.Nodes;

public sealed record TaggedUnionNode(
    Location Location,
    string Name,
    IReadOnlyList<TaggedUnionVariantNode> Variants,
    IReadOnlyList<FunctionNode> Methods,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsRaw = false,
    bool IsHeaderDeclaration = false) : TopLevelNode(Location);

public sealed record TaggedUnionVariantNode(
    Location Location,
    string Name,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    TypeNode? TypeNode = null) : SyntaxNode(Location)
{
    [Obsolete("Use TypeNode instead of the string compatibility property.")]
    public string Type => TypeNode?.TypeName ?? string.Empty;
}
