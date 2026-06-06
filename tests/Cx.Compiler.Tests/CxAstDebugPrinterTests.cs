using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;

namespace Cx.Compiler.Tests;

public sealed class CxAstDebugPrinterTests
{
    [Fact]
    public void Print_ShowsAliasAndGenericSemanticTypes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type usize = unsigned long long;

            struct Maybe<T> {
                value: T;
            }

            fn size() -> Maybe<usize> {
                let value: Maybe<usize> = Maybe<usize>(0);
                return value;
            }
            """);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var output = new CxAstDebugPrinter().Print(program);

        Assert.Contains("TypeAlias usize", output);
        Assert.Contains("Semantic.Type=Alias(usize -> unsigned long long)", output);
        Assert.Contains("Function size -> Maybe<usize>", output);
        Assert.Contains("Semantic.Type=Maybe<Alias(usize -> unsigned long long)>", output);
        Assert.Contains("Let value: Maybe<usize>", output);
    }

    [Fact]
    public void Print_BeforeSemanticResolutionStillShowsTypeNodeText()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }
            """);

        var output = new CxAstDebugPrinter().Print(program);

        Assert.Contains("Struct Box<T>", output);
        Assert.Contains("Field value: T", output);
        Assert.Contains("Type TypeNode=T", output);
        Assert.DoesNotContain("Semantic.Type=", output);
    }
}
