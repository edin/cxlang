using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Tests;

public sealed class TypeResolutionPassTests
{
    [Fact]
    public void Resolve_StoresResolvedTypeRefsBesideSyntaxNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type IntVec = Vec<int>;

            fn main() -> int {
                let values: IntVec = Vec<int>.create();
                return 0;
            }
            """);

        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var local = program.Functions.Single().Body.OfType<LetStatement>().Single();
        Assert.Equal("IntVec", local.Type);
        var alias = Assert.IsType<TypeRef.Alias>(local.Semantic.Type);
        Assert.Equal("IntVec", alias.Name);
        var named = Assert.IsType<TypeRef.Named>(alias.Target);
        Assert.Equal("Vec", named.Name);
        var argument = Assert.IsType<TypeRef.Named>(Assert.Single(named.Arguments));
        Assert.Equal("int", argument.Name);
    }

    [Fact]
    public void Resolve_PreservesAliasTypeRefsWithResolvedTargets()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type usize = unsigned long long;

            fn main() -> int {
                let value: usize = 1;
                return 0;
            }
            """);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var main = Assert.Single(program.Functions);
        var local = Assert.IsType<LetStatement>(Assert.Single(main.Body.OfType<LetStatement>()));
        var alias = Assert.IsType<TypeRef.Alias>(local.TypeNode?.Semantic.Type);

        Assert.Equal("usize", alias.Name);
        Assert.Equal("unsigned long long", Assert.IsType<TypeRef.Named>(alias.Target).Name);
    }
}
