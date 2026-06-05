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
    string TargetType,
    ExpressionNode Expression) : ExpressionNode(Location, SourceText);

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
    string? TypeOperand,
    ExpressionNode? ExpressionOperand) : ExpressionNode(Location, SourceText);

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
    string? TypeName,
    IReadOnlyList<InitializerFieldNode> Fields,
    IReadOnlyList<ExpressionNode> Values) : ExpressionNode(Location, SourceText);

public sealed record InitializerFieldNode(
    string Name,
    ExpressionNode Value);

public sealed record FunctionExpressionNode(
    Location Location,
    string SourceText,
    IReadOnlyList<ParameterNode> Parameters,
    string? ReturnType,
    ExpressionNode? ExpressionBody,
    IReadOnlyList<StatementNode>? BlockBody) : ExpressionNode(Location, SourceText);

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
    IReadOnlyList<string> TypeArguments,
    IReadOnlyList<ExpressionNode> Arguments) : ExpressionNode(Location, SourceText);

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
