namespace Cx.Compiler.C;

internal abstract record CExpression;

internal sealed record CRawExpression(string Text) : CExpression;

internal sealed record CLiteralExpression(string Text) : CExpression;

internal sealed record CNameExpression(string Name) : CExpression;

internal sealed record CParenthesizedExpression(CExpression Expression) : CExpression;

internal sealed record CCastExpression(string TargetType, CExpression Expression) : CExpression;

internal sealed record CUnaryExpression(string Operator, CExpression Operand) : CExpression;

internal sealed record CPostfixExpression(CExpression Operand, string Operator) : CExpression;

internal sealed record CSizeOfTypeExpression(string TypeName) : CExpression;

internal sealed record CSizeOfExpression(CExpression Expression) : CExpression;

internal sealed record CBinaryExpression(
    CExpression Left,
    string Operator,
    CExpression Right) : CExpression;

internal sealed record CConditionalExpression(
    CExpression Condition,
    CExpression WhenTrue,
    CExpression WhenFalse) : CExpression;

internal sealed record CAssignmentExpression(
    CExpression Target,
    string Operator,
    CExpression Value) : CExpression;

internal sealed record CMemberExpression(
    CExpression Target,
    string AccessOperator,
    string MemberName) : CExpression;

internal sealed record CIndexExpression(
    CExpression Target,
    CExpression Index) : CExpression;

internal sealed record CInitializerExpression(
    string? TypeName,
    IReadOnlyList<CInitializerField> Fields,
    IReadOnlyList<CExpression> Values) : CExpression;

internal sealed record CInitializerField(
    string Name,
    CExpression Value);

internal abstract record CFunctionReference(string Name);

internal sealed record CFunctionName(string Name) : CFunctionReference(Name);

internal sealed record CResolvedFunction(
    string ModuleName,
    string Name) : CFunctionReference(Name);

internal sealed record CCallExpression(
    CFunctionReference Function,
    IReadOnlyList<CExpression> Arguments) : CExpression;
