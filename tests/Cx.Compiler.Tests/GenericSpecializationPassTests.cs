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
            .Where(function => function.TypeParameters.Count == 0 && function.TypeArguments.Count > 0)
            .ToList();
        var identity = Assert.Single(specializations);
        var main = lowered.Functions.Single(function => function.Name == "main");
        var ret = main.Body.OfType<ReturnStatement>().Single();
        var call = Assert.IsType<GenericCallExpressionNode>(ret.Expression);

        Assert.Equal("identity", identity.Name);
        Assert.Equal(["int"], identity.TypeArguments);
        Assert.Equal("int", identity.ReturnType);
        Assert.Equal("int", Assert.Single(identity.Parameters).Type);
        Assert.Same(identity, call.Semantic.ResolvedCall?.Function);
        Assert.DoesNotContain(lowered.Functions, function => function.Name == "unused" && function.TypeArguments.Count > 0);
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
        Assert.Equal("int", field.Type);
        Assert.Equal("Box_int", let.Type);
        Assert.Equal("Box_int", initializer.TypeName);
        Assert.DoesNotContain(lowered.Structs, structNode => structNode.Name == "Unused_int");
    }
}
