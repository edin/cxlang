using Cx.Compiler.C;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Tests;

public sealed class CExpressionLowererTests
{
    [Fact]
    public void LowerSimple_LowersBinaryExpressionToCAst()
    {
        var location = TestLocation();
        var expression = new BinaryExpressionNode(
            location,
            "a + 1",
            new NameExpressionNode(location, "a"),
            "+",
            new LiteralExpressionNode(location, "1"));

        var lowered = new CExpressionLowerer(new TestContext()).LowerSimple(expression);

        var binary = Assert.IsType<CBinaryExpression>(lowered);
        Assert.Equal("+", binary.Operator);
        Assert.IsType<CNameExpression>(binary.Left);
        Assert.IsType<CLiteralExpression>(binary.Right);
    }

    [Fact]
    public void LowerSimple_LowersCastAndSizeOfUsingContextTypeLowering()
    {
        var location = TestLocation();
        var context = new TestContext(typePrefix: "lowered_");
        var lowerer = new CExpressionLowerer(context);

        var cast = lowerer.LowerSimple(new CastExpressionNode(
            location,
            "(Vec<int>*)value",
            "Vec<int>*",
            new NameExpressionNode(location, "value")));
        var sizeOf = lowerer.LowerSimple(new SizeOfExpressionNode(
            location,
            "sizeof(Vec<int>)",
            "Vec<int>",
            ExpressionOperand: null));

        Assert.Equal("lowered_Vec<int>*", Assert.IsType<CCastExpression>(cast).TargetType);
        Assert.Equal("lowered_Vec<int>", Assert.IsType<CSizeOfTypeExpression>(sizeOf).TypeName);
    }

    [Fact]
    public void LowerSimple_LowersInitializerAndAssignmentExpressions()
    {
        var location = TestLocation();
        var lowerer = new CExpressionLowerer(new TestContext());

        var initializer = lowerer.LowerSimple(new InitializerExpressionNode(
            location,
            "Point { x: 1 }",
            "Point",
            [new InitializerFieldNode("x", new LiteralExpressionNode(location, "1"))],
            []));
        var assignment = lowerer.LowerSimple(new AssignmentExpressionNode(
            location,
            "value = 1",
            new NameExpressionNode(location, "value"),
            "=",
            new LiteralExpressionNode(location, "1")));

        Assert.Equal("Point", Assert.IsType<CInitializerExpression>(initializer).TypeName);
        var loweredAssignment = Assert.IsType<CAssignmentExpression>(assignment);
        Assert.Equal("=", loweredAssignment.Operator);
        Assert.IsType<CNameExpression>(loweredAssignment.Target);
        Assert.IsType<CLiteralExpression>(loweredAssignment.Value);
    }

    [Fact]
    public void LowerSimple_LowersMemberExpressionWithFallbackOrContextOverride()
    {
        var location = TestLocation();
        var fallback = new CExpressionLowerer(new TestContext()).LowerSimple(new MemberExpressionNode(
            location,
            "point.x",
            new NameExpressionNode(location, "point"),
            "x"));
        var overridden = new CExpressionLowerer(new TestContext(memberOverride: new CNameExpression("POINT_X"))).LowerSimple(new MemberExpressionNode(
            location,
            "point.x",
            new NameExpressionNode(location, "point"),
            "x"));

        var member = Assert.IsType<CMemberExpression>(fallback);
        Assert.Equal(".", member.AccessOperator);
        Assert.Equal("x", member.MemberName);
        Assert.Equal("POINT_X", Assert.IsType<CNameExpression>(overridden).Name);
    }

    private static Location TestLocation() =>
        new(new SourceFile("test.cx", string.Empty), Position: 0, Line: 1, Column: 1);

    private sealed class TestContext(
        string typePrefix = "",
        CExpression? memberOverride = null) : ICExpressionLoweringContext
    {
        public string? SelfType => null;

        public CExpression LowerExpression(ExpressionNode expression) =>
            new CExpressionLowerer(this).LowerSimple(expression);

        public string Lower(ExpressionNode expression) =>
            new CExpressionEmitter().Emit(LowerExpression(expression));

        public CExpression LowerNameExpression(NameExpressionNode name) =>
            new CNameExpression(name.SourceText);

        public CExpression LowerAddressOfExpression(ExpressionNode operand) =>
            new CUnaryExpression("&", LowerExpression(operand));

        public string LowerRawText(string text) => text;

        public string LowerType(string type) => typePrefix + type;

        public string LowerType(TypeNode? typeNode, string fallbackType) => LowerType(fallbackType);

        public bool ShouldUseRawLowering(string text) => false;

        public bool ShouldUseRawAssignmentLowering(string text) => false;

        public CExpression? TryWrapAssignmentValue(AssignmentExpressionNode assignment, CExpression value) => null;

        public string? TryWrapAssignmentValueText(AssignmentExpressionNode assignment, string loweredValue) => null;

        public CExpression? TryRepairAssignmentTarget(CExpression target) => null;

        public CExpression? TryLowerMemberExpression(MemberExpressionNode member) => memberOverride;

        public string? TryLowerMemberText(MemberExpressionNode member) =>
            memberOverride is null ? null : new CExpressionEmitter().Emit(memberOverride);
    }
}
