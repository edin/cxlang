namespace Cx.Compiler.Syntax.Nodes;

public sealed record RequirementNode(
    Location Location,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<GenericConstraintNode> GenericConstraints,
    IReadOnlyList<RequirementMemberNode> Members) : TopLevelNode(Location);

public abstract record RequirementMemberNode(Location Location) : SyntaxNode(Location);

public sealed record RequirementFieldNode(
    Location Location,
    string Name,
    TypeNode? TypeNode = null) : RequirementMemberNode(Location)
{
    [Obsolete("Use TypeNode instead of the string compatibility property.")]
    public string Type => TypeNode?.TypeName ?? string.Empty;
}

public sealed record RequirementFunctionNode(
    Location Location,
    bool IsStatic,
    string Name,
    IReadOnlyList<ParameterNode> Parameters,
    TypeNode? ReturnTypeNode = null) : RequirementMemberNode(Location)
{
    [Obsolete("Use ReturnTypeNode instead of the string compatibility property.")]
    public string ReturnType => ReturnTypeNode?.TypeName ?? string.Empty;
}
