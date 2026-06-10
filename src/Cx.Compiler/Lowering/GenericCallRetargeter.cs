using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class GenericCallRetargeter
{
    public static void Retarget(
        ProgramNode program,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        Retarget(EnumerateExpressions(program), specializations);
    }

    public static void Retarget(
        IEnumerable<FunctionNode> functions,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        Retarget(
            functions.SelectMany(function => EnumerateExpressions(function.Body)),
            specializations);
    }

    private static void Retarget(
        IEnumerable<ExpressionNode> expressions,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        foreach (var expression in expressions)
        {
            RetargetResolvedGenericCall(expression, specializations);
        }
    }

    private static void RetargetResolvedGenericCall(
        ExpressionNode expression,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        if (expression.Semantic.ResolvedCall is not { Function.TypeParameters.Count: > 0 } resolved
            || resolved.TypeArguments.Count != resolved.Function.TypeParameters.Count
            || !specializations.TryGetValue(Key(resolved.Function, resolved.TypeArguments), out var specialized))
        {
            return;
        }

        GenericFunctionSpecializer.EnsureFunctionSymbol(specialized);
        expression.Semantic.Symbol = specialized.Semantic.Symbol;
        expression.Semantic.ResolvedCall = new ResolvedCallInfo(
            specialized,
            resolved.TypeArguments,
            resolved.IsInstance);

        if (expression is CallExpressionNode { Callee: MemberExpressionNode member })
        {
            member.Semantic.Symbol = expression.Semantic.Symbol;
            member.Semantic.ResolvedCall = expression.Semantic.ResolvedCall;
        }
        else if (expression is GenericCallExpressionNode { Callee: MemberExpressionNode genericMember })
        {
            genericMember.Semantic.Symbol = expression.Semantic.Symbol;
            genericMember.Semantic.ResolvedCall = expression.Semantic.ResolvedCall;
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateExpressions(ProgramNode program)
    {
        foreach (var global in program.GlobalVariables.Where(global => global.Initializer is not null))
        {
            foreach (var expression in EnumerateExpressions(global.Initializer!))
            {
                yield return expression;
            }
        }

        foreach (var function in program.Functions)
        {
            foreach (var expression in EnumerateExpressions(function.Body))
            {
                yield return expression;
            }
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateExpressions(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement { Initializer: not null } let:
                    foreach (var expression in EnumerateExpressions(let.Initializer)) yield return expression;
                    break;
                case ReturnStatement { Expression: not null } ret:
                    foreach (var expression in EnumerateExpressions(ret.Expression)) yield return expression;
                    break;
                case CStatement c:
                    foreach (var expression in EnumerateExpressions(c.Expression)) yield return expression;
                    break;
                case IfStatement ifStatement:
                    foreach (var expression in EnumerateExpressions(ifStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(ifStatement.ThenBody)) yield return expression;
                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var expression in EnumerateExpressions([ifStatement.ElseBranch])) yield return expression;
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var expression in EnumerateExpressions(elseBlock.Body)) yield return expression;
                    break;
                case WhileStatement whileStatement:
                    foreach (var expression in EnumerateExpressions(whileStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(whileStatement.Body)) yield return expression;
                    break;
                case ForStatement forStatement:
                    foreach (var expression in EnumerateForInitializerExpressions(forStatement.Initializer)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Increment)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Body)) yield return expression;
                    break;
                case ForeachStatement foreachStatement:
                    foreach (var expression in EnumerateExpressions(foreachStatement.IterableExpression)) yield return expression;
                    foreach (var expression in EnumerateExpressions(foreachStatement.Body)) yield return expression;
                    break;
                case SwitchStatement switchStatement:
                    foreach (var expression in EnumerateExpressions(switchStatement.Expression)) yield return expression;
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var expression in EnumerateExpressions(switchCase.Pattern)) yield return expression;
                        foreach (var expression in EnumerateExpressions(switchCase.Body)) yield return expression;
                    }
                    foreach (var expression in EnumerateExpressions(switchStatement.DefaultBody)) yield return expression;
                    break;
                case MatchStatement matchStatement:
                    foreach (var expression in EnumerateExpressions(matchStatement.Expression)) yield return expression;
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var expression in EnumerateExpressions(arm.Body)) yield return expression;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateForInitializerExpressions(ForInitializerNode initializer) => initializer switch
    {
        ForDeclarationInitializerNode { Initializer: not null } declaration => EnumerateExpressions(declaration.Initializer),
        ForExpressionInitializerNode expression => EnumerateExpressions(expression.Expression),
        _ => [],
    };

    private static IEnumerable<ExpressionNode> EnumerateExpressions(ExpressionNode expression)
    {
        yield return expression;
        switch (expression)
        {
            case ParenthesizedExpressionNode parenthesized:
                foreach (var child in EnumerateExpressions(parenthesized.Expression)) yield return child;
                break;
            case CastExpressionNode cast:
                foreach (var child in EnumerateExpressions(cast.Expression)) yield return child;
                break;
            case UnaryExpressionNode unary:
                foreach (var child in EnumerateExpressions(unary.Operand)) yield return child;
                break;
            case PostfixExpressionNode postfix:
                foreach (var child in EnumerateExpressions(postfix.Operand)) yield return child;
                break;
            case SizeOfExpressionNode { ExpressionOperand: not null } sizeOf:
                foreach (var child in EnumerateExpressions(sizeOf.ExpressionOperand)) yield return child;
                break;
            case BinaryExpressionNode binary:
                foreach (var child in EnumerateExpressions(binary.Left)) yield return child;
                foreach (var child in EnumerateExpressions(binary.Right)) yield return child;
                break;
            case ScalarRangeExpressionNode range:
                foreach (var child in EnumerateExpressions(range.Start)) yield return child;
                foreach (var child in EnumerateExpressions(range.End)) yield return child;
                break;
            case ConditionalExpressionNode conditional:
                foreach (var child in EnumerateExpressions(conditional.Condition)) yield return child;
                foreach (var child in EnumerateExpressions(conditional.WhenTrue)) yield return child;
                foreach (var child in EnumerateExpressions(conditional.WhenFalse)) yield return child;
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    foreach (var child in EnumerateExpressions(field.Value)) yield return child;
                }
                foreach (var value in initializer.Values)
                {
                    foreach (var child in EnumerateExpressions(value)) yield return child;
                }
                break;
            case FunctionExpressionNode function:
                if (function.ExpressionBody is not null)
                {
                    foreach (var child in EnumerateExpressions(function.ExpressionBody)) yield return child;
                }
                if (function.BlockBody is not null)
                {
                    foreach (var child in EnumerateExpressions(function.BlockBody)) yield return child;
                }
                break;
            case AssignmentExpressionNode assignment:
                foreach (var child in EnumerateExpressions(assignment.Target)) yield return child;
                foreach (var child in EnumerateExpressions(assignment.Value)) yield return child;
                break;
            case CallExpressionNode call:
                foreach (var child in EnumerateExpressions(call.Callee)) yield return child;
                foreach (var argument in call.Arguments)
                {
                    foreach (var child in EnumerateExpressions(argument)) yield return child;
                }
                break;
            case GenericCallExpressionNode call:
                foreach (var child in EnumerateExpressions(call.Callee)) yield return child;
                foreach (var argument in call.Arguments)
                {
                    foreach (var child in EnumerateExpressions(argument)) yield return child;
                }
                break;
            case MemberExpressionNode member:
                foreach (var child in EnumerateExpressions(member.Target)) yield return child;
                break;
            case IndexExpressionNode index:
                foreach (var child in EnumerateExpressions(index.Target)) yield return child;
                foreach (var child in EnumerateExpressions(index.Index)) yield return child;
                break;
        }
    }

    private static string Key(FunctionNode function, IReadOnlyList<string> arguments) =>
        $"{(function.OwnerTypeNode is null ? function.Name : $"{function.OwnerTypeNode.ToTypeName()}.{function.Name}")}<{string.Join(",", arguments)}>";
}
