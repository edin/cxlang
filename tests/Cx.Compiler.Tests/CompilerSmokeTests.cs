using Cx.Compiler.C;

namespace Cx.Compiler.Tests;

public sealed class CompilerSmokeTests
{
    [Fact]
    public void CompileToC_AcceptsCxSourceFile()
    {
        var result = CompilerTestHelpers.Compile(
            """
            fn main() -> int {
                return 0;
            }
            """);

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("int main()", result.Output);
        Assert.Contains("return 0;", result.Output);
    }

    [Fact]
    public void CompileToC_NamedModuleDoesNotPrefixCNamesYet()
    {
        var result = CompilerTestHelpers.Compile(
            """
            module app.main;

            fn helper() -> int {
                return 1;
            }

            fn main() -> int {
                return helper();
            }
            """);

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("int helper()", result.Output);
        Assert.Contains("return helper();", result.Output);
        Assert.DoesNotContain("app_main_helper", result.Output);
    }

    [Fact]
    public void CompileToC_WithModulePrefixesCanDisambiguateModuleFunctions()
    {
        var result = CompilerTestHelpers.Compile(
        [
            CompilerTestHelpers.Source(
                """
                module app.main;

                import lib.a;
                import lib.b;

                fn main() -> int {
                    return lib.a.helper() + lib.b.helper();
                }
                """),
            CompilerTestHelpers.Source(
                """
                module lib.a;

                fn helper() -> int {
                    return 1;
                }
                """,
                "lib-a.cx"),
            CompilerTestHelpers.Source(
                """
                module lib.b;

                fn helper() -> int {
                    return 2;
                }
                """,
                "lib-b.cx"),
        ],
        new CNameManglerOptions(UseModulePrefixes: true));

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("int lib_a_helper()", result.Output);
        Assert.Contains("int lib_b_helper()", result.Output);
        Assert.Contains("return lib_a_helper() + lib_b_helper();", result.Output);
    }

    [Fact]
    public void CompileToC_LowersDirectFunctionReferences()
    {
        var result = CompilerTestHelpers.Compile(
            """
            fn add(left: int, right: int) -> int {
                return left + right;
            }

            struct Box {
                value: int;

                static fn create(value: int) -> Box {
                    return Box(value);
                }
            }

            fn main() -> int {
                let op: fn(int, int) -> int = add;
                let make: fn(int) -> Box = Box.create;
                let box: Box = make(op(1, 2));
                return box.value;
            }
            """);

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("(*op)(int, int) = add;", result.Output);
        Assert.Contains("(*make)(int) = Box_create;", result.Output);
    }

    [Fact]
    public void CompileToC_KeepsAliasSpellingForGenericCNames()
    {
        var result = CompilerTestHelpers.Compile(
            """
            type usize = unsigned long long;

            struct Maybe<T> {
                has_value: bool;
                value: T;
            }

            fn size() -> Maybe<usize> {
                let value: Maybe<usize> = Maybe<usize>(false, 0);
                return value;
            }
            """);

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("Maybe_usize size()", result.Output);
        Assert.Contains("Maybe_usize value =", result.Output);
        Assert.DoesNotContain("Maybe_unsignedlonglong", result.Output);
    }

    [Fact]
    public void CompileTestsToC_GeneratesRunnerForTestBlock()
    {
        var result = new CxCompiler().CompileTestsToC(
        [
            CompilerTestHelpers.Source(
                """
                test "math works" {
                    expect_eq_int(42, 40 + 2);
                }
                """,
                "sample.cx"),
        ]);

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("TestRunner runner = TestRunner_create();", result.Output);
        Assert.Contains("TestRunner_begin(&runner, \"math works\");", result.Output);
        Assert.Contains("TestRunner_expect_int(runner, 42, 40 + 2", result.Output);
        Assert.Contains("return TestRunner_result(&runner);", result.Output);
    }

    [Fact]
    public void CompileTestsToC_WithStdCoreModule_CollectsEmbeddedStdTestsWithoutUserSources()
    {
        var result = new CxCompiler().CompileTestsToC([], "std.core");

        CompilerTestHelpers.AssertSuccess(result);
        Assert.Contains("TestRunner_begin(&runner, \"string view trim\");", result.Output);
        Assert.Contains("TestRunner_begin(&runner, \"vec push get and pop\");", result.Output);
        Assert.Contains("return TestRunner_result(&runner);", result.Output);
    }

    [Fact]
    public void CompileToC_UnknownCFunctionSuggestsImport()
    {
        var result = CompilerTestHelpers.Compile(
            """
            fn main() -> int {
                clock();
                return 0;
            }
            """);

        CompilerTestHelpers.AssertDiagnosticContains(result, "Unknown function 'clock'", "import c.time");
    }
}
