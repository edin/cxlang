namespace Cx.Compiler.Syntax.Nodes;

public sealed record FunctionNode(
    Location Location,
    bool IsStatic,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<GenericConstraintNode> GenericConstraints,
    IReadOnlyList<ParameterNode> Parameters,
    IReadOnlyList<StatementNode> Body,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    TypeNode? ReturnTypeNode = null,
    TypeNode? OwnerTypeNode = null,
    IReadOnlyList<TypeNode>? TypeArgumentNodes = null) : TopLevelNode(Location)
{
    public FunctionNode(
        Location Location,
        bool IsStatic,
        string? OwnerType,
        string Name,
        IReadOnlyList<string> TypeParameters,
        IReadOnlyList<string> TypeArguments,
        IReadOnlyList<GenericConstraintNode> GenericConstraints,
        IReadOnlyList<ParameterNode> Parameters,
        IReadOnlyList<StatementNode> Body,
        IReadOnlyList<AttributeApplicationNode> Attributes,
        TypeNode? ReturnTypeNode = null)
        : this(
            Location,
            IsStatic,
            Name,
            TypeParameters,
            GenericConstraints,
            Parameters,
            Body,
            Attributes,
            ReturnTypeNode,
            CreateOwnerTypeNode(Location, OwnerType),
            CreateTypeArgumentNodes(Location, TypeArguments))
    {
    }

    [Obsolete("Use OwnerTypeNode instead of the string compatibility property.")]
    public string? OwnerType => OwnerTypeNode?.TypeName;

    [Obsolete("Use TypeArgumentNodes instead of the string compatibility property.")]
    public IReadOnlyList<string> TypeArguments => (TypeArgumentNodes ?? []).Select(node => node.TypeName).ToList();

    [Obsolete("Use ReturnTypeNode instead of the string compatibility property.")]
    public string ReturnType => ReturnTypeNode?.TypeName ?? string.Empty;

    private static TypeNode? CreateOwnerTypeNode(Location location, string? ownerType) =>
        string.IsNullOrWhiteSpace(ownerType)
            ? null
            : new TypeNode(location, ownerType, TypeSyntaxParser.Parse(ownerType));

    private static IReadOnlyList<TypeNode> CreateTypeArgumentNodes(Location location, IReadOnlyList<string> typeArguments) =>
        typeArguments.Select(type => new TypeNode(location, type, TypeSyntaxParser.Parse(type))).ToList();
}
