using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ReturnFlowAnalyzer(ProgramNode program, ExpressionTypeResolver expressionTypeResolver)
{
    public bool StatementsAlwaysReturn(
        IReadOnlyList<StatementNode> statements,
        IReadOnlyDictionary<string, string> variables) =>
        statements.Any(statement => StatementAlwaysReturns(statement, variables));

    public bool StatementsAlwaysReturn(
        IReadOnlyList<StatementNode> statements,
        TypeEnvironment variables) =>
        statements.Any(statement => StatementAlwaysReturns(statement, variables));

    public bool StatementAlwaysReturns(
        StatementNode statement,
        IReadOnlyDictionary<string, string> variables) =>
        statement switch
        {
            ReturnStatement => true,
            IfStatement ifStatement => IfStatementAlwaysReturns(ifStatement, variables),
            ElseBlockStatement elseBlock => StatementsAlwaysReturn(elseBlock.Body, variables),
            SwitchStatement switchStatement => SwitchStatementAlwaysReturns(switchStatement, variables),
            MatchStatement matchStatement => MatchStatementAlwaysReturns(matchStatement, variables),
            _ => false,
        };

    public bool StatementAlwaysReturns(
        StatementNode statement,
        TypeEnvironment variables) =>
        statement switch
        {
            ReturnStatement => true,
            IfStatement ifStatement => IfStatementAlwaysReturns(ifStatement, variables),
            ElseBlockStatement elseBlock => StatementsAlwaysReturn(elseBlock.Body, variables),
            SwitchStatement switchStatement => SwitchStatementAlwaysReturns(switchStatement, variables),
            MatchStatement matchStatement => MatchStatementAlwaysReturns(matchStatement, variables),
            _ => false,
        };

    public bool StatementAlwaysTransfersControl(
        StatementNode statement,
        IReadOnlyDictionary<string, string> variables) =>
        statement switch
        {
            ReturnStatement or BreakStatement or ContinueStatement => true,
            IfStatement ifStatement => IfStatementAlwaysTransfersControl(ifStatement, variables),
            ElseBlockStatement elseBlock => StatementsAlwaysTransferControl(elseBlock.Body, variables),
            SwitchStatement switchStatement => SwitchStatementAlwaysReturns(switchStatement, variables),
            MatchStatement matchStatement => MatchStatementAlwaysReturns(matchStatement, variables),
            _ => false,
        };

    public bool StatementAlwaysTransfersControl(
        StatementNode statement,
        TypeEnvironment variables) =>
        statement switch
        {
            ReturnStatement or BreakStatement or ContinueStatement => true,
            IfStatement ifStatement => IfStatementAlwaysTransfersControl(ifStatement, variables),
            ElseBlockStatement elseBlock => StatementsAlwaysTransferControl(elseBlock.Body, variables),
            SwitchStatement switchStatement => SwitchStatementAlwaysReturns(switchStatement, variables),
            MatchStatement matchStatement => MatchStatementAlwaysReturns(matchStatement, variables),
            _ => false,
        };

    public bool IsMatchExhaustive(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables) =>
        IsMatchExhaustive(matchStatement, ResolveMatchedTaggedUnion(matchStatement, variables));

    public bool IsMatchExhaustive(
        MatchStatement matchStatement,
        TypeEnvironment variables) =>
        IsMatchExhaustive(matchStatement, ResolveMatchedTaggedUnion(matchStatement, variables));

    public TaggedUnionNode? ResolveMatchedTaggedUnion(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = expressionTypeResolver.ResolveTypeRef(matchStatement.Expression, variables);
        return ResolveTaggedUnion(matchExpressionType);
    }

    public TaggedUnionNode? ResolveMatchedTaggedUnion(
        MatchStatement matchStatement,
        TypeEnvironment variables)
    {
        var matchExpressionType = expressionTypeResolver.ResolveTypeRef(matchStatement.Expression, variables);
        return ResolveTaggedUnion(matchExpressionType);
    }

    private bool StatementsAlwaysTransferControl(
        IReadOnlyList<StatementNode> statements,
        IReadOnlyDictionary<string, string> variables) =>
        statements.Any(statement => StatementAlwaysTransfersControl(statement, variables));

    private bool StatementsAlwaysTransferControl(
        IReadOnlyList<StatementNode> statements,
        TypeEnvironment variables) =>
        statements.Any(statement => StatementAlwaysTransfersControl(statement, variables));

    private bool IfStatementAlwaysTransfersControl(
        IfStatement ifStatement,
        IReadOnlyDictionary<string, string> variables) =>
        StatementsAlwaysTransferControl(ifStatement.ThenBody, variables)
        && ifStatement.ElseBranch is not null
        && StatementAlwaysTransfersControl(ifStatement.ElseBranch, variables);

    private bool IfStatementAlwaysTransfersControl(
        IfStatement ifStatement,
        TypeEnvironment variables) =>
        StatementsAlwaysTransferControl(ifStatement.ThenBody, variables)
        && ifStatement.ElseBranch is not null
        && StatementAlwaysTransfersControl(ifStatement.ElseBranch, variables);

    private bool IfStatementAlwaysReturns(
        IfStatement ifStatement,
        IReadOnlyDictionary<string, string> variables) =>
        StatementsAlwaysReturn(ifStatement.ThenBody, variables)
        && ifStatement.ElseBranch is not null
        && StatementAlwaysReturns(ifStatement.ElseBranch, variables);

    private bool IfStatementAlwaysReturns(
        IfStatement ifStatement,
        TypeEnvironment variables) =>
        StatementsAlwaysReturn(ifStatement.ThenBody, variables)
        && ifStatement.ElseBranch is not null
        && StatementAlwaysReturns(ifStatement.ElseBranch, variables);

    private bool SwitchStatementAlwaysReturns(
        SwitchStatement switchStatement,
        IReadOnlyDictionary<string, string> variables) =>
        (switchStatement.DefaultBody.Count > 0 || IsSwitchExhaustive(switchStatement, variables))
        && (switchStatement.DefaultBody.Count == 0 || StatementsAlwaysReturn(switchStatement.DefaultBody, variables))
        && switchStatement.Cases.All(switchCase => StatementsAlwaysReturn(switchCase.Body, variables));

    private bool SwitchStatementAlwaysReturns(
        SwitchStatement switchStatement,
        TypeEnvironment variables) =>
        (switchStatement.DefaultBody.Count > 0 || IsSwitchExhaustive(switchStatement, variables))
        && (switchStatement.DefaultBody.Count == 0 || StatementsAlwaysReturn(switchStatement.DefaultBody, variables))
        && switchStatement.Cases.All(switchCase => StatementsAlwaysReturn(switchCase.Body, variables));

    private bool IsSwitchExhaustive(
        SwitchStatement switchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var expressionType = expressionTypeResolver.ResolveTypeRef(switchStatement.Expression, variables);
        return IsSwitchExhaustive(switchStatement, expressionType);
    }

    private bool IsSwitchExhaustive(
        SwitchStatement switchStatement,
        TypeEnvironment variables)
    {
        var expressionType = expressionTypeResolver.ResolveTypeRef(switchStatement.Expression, variables);
        return IsSwitchExhaustive(switchStatement, expressionType);
    }

    private bool IsSwitchExhaustive(SwitchStatement switchStatement, TypeRef? expressionType)
    {
        var enumType = TypeRefFacts.GetBaseName(expressionType);
        if (enumType is null)
        {
            return false;
        }

        var enumNode = program.Enums.FirstOrDefault(node =>
            string.Equals(node.Name, enumType, StringComparison.Ordinal));
        if (enumNode is null || enumNode.Members.Count == 0)
        {
            return false;
        }

        var covered = switchStatement.Cases
            .Select(switchCase => switchCase.Pattern.SourceText)
            .ToHashSet(StringComparer.Ordinal);
        return enumNode.Members.All(member => covered.Contains(member.Name));
    }

    private bool MatchStatementAlwaysReturns(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables) =>
        IsMatchExhaustive(matchStatement, variables)
        && matchStatement.Arms.All(arm => StatementsAlwaysReturn(arm.Body, variables));

    private bool MatchStatementAlwaysReturns(
        MatchStatement matchStatement,
        TypeEnvironment variables) =>
        IsMatchExhaustive(matchStatement, variables)
        && matchStatement.Arms.All(arm => StatementsAlwaysReturn(arm.Body, variables));

    private TaggedUnionNode? ResolveTaggedUnion(TypeRef? expressionType)
    {
        var normalizedType = TypeRefFacts.GetBaseName(expressionType);
        if (normalizedType is null)
        {
            return null;
        }

        return program.TaggedUnions.FirstOrDefault(union =>
            string.Equals(union.Name, normalizedType, StringComparison.Ordinal));
    }

    private static bool IsMatchExhaustive(MatchStatement matchStatement, TaggedUnionNode? taggedUnion)
    {
        if (matchStatement.Arms.Any(arm => arm.Pattern == "_"))
        {
            return true;
        }

        if (taggedUnion is null)
        {
            return false;
        }

        var covered = matchStatement.Arms
            .Select(arm => arm.Pattern)
            .ToHashSet(StringComparer.Ordinal);
        return taggedUnion.Variants.All(variant => covered.Contains(variant.Name));
    }
}
