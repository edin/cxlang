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

        AnalyzeReturnCore(
            statement,
            returnType,
            () => analyzeExpression(statement.Expression!, statement.Location, variables, mutability),
            () => assignmentAnalyzer.CheckAssignmentCompatibility(statement.Location, returnType, statement.Expression, variables, "return value"));
    }

    public void AnalyzeReturn(
        ReturnStatement statement,
        TypeRef returnType,
        IReadOnlyDictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        IReadOnlyDictionary<string, LocalMutability> mutability,
        Action<ExpressionNode, Location, IReadOnlyDictionary<string, string>, TypeEnvironment?, IReadOnlyDictionary<string, LocalMutability>?> analyzeExpression)
    {
        if (IsVoidType(returnType))
        {
            if (statement.Expression is not null)
            {
                analyzeExpression(statement.Expression, statement.Location, variables, typeEnvironment, mutability);
                diagnostics.Report(statement.Location, "Cannot return a value from function returning void.");
            }

            return;
        }

        AnalyzeReturnCore(
            statement,
            returnType,
            () => analyzeExpression(statement.Expression!, statement.Location, variables, typeEnvironment, mutability),
            () => assignmentAnalyzer.CheckAssignmentCompatibility(statement.Location, returnType, statement.Expression, typeEnvironment, "return value"));
    }

    private void AnalyzeReturnCore(
        ReturnStatement statement,
        TypeRef returnType,
        Action analyzeExpression,
        Action checkAssignmentCompatibility)
    {
        if (statement.Expression is null)
        {
            diagnostics.Report(statement.Location, $"Function returning '{FormatTypeRef(returnType)}' must return a value.");
            return;
        }

        analyzeExpression();
        if (IsBareNull(statement.Expression) && !IsNullableType(returnType))
        {
            diagnostics.Report(statement.Location, $"Cannot return null from function returning non-pointer type '{FormatTypeRef(returnType)}'.");
        }

        checkAssignmentCompatibility();
    }

    private static bool IsVoidType(TypeRef? type) =>
        TypeRefFacts.IsNamed(type, "void");

    private static bool IsNullableType(TypeRef? type) =>
        TypeRefFacts.IsPointer(type);

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

    private static bool IsBareNull(ExpressionNode expression) =>
        string.Equals(expression.SourceText.Trim(), "null", StringComparison.Ordinal);
}
