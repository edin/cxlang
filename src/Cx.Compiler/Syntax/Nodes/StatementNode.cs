namespace Cx.Compiler.Syntax.Nodes;

public abstract record StatementNode(Location Location) : SyntaxNode(Location);

public sealed record LetStatement(
    Location Location,
    bool IsConst,
    string Name,
    ExpressionNode? Initializer,
    TypeNode? TypeNode = null) : StatementNode(Location);

public sealed record ReturnStatement(
    Location Location,
    ExpressionNode? Expression) : StatementNode(Location);

public sealed record BreakStatement(
    Location Location) : StatementNode(Location);

public sealed record ContinueStatement(
    Location Location) : StatementNode(Location);

public sealed record IfStatement(
    Location Location,
    ExpressionNode Condition,
    IReadOnlyList<StatementNode> ThenBody,
    StatementNode? ElseBranch) : StatementNode(Location);

public sealed record ElseBlockStatement(
    Location Location,
    IReadOnlyList<StatementNode> Body) : StatementNode(Location);

public sealed record WhileStatement(
    Location Location,
    ExpressionNode Condition,
    IReadOnlyList<StatementNode> Body) : StatementNode(Location);

public sealed record ForStatement(
    Location Location,
    ForInitializerNode Initializer,
    ExpressionNode Condition,
    ExpressionNode Increment,
    IReadOnlyList<StatementNode> Body) : StatementNode(Location);

public abstract record ForInitializerNode(Location Location) : SyntaxNode(Location);

public sealed record ForDeclarationInitializerNode(
    Location Location,
    bool IsConst,
    string Name,
    ExpressionNode? Initializer,
    TypeNode? TypeNode = null) : ForInitializerNode(Location);

public sealed record ForExpressionInitializerNode(
    Location Location,
    ExpressionNode Expression) : ForInitializerNode(Location);

public sealed record ForeachBinding(
    Location Location,
    string Name,
    bool IsReference,
    bool IsConst,
    TypeNode? TypeNode = null) : SyntaxNode(Location);

public sealed record ForeachStatement(
    Location Location,
    ForeachBinding? IndexBinding,
    ForeachBinding? KeyBinding,
    ForeachBinding ValueBinding,
    ExpressionNode IterableExpression,
    IReadOnlyList<StatementNode> Body) : StatementNode(Location)
{
    public string ItemName => ValueBinding.Name;
}

public sealed record SwitchStatement(
    Location Location,
    ExpressionNode Expression,
    IReadOnlyList<SwitchCaseNode> Cases,
    IReadOnlyList<StatementNode> DefaultBody) : StatementNode(Location);

public sealed record SwitchCaseNode(
    Location Location,
    ExpressionNode Pattern,
    IReadOnlyList<StatementNode> Body) : SyntaxNode(Location);

public sealed record MatchStatement(
    Location Location,
    ExpressionNode Expression,
    IReadOnlyList<MatchArmNode> Arms) : StatementNode(Location);

public sealed record MatchArmNode(
    Location Location,
    string Pattern,
    string? BindingName,
    IReadOnlyList<StatementNode> Body) : SyntaxNode(Location);

public sealed record CStatement(
    Location Location,
    ExpressionNode Expression) : StatementNode(Location);
