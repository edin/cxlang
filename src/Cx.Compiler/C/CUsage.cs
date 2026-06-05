namespace Cx.Compiler.C;

internal sealed record CUsage(IReadOnlySet<CUsedFunction> Functions)
{
    public static CUsage Empty { get; } = new(new HashSet<CUsedFunction>());
}

internal sealed record CUsedFunction(string? ModuleName, string Name);

internal sealed class CUsageCollector
{
    private readonly HashSet<CUsedFunction> _functions = [];

    public CUsage Collect(CExpression expression)
    {
        Visit(expression);
        return new CUsage(_functions);
    }

    private void Visit(CExpression expression)
    {
        switch (expression)
        {
            case CRawExpression:
            case CLiteralExpression:
            case CNameExpression:
            case CSizeOfTypeExpression:
                break;
            case CParenthesizedExpression parenthesized:
                Visit(parenthesized.Expression);
                break;
            case CCastExpression cast:
                Visit(cast.Expression);
                break;
            case CUnaryExpression unary:
                Visit(unary.Operand);
                break;
            case CPostfixExpression postfix:
                Visit(postfix.Operand);
                break;
            case CSizeOfExpression sizeOf:
                Visit(sizeOf.Expression);
                break;
            case CBinaryExpression binary:
                Visit(binary.Left);
                Visit(binary.Right);
                break;
            case CConditionalExpression conditional:
                Visit(conditional.Condition);
                Visit(conditional.WhenTrue);
                Visit(conditional.WhenFalse);
                break;
            case CAssignmentExpression assignment:
                Visit(assignment.Target);
                Visit(assignment.Value);
                break;
            case CMemberExpression member:
                Visit(member.Target);
                break;
            case CIndexExpression index:
                Visit(index.Target);
                Visit(index.Index);
                break;
            case CInitializerExpression initializer:
                foreach (var field in initializer.Fields)
                {
                    Visit(field.Value);
                }

                foreach (var value in initializer.Values)
                {
                    Visit(value);
                }
                break;
            case CCallExpression call:
                _functions.Add(call.Function switch
                {
                    CResolvedFunction resolved => new CUsedFunction(resolved.ModuleName, resolved.Name),
                    CFunctionName name => new CUsedFunction(ModuleName: null, name.Name),
                    _ => new CUsedFunction(ModuleName: null, call.Function.Name),
                });

                foreach (var argument in call.Arguments)
                {
                    Visit(argument);
                }
                break;
        }
    }
}
