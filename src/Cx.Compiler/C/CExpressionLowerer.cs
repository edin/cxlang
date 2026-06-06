using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.C;

internal interface ICExpressionLoweringContext
{
    string? SelfType { get; }

    CExpression LowerExpression(ExpressionNode expression);

    string Lower(ExpressionNode expression);

    CExpression LowerNameExpression(NameExpressionNode name);

    CExpression LowerAddressOfExpression(ExpressionNode operand);

    string LowerRawText(string text);

    string LowerType(string type);

    string LowerType(TypeRef type);

    string LowerType(TypeNode? typeNode, string fallbackType);

    bool ShouldUseRawLowering(string text);

    bool ShouldUseRawAssignmentLowering(string text);

    CExpression? TryWrapAssignmentValue(AssignmentExpressionNode assignment, CExpression value);

    string? TryWrapAssignmentValueText(AssignmentExpressionNode assignment, string loweredValue);

    CExpression? TryRepairAssignmentTarget(CExpression target);

    CExpression? TryLowerMemberExpression(MemberExpressionNode member);

    string? TryLowerMemberText(MemberExpressionNode member);
}

internal sealed class CExpressionLowerer(ICExpressionLoweringContext context)
{
    public CExpression LowerSimple(ExpressionNode expression) => expression switch
    {
        LiteralExpressionNode literal => new CLiteralExpression(LowerLiteral(literal.SourceText)),
        NameExpressionNode name => context.LowerNameExpression(name),
        ParenthesizedExpressionNode parenthesized => new CParenthesizedExpression(context.LowerExpression(parenthesized.Expression)),
        CastExpressionNode cast => new CCastExpression(
            LowerType(cast.TargetTypeNode, cast.TargetType),
            context.LowerExpression(cast.Expression)),
        UnaryExpressionNode { Operator: "&" } unary => context.LowerAddressOfExpression(unary.Operand),
        UnaryExpressionNode { Operator: "*" } unary => new CUnaryExpression(
            unary.Operator,
            context.LowerExpression(unary.Operand)),
        UnaryExpressionNode unary => context.ShouldUseRawLowering(unary.SourceText)
            ? new CRawExpression(context.LowerRawText(unary.SourceText))
            : new CUnaryExpression(unary.Operator, context.LowerExpression(unary.Operand)),
        PostfixExpressionNode postfix => new CPostfixExpression(context.LowerExpression(postfix.Operand), postfix.Operator),
        SizeOfExpressionNode sizeOf => LowerSizeOf(sizeOf),
        BinaryExpressionNode binary => LowerBinary(binary),
        ConditionalExpressionNode conditional => LowerConditional(conditional),
        InitializerExpressionNode initializer => LowerInitializer(initializer),
        AssignmentExpressionNode assignment => LowerAssignment(assignment),
        MemberExpressionNode member => LowerMember(member),
        IndexExpressionNode index => context.ShouldUseRawLowering(index.SourceText)
            ? new CRawExpression(context.LowerRawText(index.SourceText))
            : new CIndexExpression(context.LowerExpression(index.Target), context.LowerExpression(index.Index)),
        _ => new CRawExpression(context.LowerRawText(expression.SourceText)),
    };

    private CExpression LowerBinary(BinaryExpressionNode binary)
    {
        if (context.ShouldUseRawLowering(binary.SourceText))
        {
            return new CRawExpression(context.LowerRawText(binary.SourceText));
        }

        return binary.Operator == "<=>"
            ? new CCallExpression(new CFunctionName("compare"), [context.LowerExpression(binary.Left), context.LowerExpression(binary.Right)])
            : new CBinaryExpression(context.LowerExpression(binary.Left), binary.Operator, context.LowerExpression(binary.Right));
    }

    private CExpression LowerSizeOf(SizeOfExpressionNode sizeOf)
    {
        if (!string.IsNullOrWhiteSpace(sizeOf.TypeOperand))
        {
            return new CSizeOfTypeExpression(LowerType(sizeOf.TypeOperandNode, sizeOf.TypeOperand));
        }

        return sizeOf.ExpressionOperand is null
            ? new CSizeOfTypeExpression("void")
            : new CSizeOfExpression(context.LowerExpression(sizeOf.ExpressionOperand));
    }

