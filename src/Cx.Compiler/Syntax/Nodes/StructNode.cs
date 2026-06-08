namespace Cx.Compiler.Syntax.Nodes;

public sealed record StructNode(
    Location Location,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<GenericConstraintNode> GenericConstraints,
    IReadOnlyList<StructRequirementNode> Requirements,
    IReadOnlyList<StructFieldNode> Fields,
    IReadOnlyList<FunctionNode> Methods,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false) : TopLevelNode(Location);

public sealed record GenericConstraintNode(
    Location Location,
    string TypeParameter,
    IReadOnlyList<StructRequirementNode> Requirements) : SyntaxNode(Location);

public sealed record StructFieldNode(
    Location Location,
    string Name,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    TypeNode? TypeNode = null) : SyntaxNode(Location)
{
    [Obsolete("Use TypeNode instead of the string compatibility property.")]
    public string Type => TypeNode?.TypeName ?? string.Empty;
}

public sealed record StructRequirementNode(
    Location Location,
    string Name,
    IReadOnlyList<TypeNode> TypeArgumentNodes) : SyntaxNode(Location)
{
    [Obsolete("Use TypeArgumentNodes instead of the string compatibility property.")]
    public IReadOnlyList<string> TypeArguments => TypeArgumentNodes.Select(node => node.TypeName).ToList();

    public StructRequirementNode(
        Location Location,
        string Name,
        IReadOnlyList<string> TypeArguments)
        : this(Location, Name, TypeArguments.Select(type => new TypeNode(Location, type, TypeSyntaxParser.Parse(type))).ToList())
    {
    }
}
