namespace Cx.Compiler.Syntax.Nodes;

public sealed record FunctionNode(
    Location Location,
    bool IsStatic,
    string? OwnerType,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<string> TypeArguments,
    IReadOnlyList<GenericConstraintNode> GenericConstraints,
    string ReturnType,
    IReadOnlyList<ParameterNode> Parameters,
    IReadOnlyList<StatementNode> Body,
    IReadOnlyList<AttributeApplicationNode> Attributes) : TopLevelNode(Location);
