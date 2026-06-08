namespace Cx.Compiler.Syntax.Nodes;

public sealed record TypeAliasNode(
    Location Location,
    string Name,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false,
    TypeNode? TargetTypeNode = null) : TopLevelNode(Location)
{
    [Obsolete("Use TargetTypeNode instead of the string compatibility property.")]
    public string TargetType => TargetTypeNode?.TypeName ?? string.Empty;
}
