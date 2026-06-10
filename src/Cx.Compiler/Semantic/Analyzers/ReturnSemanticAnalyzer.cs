using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ReturnSemanticAnalyzer(
    DiagnosticBag diagnostics,
    AssignmentSemanticAnalyzer assignmentAnalyzer)
{
    public void AnalyzeReturn(
        ReturnStatement statement,
        TypeRef returnType,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, LocalMutability> mutability,
        Action<ExpressionNode, Location, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, LocalMutability>?> analyzeExpression)
    {
        if (IsVoidType(returnType))
        {
            if (statement.Expression is not null)
            {
                analyzeExpression(statement.Expression, statement.Location, variables, mutability);
                diagnostics.Report(statement.Location, "Cannot return a value from function returning void.");
            }

            return;
        }

        if (statement.Expression is null)
        {
            diagnostics.Report(statement.Location, $"Function returning '{FormatTypeRef(returnType)}' must return a value.");
            return;
        }

        analyzeExpression(statement.Expression, statement.Location, variables, mutability);
        if (IsBareNull(statement.Expression) && !IsNullableType(returnType))
        {
            diagnostics.Report(statement.Location, $"Cannot return null from function returning non-pointer type '{FormatTypeRef(returnType)}'.");
        }

        assignmentAnalyzer.CheckAssignmentCompatibility(statement.Location, returnType, statement.Expression, variables, "return value");
    }

    private static bool IsVoidType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Named { Name: "void", Arguments.Count: 0 };

    private static bool IsNullableType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Pointer;

    private static TypeRef? UnwrapAlias(TypeRef? type)
    {
        while (type is TypeRef.Alias alias)
        {
            type = alias.Target;
        }

        return type;
    }

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

    private static bool IsBareNull(ExpressionNode expression) =>
        string.Equals(expression.SourceText.Trim(), "null", StringComparison.Ordinal);
}
