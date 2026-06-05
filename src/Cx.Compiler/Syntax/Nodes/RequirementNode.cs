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
    string Type) : RequirementMemberNode(Location);

public sealed record RequirementFunctionNode(
    Location Location,
    bool IsStatic,
    string Name,
    string ReturnType,
    IReadOnlyList<ParameterNode> Parameters) : RequirementMemberNode(Location);
