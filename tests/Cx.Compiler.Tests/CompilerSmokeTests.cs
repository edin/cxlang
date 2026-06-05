using Cx.Compiler.Syntax;

namespace Cx.Compiler.Tests;

public sealed class CompilerSmokeTests
{
    [Fact]
    public void CompileToC_AcceptsCxSourceFile()
    {
        var result = new CxCompiler().CompileToC(
        [
            Source(
                "main.cx",
                """
                fn main() -> int {
                    return 0;
                }
                """),
        ]);

        AssertSuccess(result);
        Assert.Contains("int main()", result.Output);
        Assert.Contains("return 0;", result.Output);
    }

    [Fact]
    public void CompileTestsToC_GeneratesRunnerForTestBlock()
    {
        var result = new CxCompiler().CompileTestsToC(
        [
            Source(
                "sample.cx",
                """
                test "math works" {
                    expect_eq_int(42, 40 + 2);
                }
                """),
        ]);

        AssertSuccess(result);
        Assert.Contains("TestRunner runner = TestRunner_create();", result.Output);
        Assert.Contains("TestRunner_begin(&runner, \"math works\");", result.Output);
        Assert.Contains("TestRunner_expect_int(runner, 42, 40 + 2", result.Output);
        Assert.Contains("return TestRunner_result(&runner);", result.Output);
    }

    [Fact]
    public void CompileTestsToC_WithStdCoreModule_CollectsEmbeddedStdTestsWithoutUserSources()
    {
        var result = new CxCompiler().CompileTestsToC([], "std.core");

        AssertSuccess(result);
        Assert.Contains("TestRunner_begin(&runner, \"string view trim\");", result.Output);
        Assert.Contains("TestRunner_begin(&runner, \"vec push get and pop\");", result.Output);
        Assert.Contains("return TestRunner_result(&runner);", result.Output);
    }

    [Fact]
    public void CompileToC_UnknownCFunctionSuggestsImport()
    {
        var result = new CxCompiler().CompileToC(
        [
            Source(
                "main.cx",
                """
                fn main() -> int {
                    clock();
                    return 0;
                }
                """),
        ]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown function 'clock'", StringComparison.Ordinal)
            && diagnostic.Message.Contains("import c.time", StringComparison.Ordinal));
    }

    private static SourceFile Source(string path, string text) => new(path, text);

    private static void AssertSuccess(CompilationResult result)
    {
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        Assert.NotNull(result.Output);
    }
}
