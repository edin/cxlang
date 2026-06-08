namespace Cx.Compiler.Syntax.Nodes;

public abstract record ExpressionNode(Location Location, string SourceText) : SyntaxNode(Location);

public sealed record RawExpressionNode(
    Location Location,
    string SourceText) : ExpressionNode(Location, SourceText);

public sealed record LiteralExpressionNode(
    Location Location,
    string SourceText) : ExpressionNode(Location, SourceText);

public sealed record NameExpressionNode(
    Location Location,
    string SourceText) : ExpressionNode(Location, SourceText);

public sealed record ParenthesizedExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Expression) : ExpressionNode(Location, SourceText);

public sealed record CastExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Expression,
    TypeNode? TargetTypeNode = null) : ExpressionNode(Location, SourceText)
{
    [Obsolete("Use TargetTypeNode instead of the string compatibility property.")]
    public string TargetType => TargetTypeNode?.TypeName ?? string.Empty;
}

public sealed record UnaryExpressionNode(
    Location Location,
    string SourceText,
    string Operator,
    ExpressionNode Operand) : ExpressionNode(Location, SourceText);

public sealed record PostfixExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Operand,
    string Operator) : ExpressionNode(Location, SourceText);

public sealed record SizeOfExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode? ExpressionOperand,
    TypeNode? TypeOperandNode = null) : ExpressionNode(Location, SourceText)
{
    [Obsolete("Use TypeOperandNode instead of the string compatibility property.")]
    public string? TypeOperand => TypeOperandNode?.TypeName;
}

public sealed record BinaryExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Left,
    string Operator,
    ExpressionNode Right) : ExpressionNode(Location, SourceText);

public sealed record ConditionalExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Condition,
    ExpressionNode WhenTrue,
    ExpressionNode WhenFalse) : ExpressionNode(Location, SourceText);

public sealed record ScalarRangeExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Start,
    ExpressionNode End,
    bool IsInclusive) : ExpressionNode(Location, SourceText);

public sealed record InitializerExpressionNode(
    Location Location,
    string SourceText,
    IReadOnlyList<InitializerFieldNode> Fields,
    IReadOnlyList<ExpressionNode> Values,
    TypeNode? TypeNameNode = null) : ExpressionNode(Location, SourceText)
{
    [Obsolete("Use TypeNameNode instead of the string compatibility property.")]
    public string? TypeName => TypeNameNode?.TypeName;
}

public sealed record InitializerFieldNode(
    string Name,
    ExpressionNode Value);

public sealed record FunctionExpressionNode(
    Location Location,
    string SourceText,
    IReadOnlyList<ParameterNode> Parameters,
    ExpressionNode? ExpressionBody,
    IReadOnlyList<StatementNode>? BlockBody,
    TypeNode? ReturnTypeNode = null) : ExpressionNode(Location, SourceText)
{
    [Obsolete("Use ReturnTypeNode instead of the string compatibility property.")]
    public string? ReturnType => ReturnTypeNode?.TypeName;
}

public sealed record AssignmentExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Target,
    string Operator,
    ExpressionNode Value) : ExpressionNode(Location, SourceText);

public sealed record CallExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Callee,
    IReadOnlyList<ExpressionNode> Arguments) : ExpressionNode(Location, SourceText);

public sealed record GenericCallExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Callee,
    IReadOnlyList<ExpressionNode> Arguments,
    IReadOnlyList<TypeNode> TypeArgumentNodes) : ExpressionNode(Location, SourceText)
{
    [Obsolete("Use TypeArgumentNodes instead of the string compatibility property.")]
    public IReadOnlyList<string> TypeArguments => TypeArgumentNodes.Select(node => node.TypeName).ToList();

    public GenericCallExpressionNode(
        Location Location,
        string SourceText,
        ExpressionNode Callee,
        IReadOnlyList<string> TypeArguments,
        IReadOnlyList<ExpressionNode> Arguments)
        : this(
            Location,
            SourceText,
            Callee,
            Arguments,
            TypeArguments.Select(type => new TypeNode(Location, type, TypeSyntaxParser.Parse(type))).ToList())
    {
    }
}

public sealed record MemberExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Target,
    string MemberName) : ExpressionNode(Location, SourceText);

public sealed record IndexExpressionNode(
    Location Location,
    string SourceText,
    ExpressionNode Target,
    ExpressionNode Index) : ExpressionNode(Location, SourceText);
