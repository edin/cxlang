using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ExpressionSemanticAnalyzer(
    DiagnosticBag diagnostics,
    ProgramNode program,
    AssignmentSemanticAnalyzer? assignmentAnalyzer,
    ExpressionTypeResolver expressionTypeResolver,
    TypeCompatibility typeCompatibility,
    SymbolSuggestionService? symbolSuggestions,
    IReadOnlyList<string> currentTypeParameters,
    IReadOnlyList<GenericConstraintNode> currentGenericConstraints,
    Func<TypeNode?, string> typeText,
    Func<string, bool> isKnownTypeName,
    Action<ExpressionNode, Location, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, LocalMutability>?> analyzeExpression)
{
    public void Analyze(
        ExpressionNode? expression,
        Location location,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        Analyze(expression, location, variables, null, mutability);
    }

    public void Analyze(
        ExpressionNode? expression,
        Location location,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        if (expression is null)
        {
            return;
        }

        switch (expression)
        {
            case NameExpressionNode name:
                AnalyzeNameExpression(name, location, variables, typeEnvironment);
                break;
            case ParenthesizedExpressionNode parenthesized:
                Analyze(parenthesized.Expression, location, variables, typeEnvironment, mutability);
                break;
            case CastExpressionNode cast:
                Analyze(cast.Expression, location, variables, typeEnvironment, mutability);
                break;
            case UnaryExpressionNode unary:
                Analyze(unary.Operand, location, variables, typeEnvironment, mutability);
                break;
            case PostfixExpressionNode postfix:
                if (postfix.Operator is "++" or "--")
                {
                    assignmentAnalyzer?.AnalyzeMutationTarget(postfix.Operand, postfix.Location, mutability);
                }

                Analyze(postfix.Operand, location, variables, typeEnvironment, mutability);
                break;
            case SizeOfExpressionNode sizeOf:
                Analyze(sizeOf.ExpressionOperand, location, variables, typeEnvironment, mutability);
                break;
            case BinaryExpressionNode binary:
                Analyze(binary.Left, location, variables, typeEnvironment, mutability);
                Analyze(binary.Right, location, variables, typeEnvironment, mutability);
                break;
            case ScalarRangeExpressionNode range:
                Analyze(range.Start, location, variables, typeEnvironment, mutability);
                Analyze(range.End, location, variables, typeEnvironment, mutability);
                break;
            case ConditionalExpressionNode conditional:
                Analyze(conditional.Condition, location, variables, typeEnvironment, mutability);
                Analyze(conditional.WhenTrue, location, variables, typeEnvironment, mutability);
                Analyze(conditional.WhenFalse, location, variables, typeEnvironment, mutability);
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    Analyze(field.Value, location, variables, typeEnvironment, mutability);
                }

                foreach (var value in initializer.Values)
                {
                    Analyze(value, location, variables, typeEnvironment, mutability);
                }

                break;
            case FunctionExpressionNode functionExpression:
                Analyze(functionExpression.ExpressionBody, location, variables, typeEnvironment, mutability);
                break;
            case AssignmentExpressionNode assignment:
                Analyze(assignment.Target, location, variables, typeEnvironment, mutability);
                Analyze(assignment.Value, location, variables, typeEnvironment, mutability);
                if (variables is not null)
                {
                    if (typeEnvironment is null)
                    {
                        assignmentAnalyzer?.AnalyzeAssignmentExpression(
                            assignment,
                            variables,
                            mutability,
                            analyzeExpression);
                    }
                    else
                    {
                        assignmentAnalyzer?.AnalyzeAssignmentExpression(
                            assignment,
                            variables,
                            typeEnvironment,
                            mutability,
                            analyzeExpression);
                    }
                }

                break;
            case CallExpressionNode call:
                AnalyzeCallExpression(call, location, variables, typeEnvironment);
                Analyze(call.Callee, location, variables, typeEnvironment, mutability);
                foreach (var argument in call.Arguments)
                {
                    Analyze(argument, location, variables, typeEnvironment, mutability);
                }

                break;
            case GenericCallExpressionNode call:
                AnalyzeGenericCallExpression(call, location, variables, typeEnvironment);
                Analyze(call.Callee, location, variables, typeEnvironment, mutability);
                foreach (var argument in call.Arguments)
                {
                    Analyze(argument, location, variables, typeEnvironment, mutability);
                }

                break;
            case MemberExpressionNode member:
                Analyze(member.Target, location, variables, typeEnvironment, mutability);
                break;
            case IndexExpressionNode index:
                Analyze(index.Target, location, variables, typeEnvironment, mutability);
                Analyze(index.Index, location, variables, typeEnvironment, mutability);
                break;
        }
    }

    private void AnalyzeNameExpression(
        NameExpressionNode name,
        Location location,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment)
    {
        if (variables is null && typeEnvironment is null)
        {
            return;
        }

        if (ResolveExpression(name, variables, typeEnvironment) is not null
            || isKnownTypeName(name.SourceText)
            || currentTypeParameters.Contains(name.SourceText, StringComparer.Ordinal)
            || IsKnownConstructorOrVariantCall(name.SourceText))
        {
            return;
        }

        if (symbolSuggestions?.FindAliasSuggestionForValue(name.SourceText) is { } aliasSuggestion)
        {
            diagnostics.Report(location, $"Unknown symbol '{name.SourceText}'. Did you mean '{aliasSuggestion}'?");
        }
        else if (symbolSuggestions?.FindPartialImportSuggestionForValue(name.SourceText) is { } partialSuggestion)
        {
            diagnostics.Report(location, $"Unknown symbol '{name.SourceText}'. Did you mean '{partialSuggestion}'?");
        }
        else if (symbolSuggestions?.FindImportSuggestionForValue(name.SourceText) is { } suggestion)
        {
            diagnostics.Report(location, $"Unknown symbol '{name.SourceText}'. Did you mean to import {suggestion}?");
        }
    }

    private void AnalyzeCallExpression(
        CallExpressionNode call,
        Location location,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment)
    {
        if (variables is null && typeEnvironment is null)
        {
            return;
        }

        if (ResolveCallSignature(call.Callee, [], call.Arguments, variables, typeEnvironment) is { } signature)
        {
            CheckCallArguments(location, signature, call.Arguments, variables, typeEnvironment);
            return;
        }

        ReportUnknownCall(call.Callee, location, variables, typeEnvironment);
    }

    private void AnalyzeGenericCallExpression(
        GenericCallExpressionNode call,
        Location location,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment)
    {
        if (variables is null && typeEnvironment is null)
        {
            return;
        }

        if (ResolveCallSignature(call.Callee, TypeArguments(call.TypeArgumentNodes), call.Arguments, variables, typeEnvironment) is { } signature)
        {
            CheckCallArguments(location, signature, call.Arguments, variables, typeEnvironment);
            return;
        }

        ReportUnknownCall(call.Callee, location, variables, typeEnvironment);
    }

    private CallSignature? ResolveCallSignature(
        ExpressionNode callee,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment)
    {
        var resolvedCall = new CallResolver(
            program,
            expressionTypeResolver.Resolve,
            currentTypeParameters,
            currentGenericConstraints);

        var resolution = typeEnvironment is null
            ? resolvedCall.Resolve(callee, typeArguments, arguments, variables!)
            : resolvedCall.Resolve(callee, typeArguments, arguments, typeEnvironment);
        return resolution is null
            ? null
            : new CallSignature(resolution.Name, resolution.ParameterTypes, resolution.IsVariadic);
    }

    private void CheckCallArguments(
        Location location,
        CallSignature signature,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment)
    {
        if (arguments.Count < signature.ParameterTypes.Count)
        {
            diagnostics.Report(
                location,
                $"Call to '{signature.Name}' expects at least {signature.ParameterTypes.Count} argument(s), got {arguments.Count}.");
            return;
        }

        if (!signature.IsVariadic && arguments.Count > signature.ParameterTypes.Count)
        {
            diagnostics.Report(
                location,
                $"Call to '{signature.Name}' expects {signature.ParameterTypes.Count} argument(s), got {arguments.Count}.");
            return;
        }

        for (var i = 0; i < signature.ParameterTypes.Count && i < arguments.Count; i++)
        {
            var parameterType = signature.ParameterTypes[i];
            if (IsAnyType(parameterType))
            {
                continue;
            }

            var argumentType = ResolveExpressionTypeRef(arguments[i], variables, typeEnvironment);
            if (!typeCompatibility.CanAssign(parameterType, argumentType, out var reason))
            {
                diagnostics.Report(
                    location,
                    $"Argument {i + 1} for call to '{signature.Name}' has incompatible type: {reason}.");
            }
        }
    }

    private void ReportUnknownCall(
        ExpressionNode callee,
        Location location,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment)
    {
        if (ResolveExpression(callee, variables, typeEnvironment) is not null)
        {
            return;
        }

        if (callee is not NameExpressionNode)
        {
            return;
        }

        if (ExpressionNameFacts.GetQualifiedName(callee) is { } name)
        {
            if (IsKnownConstructorOrVariantCall(name))
            {
                return;
            }

            if (isKnownTypeName(name))
            {
                return;
            }

            var aliasSuggestion = symbolSuggestions?.FindAliasSuggestionForFunction(name);
            var partialSuggestion = aliasSuggestion is null ? symbolSuggestions?.FindPartialImportSuggestionForFunction(name) : null;
            var suggestion = aliasSuggestion is null && partialSuggestion is null ? symbolSuggestions?.FindImportSuggestionForFunction(name) : null;
            diagnostics.Report(
                location,
                aliasSuggestion is not null
                    ? $"Unknown function '{name}'. Did you mean '{aliasSuggestion}'?"
                    : partialSuggestion is not null
                    ? $"Unknown function '{name}'. Did you mean '{partialSuggestion}'?"
                    : suggestion is null
                    ? $"Unknown function '{name}'."
                    : $"Unknown function '{name}'. Did you mean to import {suggestion}?");
        }
    }

    private string? ResolveExpression(
        ExpressionNode expression,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment) =>
        typeEnvironment is null
            ? expressionTypeResolver.Resolve(expression, variables!)
            : expressionTypeResolver.Resolve(expression, typeEnvironment);

    private TypeRef? ResolveExpressionTypeRef(
        ExpressionNode expression,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment) =>
        typeEnvironment is null
            ? expressionTypeResolver.ResolveTypeRef(expression, variables!)
            : expressionTypeResolver.ResolveTypeRef(expression, typeEnvironment);

    private bool IsKnownConstructorOrVariantCall(string name)
    {
        if (program.Structs.Any(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal)))
        {
            return true;
        }

        return program.TaggedUnions
            .Where(union => !union.IsRaw)
            .Any(union => union.Variants.Any(variant =>
                string.Equals($"{union.Name}.{variant.Name}", name, StringComparison.Ordinal)
                || string.Equals(variant.Name, name, StringComparison.Ordinal)));
    }

    private static bool IsAnyType(TypeRef? type) =>
        TypeRefFacts.IsNamed(type, "any");

    private IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> nodes) =>
        nodes.Select(typeText).ToList();

    private sealed record CallSignature(
        string Name,
        IReadOnlyList<TypeRef> ParameterTypes,
        bool IsVariadic);
}