    private CExpression LowerConditional(ConditionalExpressionNode conditional)
    {
        if (context.ShouldUseRawLowering(conditional.SourceText))
        {
            return new CRawExpression(context.LowerRawText(conditional.SourceText));
        }

        return new CConditionalExpression(
            context.LowerExpression(conditional.Condition),
            context.LowerExpression(conditional.WhenTrue),
            context.LowerExpression(conditional.WhenFalse));
    }

    public string LowerSimpleText(ExpressionNode expression, CExpressionEmitter emitter) => expression switch
    {
        LiteralExpressionNode literal => LowerLiteral(literal.SourceText),
        NameExpressionNode name => emitter.Emit(context.LowerNameExpression(name)),
        ParenthesizedExpressionNode parenthesized => $"({context.Lower(parenthesized.Expression)})",
        CastExpressionNode cast => $"({LowerType(cast.TargetTypeNode, cast.TargetType)}) {context.Lower(cast.Expression)}",
        UnaryExpressionNode { Operator: "&" } unary => emitter.Emit(context.LowerAddressOfExpression(unary.Operand)),
        UnaryExpressionNode unary => context.ShouldUseRawLowering(unary.SourceText)
            ? context.LowerRawText(unary.SourceText)
            : unary.Operator + context.Lower(unary.Operand),
        PostfixExpressionNode postfix => $"{context.Lower(postfix.Operand)}{postfix.Operator}",
        SizeOfExpressionNode sizeOf => emitter.Emit(LowerSizeOf(sizeOf)),
        BinaryExpressionNode binary => emitter.Emit(LowerBinary(binary)),
        ConditionalExpressionNode conditional => emitter.Emit(LowerConditional(conditional)),
        InitializerExpressionNode initializer => emitter.Emit(LowerInitializer(initializer)),
        AssignmentExpressionNode assignment => emitter.Emit(LowerAssignment(assignment)),
        MemberExpressionNode member => context.TryLowerMemberText(member)
            ?? $"{context.Lower(member.Target)}.{member.MemberName}",
        IndexExpressionNode index => context.ShouldUseRawLowering(index.SourceText)
            ? context.LowerRawText(index.SourceText)
            : $"{context.Lower(index.Target)}[{context.Lower(index.Index)}]",
        _ => context.LowerRawText(expression.SourceText),
    };

    public CExpression LowerInitializer(InitializerExpressionNode initializer, string? targetType = null) =>
        new CInitializerExpression(
            initializer.TypeName is null ? null : LowerType(initializer.TypeNameNode, initializer.TypeName),
            initializer.Fields
                .Select(field => new CInitializerField(field.Name, context.LowerExpression(field.Value)))
                .ToList(),
            initializer.Values.Select(context.LowerExpression).ToList());

    public string LowerInitializerText(
        InitializerExpressionNode initializer,
        CExpressionEmitter emitter,
        string? targetType = null) =>
        emitter.Emit(LowerInitializer(initializer, targetType));

    private CExpression LowerAssignment(AssignmentExpressionNode assignment)
    {
        if (context.ShouldUseRawAssignmentLowering(assignment.SourceText))
        {
            return new CRawExpression(context.LowerRawText(assignment.SourceText));
        }

        var value = context.LowerExpression(assignment.Value);
        value = context.TryWrapAssignmentValue(assignment, value) ?? value;

        var target = context.LowerExpression(assignment.Target);
        target = context.TryRepairAssignmentTarget(target) ?? target;

        return new CAssignmentExpression(target, assignment.Operator, value);
    }

    private CExpression LowerMember(MemberExpressionNode member)
    {
        if (context.ShouldUseRawLowering(member.SourceText))
        {
            return new CRawExpression(context.LowerRawText(member.SourceText));
        }

        return context.TryLowerMemberExpression(member)
            ?? new CMemberExpression(context.LowerExpression(member.Target), ".", member.MemberName);
    }

    private static string LowerLiteral(string text) => text switch
    {
        "true" => "1",
        "false" => "0",
        "null" => "NULL",
        _ => text,
    };

    private string LowerType(TypeNode? typeNode, string fallbackType) =>
        typeNode?.Semantic.Type is { } type
            ? context.LowerType(type)
            : context.LowerType(typeNode, fallbackType);
}
