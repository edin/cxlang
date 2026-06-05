namespace Cx.Compiler.C;

internal sealed class CExpressionEmitter
{
    public string Emit(CExpression expression) => expression switch
    {
        CRawExpression raw => raw.Text,
        CLiteralExpression literal => literal.Text,
        CNameExpression name => name.Name,
        CParenthesizedExpression parenthesized => $"({Emit(parenthesized.Expression)})",
        CCastExpression cast => $"({cast.TargetType}) {Emit(cast.Expression)}",
        CUnaryExpression unary => unary.Operator + Emit(unary.Operand),
        CPostfixExpression postfix => $"{Emit(postfix.Operand)}{postfix.Operator}",
        CSizeOfTypeExpression sizeOf => $"sizeof({sizeOf.TypeName})",
        CSizeOfExpression sizeOf => $"sizeof({Emit(sizeOf.Expression)})",
        CBinaryExpression binary => $"{Emit(binary.Left)} {binary.Operator} {Emit(binary.Right)}",
        CConditionalExpression conditional => $"{Emit(conditional.Condition)} ? {Emit(conditional.WhenTrue)} : {Emit(conditional.WhenFalse)}",
        CAssignmentExpression assignment => $"{Emit(assignment.Target)} {assignment.Operator} {Emit(assignment.Value)}",
        CMemberExpression member => $"{Emit(member.Target)}{member.AccessOperator}{member.MemberName}",
        CIndexExpression index => $"{Emit(index.Target)}[{Emit(index.Index)}]",
        CInitializerExpression initializer => EmitInitializer(initializer),
        CCallExpression call => EmitCall(call),
        _ => throw new InvalidOperationException($"Unexpected C expression node {expression.GetType().Name}."),
    };

    private string EmitCall(CCallExpression call) =>
        $"{call.Function.Name}({string.Join(", ", call.Arguments.Select(Emit))})";

    private string EmitInitializer(CInitializerExpression initializer)
    {
        var fields = initializer.Fields.Select(field => $".{field.Name} = {Emit(field.Value)}");
        var values = initializer.Values.Select(Emit);
        var body = string.Join(", ", fields.Concat(values));
        if (string.IsNullOrWhiteSpace(body))
        {
            body = "0";
        }

        return initializer.TypeName is null
            ? "{ " + body + " }"
            : $"({initializer.TypeName}){{ {body} }}";
    }
}
