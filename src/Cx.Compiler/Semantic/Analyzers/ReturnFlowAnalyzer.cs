using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ReturnFlowAnalyzer(ProgramNode program, ExpressionTypeResolver expressionTypeResolver)
{
    private readonly TypeRefParser _typeRefParser = new(program);

    public bool StatementsAlwaysReturn(
        IReadOnlyList<StatementNode> statements,
        IReadOnlyDictionary<string, string> variables) =>
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

    public bool IsMatchExhaustive(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables) =>
        IsMatchExhaustive(matchStatement, ResolveMatchedTaggedUnion(matchStatement, variables));

    public TaggedUnionNode? ResolveMatchedTaggedUnion(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = expressionTypeResolver.Resolve(matchStatement.Expression, variables);
        if (matchExpressionType is null)
        {
            return null;
        }

        var normalizedType = StripPointer(ResolveAlias(matchExpressionType));
        return program.TaggedUnions.FirstOrDefault(union =>
            string.Equals(union.Name, normalizedType, StringComparison.Ordinal));
    }

    private bool StatementsAlwaysTransferControl(
        IReadOnlyList<StatementNode> statements,
        IReadOnlyDictionary<string, string> variables) =>
        statements.Any(statement => StatementAlwaysTransfersControl(statement, variables));

    private bool IfStatementAlwaysTransfersControl(
        IfStatement ifStatement,
        IReadOnlyDictionary<string, string> variables) =>
        StatementsAlwaysTransferControl(ifStatement.ThenBody, variables)
        && ifStatement.ElseBranch is not null
        && StatementAlwaysTransfersControl(ifStatement.ElseBranch, variables);

    private bool IfStatementAlwaysReturns(
        IfStatement ifStatement,
        IReadOnlyDictionary<string, string> variables) =>
        StatementsAlwaysReturn(ifStatement.ThenBody, variables)
        && ifStatement.ElseBranch is not null
        && StatementAlwaysReturns(ifStatement.ElseBranch, variables);

    private bool SwitchStatementAlwaysReturns(
        SwitchStatement switchStatement,
        IReadOnlyDictionary<string, string> variables) =>
        (switchStatement.DefaultBody.Count > 0 || IsSwitchExhaustive(switchStatement, variables))
        && (switchStatement.DefaultBody.Count == 0 || StatementsAlwaysReturn(switchStatement.DefaultBody, variables))
        && switchStatement.Cases.All(switchCase => StatementsAlwaysReturn(switchCase.Body, variables));

    private bool IsSwitchExhaustive(
        SwitchStatement switchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var expressionType = expressionTypeResolver.Resolve(switchStatement.Expression, variables);
        if (expressionType is null)
        {
            return false;
        }

        var enumType = StripPointer(ResolveAlias(expressionType));
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

    private string ResolveAlias(string type)
    {
        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var aliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TypeText(group.First().TargetTypeNode), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(type, out var targetType) && seen.Add(type))
        {
            type = targetType;
        }

        return type + pointerSuffix;
    }

    private static string StripPointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    private string TypeText(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        var type = typeNode.ToTypeRef(_typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }
}
