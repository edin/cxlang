using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class DefiniteAssignmentAnalyzer(
    DiagnosticBag diagnostics,
    ProgramNode program,
    ExpressionTypeResolver expressionTypeResolver,
    ReturnFlowAnalyzer returnFlow)
{
    public void AnalyzeFunction(
        FunctionNode function,
        IReadOnlyDictionary<string, string> globalVariables)
    {
        var variables = new Dictionary<string, string>(globalVariables, StringComparer.Ordinal);
        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            variables[parameter.Name] = parameter.Type;
        }

        foreach (var local in CollectLocalVariables(function.Body))
        {
            variables[local.Name] = local.Type;
        }

        var assigned = new HashSet<string>(globalVariables.Keys, StringComparer.Ordinal);
        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            assigned.Add(parameter.Name);
        }

        AnalyzeStatements(function.Body, variables, assigned);
    }

    private void AnalyzeStatements(
        IReadOnlyList<StatementNode> statements,
        Dictionary<string, string> variables,
        HashSet<string> assigned)
    {
        var unreachable = false;
        foreach (var statement in statements)
        {
            if (unreachable)
            {
                diagnostics.Warn(statement.Location, "Unreachable code.");
            }

            AnalyzeStatement(statement, variables, assigned);
            if (returnFlow.StatementAlwaysTransfersControl(statement, variables))
            {
                unreachable = true;
            }
        }
    }

    private void AnalyzeStatement(
        StatementNode statement,
        Dictionary<string, string> variables,
        HashSet<string> assigned)
    {
        switch (statement)
        {
            case LetStatement let:
                AnalyzeExpression(let.Initializer, variables, assigned);
                variables[let.Name] = let.Type;
                if (let.Initializer is null)
                {
                    assigned.Remove(let.Name);
                }
                else
                {
                    assigned.Add(let.Name);
                }

                break;

            case ReturnStatement ret:
                AnalyzeExpression(ret.Expression, variables, assigned);
                break;

            case CStatement c:
                AnalyzeExpression(c.Expression, variables, assigned);
                break;

            case IfStatement ifStatement:
                AnalyzeExpression(ifStatement.Condition, variables, assigned);
                var beforeIf = new HashSet<string>(assigned, StringComparer.Ordinal);
                var thenAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                AnalyzeStatements(ifStatement.ThenBody, new Dictionary<string, string>(variables, StringComparer.Ordinal), thenAssigned);
                if (ifStatement.ElseBranch is not null)
                {
                    var elseAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    AnalyzeStatement(ifStatement.ElseBranch, new Dictionary<string, string>(variables, StringComparer.Ordinal), elseAssigned);
                    ReplaceWithIntersection(assigned, thenAssigned, elseAssigned);
                }
                else
                {
                    ReplaceWith(assigned, beforeIf);
                }

                break;

            case ElseBlockStatement elseBlock:
                AnalyzeStatements(elseBlock.Body, new Dictionary<string, string>(variables, StringComparer.Ordinal), assigned);
                break;

            case WhileStatement whileStatement:
                AnalyzeExpression(whileStatement.Condition, variables, assigned);
                AnalyzeStatements(
                    whileStatement.Body,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    new HashSet<string>(assigned, StringComparer.Ordinal));
                break;

            case ForStatement forStatement:
                var forVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                var forAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                AnalyzeForInitializer(forStatement.Initializer, forVariables, forAssigned);
                foreach (var name in variables.Keys.Where(forAssigned.Contains))
                {
                    assigned.Add(name);
                }

                AnalyzeExpression(forStatement.Condition, forVariables, forAssigned);
                AnalyzeExpression(forStatement.Increment, forVariables, forAssigned);
                AnalyzeStatements(
                    forStatement.Body,
                    forVariables,
                    new HashSet<string>(forAssigned, StringComparer.Ordinal));
                break;

            case ForeachStatement foreachStatement:
                AnalyzeExpression(foreachStatement.IterableExpression, variables, assigned);
                var foreachVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                var foreachAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                foreach (var binding in GetForeachBindings(foreachStatement))
                {
                    foreachVariables[binding.Name] = "any";
                    foreachAssigned.Add(binding.Name);
                }

                AnalyzeStatements(foreachStatement.Body, foreachVariables, foreachAssigned);
                break;

            case SwitchStatement switchStatement:
                AnalyzeExpression(switchStatement.Expression, variables, assigned);
                var switchAssignments = new List<HashSet<string>>();
                foreach (var switchCase in switchStatement.Cases)
                {
                    AnalyzeExpression(switchCase.Pattern, variables, assigned);
                    var caseAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    AnalyzeStatements(switchCase.Body, new Dictionary<string, string>(variables, StringComparer.Ordinal), caseAssigned);
                    switchAssignments.Add(caseAssigned);
                }

                if (switchStatement.DefaultBody.Count > 0)
                {
                    var defaultAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    AnalyzeStatements(switchStatement.DefaultBody, new Dictionary<string, string>(variables, StringComparer.Ordinal), defaultAssigned);
                    switchAssignments.Add(defaultAssigned);
                    ReplaceWithIntersection(assigned, switchAssignments);
                }
                else if (IsSwitchExhaustive(switchStatement, variables))
                {
                    ReplaceWithIntersection(assigned, switchAssignments);
                }

                break;

            case MatchStatement matchStatement:
                AnalyzeExpression(matchStatement.Expression, variables, assigned);
                var matchedTaggedUnion = returnFlow.ResolveMatchedTaggedUnion(matchStatement, variables);
                var armAssignments = new List<HashSet<string>>();
                foreach (var arm in matchStatement.Arms)
                {
                    var armVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                    var armAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    if (matchedTaggedUnion is not null
                        && arm.BindingName is not null
                        && arm.Pattern != "_"
                        && matchedTaggedUnion.Variants.FirstOrDefault(variant => variant.Name == arm.Pattern) is { } variant)
                    {
                        armVariables[arm.BindingName] = variant.Type;
                        armAssigned.Add(arm.BindingName);
                    }

                    AnalyzeStatements(arm.Body, armVariables, armAssigned);
                    armAssignments.Add(armAssigned);
                }

                if (returnFlow.IsMatchExhaustive(matchStatement, variables))
                {
                    ReplaceWithIntersection(assigned, armAssignments);
                }

                break;
        }
    }

    private void AnalyzeForInitializer(
        ForInitializerNode initializer,
        Dictionary<string, string> variables,
        HashSet<string> assigned)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                AnalyzeExpression(declaration.Initializer, variables, assigned);
                variables[declaration.Name] = declaration.Type;
                if (declaration.Initializer is null)
                {
                    assigned.Remove(declaration.Name);
                }
                else
                {
                    assigned.Add(declaration.Name);
                }

                break;

            case ForExpressionInitializerNode expression:
                AnalyzeExpression(expression.Expression, variables, assigned);
                break;
        }
    }

    private void AnalyzeExpression(
        ExpressionNode? expression,
        IReadOnlyDictionary<string, string> variables,
        HashSet<string> assigned)
    {
        if (expression is null)
        {
            return;
        }

        switch (expression)
        {
            case NameExpressionNode name:
                AnalyzeNameExpression(name, variables, assigned);
                break;
            case ParenthesizedExpressionNode parenthesized:
                AnalyzeExpression(parenthesized.Expression, variables, assigned);
                break;
            case CastExpressionNode cast:
                AnalyzeExpression(cast.Expression, variables, assigned);
                break;
            case UnaryExpressionNode unary:
                AnalyzeExpression(unary.Operand, variables, assigned);
                break;
            case PostfixExpressionNode postfix:
                AnalyzeExpression(postfix.Operand, variables, assigned);
                break;
            case SizeOfExpressionNode sizeOf:
                AnalyzeExpression(sizeOf.ExpressionOperand, variables, assigned);
                break;
            case BinaryExpressionNode binary:
                AnalyzeExpression(binary.Left, variables, assigned);
                AnalyzeExpression(binary.Right, variables, assigned);
                break;
            case ScalarRangeExpressionNode range:
                AnalyzeExpression(range.Start, variables, assigned);
                AnalyzeExpression(range.End, variables, assigned);
                break;
            case ConditionalExpressionNode conditional:
                AnalyzeExpression(conditional.Condition, variables, assigned);
                AnalyzeExpression(conditional.WhenTrue, variables, assigned);
                AnalyzeExpression(conditional.WhenFalse, variables, assigned);
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    AnalyzeExpression(field.Value, variables, assigned);
                }

                foreach (var value in initializer.Values)
                {
                    AnalyzeExpression(value, variables, assigned);
                }

                break;
            case FunctionExpressionNode functionExpression:
                AnalyzeExpression(functionExpression.ExpressionBody, variables, assigned);
                break;
            case AssignmentExpressionNode assignment:
                AnalyzeExpression(assignment.Value, variables, assigned);
                AnalyzeAssignmentTarget(assignment.Target, variables, assigned);
                break;
            case CallExpressionNode call:
                AnalyzeExpression(call.Callee, variables, assigned);
                foreach (var argument in call.Arguments)
                {
                    AnalyzeCallArgument(argument, variables, assigned);
                }

                break;
            case GenericCallExpressionNode call:
                AnalyzeExpression(call.Callee, variables, assigned);
                foreach (var argument in call.Arguments)
                {
                    AnalyzeCallArgument(argument, variables, assigned);
                }

                break;
            case MemberExpressionNode member:
                AnalyzeExpression(member.Target, variables, assigned);
                break;
            case IndexExpressionNode index:
                AnalyzeExpression(index.Target, variables, assigned);
                AnalyzeExpression(index.Index, variables, assigned);
                break;
        }
    }

    private void AnalyzeCallArgument(
        ExpressionNode argument,
        IReadOnlyDictionary<string, string> variables,
        HashSet<string> assigned)
    {
        if (argument is UnaryExpressionNode { Operator: "&" } addressOf
            && TryGetAssignmentRootName(addressOf.Operand, out var rootName))
        {
            assigned.Add(rootName);
            AnalyzeAddressOfTarget(addressOf.Operand, variables, assigned);
            return;
        }

        AnalyzeExpression(argument, variables, assigned);
    }

    private void AnalyzeAddressOfTarget(
        ExpressionNode target,
        IReadOnlyDictionary<string, string> variables,
        HashSet<string> assigned)
    {
        switch (target)
        {
            case NameExpressionNode:
                break;
            case MemberExpressionNode member:
                AnalyzeAddressOfTarget(member.Target, variables, assigned);
                break;
            case IndexExpressionNode index:
                AnalyzeAddressOfTarget(index.Target, variables, assigned);
                AnalyzeExpression(index.Index, variables, assigned);
                break;
            case ParenthesizedExpressionNode parenthesized:
                AnalyzeAddressOfTarget(parenthesized.Expression, variables, assigned);
                break;
            default:
                AnalyzeExpression(target, variables, assigned);
                break;
        }
    }

    private void AnalyzeAssignmentTarget(
        ExpressionNode target,
        IReadOnlyDictionary<string, string> variables,
        HashSet<string> assigned)
    {
        switch (target)
        {
            case NameExpressionNode name:
                assigned.Add(name.SourceText);
                break;

            case MemberExpressionNode member:
                if (TryGetAssignmentRootName(member, out var memberRoot))
                {
                    assigned.Add(memberRoot);
                }
                else
                {
                    AnalyzeExpression(member.Target, variables, assigned);
                }

                break;

            case IndexExpressionNode index:
                if (TryGetAssignmentRootName(index.Target, out var indexRoot))
                {
                    assigned.Add(indexRoot);
                }
                else
                {
                    AnalyzeExpression(index.Target, variables, assigned);
                }

                AnalyzeExpression(index.Index, variables, assigned);
                break;

            case UnaryExpressionNode { Operator: "*" } unary:
                AnalyzeExpression(unary.Operand, variables, assigned);
                break;

            case ParenthesizedExpressionNode parenthesized:
                AnalyzeAssignmentTarget(parenthesized.Expression, variables, assigned);
                break;

            default:
                AnalyzeExpression(target, variables, assigned);
                break;
        }
    }

    private void AnalyzeNameExpression(
        NameExpressionNode name,
        IReadOnlyDictionary<string, string> variables,
        HashSet<string> assigned)
    {
        if (variables.ContainsKey(name.SourceText) && !assigned.Contains(name.SourceText))
        {
            diagnostics.Report(name.Location, $"Local '{name.SourceText}' may be used before it is assigned.");
        }
    }

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

    private static IEnumerable<(string Name, string Type)> CollectLocalVariables(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return (let.Name, let.Type);
                    break;
                case IfStatement ifStatement:
                    foreach (var variable in CollectLocalVariables(ifStatement.ThenBody))
                    {
                        yield return variable;
                    }

                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var variable in CollectLocalVariables([ifStatement.ElseBranch]))
                        {
                            yield return variable;
                        }
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var variable in CollectLocalVariables(elseBlock.Body))
                    {
                        yield return variable;
                    }
                    break;
                case WhileStatement whileStatement:
                    foreach (var variable in CollectLocalVariables(whileStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForStatement forStatement:
                    if (forStatement.Initializer is ForDeclarationInitializerNode declaration)
                    {
                        yield return (declaration.Name, declaration.Type);
                    }

                    foreach (var variable in CollectLocalVariables(forStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    foreach (var binding in GetForeachBindings(foreachStatement))
                    {
                        yield return (binding.Name, binding.Type);
                    }

                    foreach (var variable in CollectLocalVariables(foreachStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var variable in CollectLocalVariables(switchCase.Body))
                        {
                            yield return variable;
                        }
                    }

                    foreach (var variable in CollectLocalVariables(switchStatement.DefaultBody))
                    {
                        yield return variable;
                    }
                    break;
                case MatchStatement matchStatement:
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var variable in CollectLocalVariables(arm.Body))
                        {
                            yield return variable;
                        }
                    }
                    break;
            }
        }
    }

    private static bool TryGetAssignmentRootName(ExpressionNode expression, out string name)
    {
        switch (expression)
        {
            case NameExpressionNode root:
                name = root.SourceText;
                return true;
            case MemberExpressionNode member:
                return TryGetAssignmentRootName(member.Target, out name);
            case IndexExpressionNode index:
                return TryGetAssignmentRootName(index.Target, out name);
            case ParenthesizedExpressionNode parenthesized:
                return TryGetAssignmentRootName(parenthesized.Expression, out name);
            default:
                name = string.Empty;
                return false;
        }
    }

    private static void ReplaceWith(HashSet<string> target, HashSet<string> source)
    {
        target.Clear();
        target.UnionWith(source);
    }

    private static void ReplaceWithIntersection(
        HashSet<string> target,
        HashSet<string> left,
        HashSet<string> right)
    {
        target.Clear();
        target.UnionWith(left);
        target.IntersectWith(right);
    }

    private static void ReplaceWithIntersection(
        HashSet<string> target,
        IReadOnlyList<HashSet<string>> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        target.Clear();
        target.UnionWith(sources[0]);
        foreach (var source in sources.Skip(1))
        {
            target.IntersectWith(source);
        }
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
            .ToDictionary(group => group.Key, group => group.First().TargetType, StringComparer.Ordinal);
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

    private static IEnumerable<ForeachBinding> GetForeachBindings(ForeachStatement foreachStatement)
    {
        if (foreachStatement.IndexBinding is not null)
        {
            yield return foreachStatement.IndexBinding;
        }

        if (foreachStatement.KeyBinding is not null)
        {
            yield return foreachStatement.KeyBinding;
        }

        yield return foreachStatement.ValueBinding;
    }
}
