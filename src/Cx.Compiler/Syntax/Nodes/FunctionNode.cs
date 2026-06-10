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

    private static TypeNode? CreateOwnerTypeNode(Location location, string? ownerType) =>
        string.IsNullOrWhiteSpace(ownerType)
            ? null
            : TypeNode.CreateFromText(location, ownerType);

    private static IReadOnlyList<TypeNode> CreateTypeArgumentNodes(Location location, IReadOnlyList<string> typeArguments) =>
        typeArguments.Select(type => TypeNode.CreateFromText(location, type)).ToList();
}
