namespace Cx.Compiler.Tests;

public sealed class SemanticReturnTypeRefTests
{
    [Fact]
    public void Compile_AllowsReturningNullForAliasPointerReturnType()
    {
        var result = CompilerTestHelpers.Compile(
            """
            type Bytes = char*;

            fn get_bytes() -> Bytes {
                return null;
            }

            fn main() -> int {
                return get_bytes() == null;
            }
            """);

        CompilerTestHelpers.AssertSuccess(result);
    }

    [Fact]
    public void Compile_ReportsReturningNullForAliasNonPointerReturnType()
    {
        var result = CompilerTestHelpers.Compile(
            """
            type Count = int;

            fn get_count() -> Count {
                return null;
            }

            fn main() -> int {
                return 0;
            }
            """);

        CompilerTestHelpers.AssertDiagnosticContains(result, "Cannot return null", "non-pointer type 'Count'");
    }

    [Fact]
    public void Compile_ReportsReturnMismatchUsingAliasTypeRef()
    {
        var result = CompilerTestHelpers.Compile(
            """
            type Bytes = char*;

            fn get_bytes() -> Bytes {
                return 10;
            }

            fn main() -> int {
                return 0;
            }
            """);

        CompilerTestHelpers.AssertDiagnosticContains(result, "Type mismatch for return value", "cannot assign 'int' to 'Bytes'");
    }
}
