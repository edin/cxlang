using Cx.Compiler.Diagnostics;
using Cx.Compiler.Lowering;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Tests;

public sealed class GenericSpecializationPassTests
{
    [Fact]
    public void Apply_AddsConcreteFunctionForResolvedGenericCall()
    {
        var program = CompilerTestHelpers.Parse(
            """
            fn identity<T>(value: T) -> T {
                return value;
            }

            fn unused<T>(value: T) -> T {
                return value;
            }

            fn main() -> int {
                return identity<int>(10);
            }
            """);
        CompilerTestHelpers.Resolve(program);

        var diagnostics = new DiagnosticBag();
        var lowered = GenericSpecializationPass.Apply(program, diagnostics);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var specializations = lowered.Functions
            .Where(function => function.TypeParameters.Count == 0 && FunctionTypeArguments(function).Count > 0)
            .ToList();
        var identity = Assert.Single(specializations);
        var main = lowered.Functions.Single(function => function.Name == "main");
        var ret = main.Body.OfType<ReturnStatement>().Single();
        var call = Assert.IsType<GenericCallExpressionNode>(ret.Expression);

        Assert.Equal("identity", identity.Name);
        Assert.Equal(["int"], FunctionTypeArguments(identity));
        Assert.Equal("int", identity.ReturnTypeNode.ToTypeName());
        Assert.Equal("int", Assert.Single(identity.Parameters).TypeNode.ToTypeName());
        Assert.Same(identity, call.Semantic.ResolvedCall?.Function);
        Assert.DoesNotContain(lowered.Functions, function => function.Name == "unused" && FunctionTypeArguments(function).Count > 0);
    }

    [Fact]
    public void Apply_AddsConcreteFunctionForInferredGenericCall()
    {
        var program = CompilerTestHelpers.Parse(
            """
            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main() -> int {
                return identity(10);
            }
            """);
        CompilerTestHelpers.Resolve(program);

        var diagnostics = new DiagnosticBag();
        var lowered = GenericSpecializationPass.Apply(program, diagnostics);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var identity = Assert.Single(lowered.Functions, function => function.Name == "identity" && FunctionTypeArguments(function).Count > 0);

        Assert.Equal(["int"], FunctionTypeArguments(identity));
        Assert.Equal("int", identity.ReturnTypeNode.ToTypeName());
        Assert.Equal("int", Assert.Single(identity.Parameters).TypeNode.ToTypeName());
    }

    [Fact]
    public void Apply_AddsConcreteStructForUsedGenericStruct()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            struct Unused<T> {
                value: T;
            }

            fn main() -> int {
                let box: Box<int> = Box<int> { value: 10 };
                return box.value;
            }
            """);
        CompilerTestHelpers.Resolve(program);

        var diagnostics = new DiagnosticBag();
        var lowered = GenericSpecializationPass.Apply(program, diagnostics);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var box = Assert.Single(lowered.Structs, structNode => structNode.Name == "Box_int");
        var field = Assert.Single(box.Fields);
        var main = lowered.Functions.Single(function => function.Name == "main");
        var let = Assert.IsType<LetStatement>(main.Body[0]);
        var initializer = Assert.IsType<InitializerExpressionNode>(let.Initializer);

        Assert.Equal("value", field.Name);
        Assert.Equal("int", field.TypeNode?.TypeName);
        Assert.Equal("Box_int", let.TypeNode?.TypeName);
        Assert.Equal("Box_int", initializer.TypeNameNode?.TypeName);
        Assert.DoesNotContain(lowered.Structs, structNode => structNode.Name == "Unused_int");
    }

    private static IReadOnlyList<string> FunctionTypeArguments(FunctionNode function) =>
        (function.TypeArgumentNodes ?? []).Select(node => node.TypeName).ToList();
}
