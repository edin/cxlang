using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed record ForeachAnalysisResult(
    Dictionary<string, string> Variables,
    Dictionary<string, LocalMutability> Mutability);

internal sealed class ForeachSemanticAnalyzer(
    DiagnosticBag diagnostics,
    TypeSystem typeSystem,
    TypeCompatibility typeCompatibility,
    ExpressionTypeResolver expressionTypeResolver,
    Func<TypeNode?, string?> typeTextOrNull)
{
    public ForeachAnalysisResult AnalyzeForeach(
        ForeachStatement foreachStatement,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, LocalMutability> mutability)
    {
        var foreachVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        var foreachMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
        var iterableExpression = foreachStatement.IterableExpression.SourceText;
        if (foreachStatement.IterableExpression is ScalarRangeExpressionNode rangeExpression)
        {
            if (foreachStatement.KeyBinding is not null)
            {
                diagnostics.Report(foreachStatement.Location, "Key/value foreach is not supported for scalar ranges.");
            }

            var rangeType = expressionTypeResolver.Resolve(rangeExpression, variables) ?? "int";
            AddForeachScalarRangeBindings(foreachStatement, foreachVariables, foreachMutability, rangeType);
        }
        else if (!TryResolveVariableType(iterableExpression, variables, out var iterableType))
        {
            diagnostics.Report(
                foreachStatement.Location,
                $"Cannot resolve foreach iterable '{iterableExpression}'. Use a visible local/global value, fixed array, scalar range like 0..10, or a type satisfying foreach requirements.");
        }
        else if (foreachStatement.KeyBinding is not null)
        {
            if (TryResolveForeachTypes(
                iterableType,
                keyValue: true,
                out var keyValueElementType,
                out var keyValueKeyType))
            {
                if (keyValueKeyType is not null)
                {
                    var keyBindingType = typeTextOrNull(foreachStatement.KeyBinding.TypeNode);
                    foreachVariables[foreachStatement.KeyBinding.Name] = keyBindingType is null
                        ? keyValueKeyType
                        : keyBindingType;
                    foreachMutability[foreachStatement.KeyBinding.Name] = LocalMutability.ForeachKey;
                }

                AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, keyValueElementType);
            }
            else
            {
                ReportForeachRequirementFailure(
                    foreachStatement,
                    iterableType,
                    SatisfiesRequirement(iterableType, "Contiguous"));
            }
        }
        else if (TryParseFixedArrayType(iterableType, out var arrayElementType, out _))
        {
            AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, arrayElementType);
        }
        else if (TryResolveForeachTypes(
            iterableType,
            keyValue: false,
            out var iteratorElementType,
            out _))
        {
            AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, iteratorElementType);
        }
        else if (SatisfiesRequirement(iterableType, "Contiguous") is { Success: true } contiguous
            && contiguous.TypeBindings.TryGetValue("T", out var contiguousElementType))
        {
            AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, contiguousElementType);
        }
        else if (SatisfiesRequirement(iterableType, "ContiguousRange") is { Success: true } range
            && range.TypeBindings.TryGetValue("T", out var rangeElementType))
        {
            AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, rangeElementType);
        }
        else if (SatisfiesRequirement(iterableType, "Contiguous") is { } match && !match.Success)
        {
            ReportForeachRequirementFailure(foreachStatement, iterableType, match);
        }

        return new ForeachAnalysisResult(foreachVariables, foreachMutability);
    }

    private void AddForeachValueBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        string elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            var declaredIndexType = typeTextOrNull(indexBinding.TypeNode);
            var indexType = declaredIndexType is null
                ? "usize"
                : declaredIndexType;
            variables[indexBinding.Name] = indexType;
            mutability[indexBinding.Name] = LocalMutability.ForeachIndex;
        }

        var valueBindingType = typeTextOrNull(foreachStatement.ValueBinding.TypeNode);
        var declaredElementType = valueBindingType is null
            ? elementType
            : valueBindingType;
        if (valueBindingType is not null
            && !typeCompatibility.CanAssign(valueBindingType, elementType, out var reason))
        {
            diagnostics.Report(
                foreachStatement.ValueBinding.Location,
                $"Type mismatch for foreach value '{foreachStatement.ValueBinding.Name}': {reason}.");
        }

        variables[foreachStatement.ValueBinding.Name] = declaredElementType;
        mutability[foreachStatement.ValueBinding.Name] = foreachStatement.ValueBinding.IsConst
            ? LocalMutability.ForeachConstItem
            : LocalMutability.Mutable;
    }

    private void AddForeachScalarRangeBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        string elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            var declaredIndexType = typeTextOrNull(indexBinding.TypeNode);
            variables[indexBinding.Name] = declaredIndexType is null
                ? "usize"
                : declaredIndexType;
            mutability[indexBinding.Name] = LocalMutability.ForeachIndex;
        }

        var declaredValueType = typeTextOrNull(foreachStatement.ValueBinding.TypeNode);
        var declaredElementType = declaredValueType is null
            ? elementType
            : declaredValueType;
        variables[foreachStatement.ValueBinding.Name] = declaredElementType;
        mutability[foreachStatement.ValueBinding.Name] = foreachStatement.ValueBinding.IsConst
            ? LocalMutability.ForeachConstItem
            : LocalMutability.Mutable;
    }

    private bool TryResolveForeachTypes(
        string iterableType,
        bool keyValue,
        out string valueType,
        out string? keyType)
    {
        valueType = string.Empty;
        keyType = null;
        return typeSystem.TryResolveForeachTypes(iterableType, keyValue, out valueType, out keyType);
    }

    private void ReportForeachRequirementFailure(
        ForeachStatement foreachStatement,
        string iterableType,
        RequirementMatch contiguousMatch)
    {
        var keyValue = foreachStatement.KeyBinding is not null;
        var iterableRequirementName = keyValue ? "KeyValueIterable" : "Iterable";
        var iteratorRequirementName = keyValue ? "KeyValueIterator" : "Iterator";
        var iterableRequirementDisplay = keyValue ? "KeyValueIterable<K, V, I>" : "Iterable<T, I>";
        var rangeMatch = SatisfiesRequirement(iterableType, "ContiguousRange");
        var iteratorMatch = SatisfiesRequirement(iterableType, iterableRequirementName);
        var parts = new List<string>
        {
            $"Type '{iterableType}' cannot be used in foreach.",
            keyValue
                ? "Expected key/value foreach source: KeyValueIterable<K, V, I> where I: KeyValueIterator<K, V>."
                : "Expected foreach source: fixed array, scalar range, Iterable<T, I> where I: Iterator<T>, Contiguous<T>, or ContiguousRange<T>.",
        };

        if (!iteratorMatch.Success)
        {
            parts.Add($"{iterableRequirementDisplay}: " + FormatRequirementFailures(iteratorMatch.Failures));
        }
        else if (!iteratorMatch.TypeBindings.TryGetValue("I", out var iteratorType))
        {
            parts.Add($"{iterableRequirementDisplay}: could not infer iterator type 'I' from iterator().");
        }
        else
        {
            var concreteIteratorMatch = SatisfiesRequirement(iteratorType, iteratorRequirementName);
            if (!concreteIteratorMatch.Success)
            {
                parts.Add($"{iteratorType} must satisfy {iteratorRequirementName}: {FormatRequirementFailures(concreteIteratorMatch.Failures)}");
            }
        }

        if (contiguousMatch.Failures.Count > 0)
        {
            parts.Add("Contiguous<T>: " + FormatRequirementFailures(contiguousMatch.Failures));
        }

        if (rangeMatch.Failures.Count > 0)
        {
            parts.Add("ContiguousRange<T>: " + FormatRequirementFailures(rangeMatch.Failures));
        }

        parts.Add(keyValue
            ? "Add iterator(self: Self*) plus next/key/value methods, or use 'foreach item in source' for value iteration."
            : "Add iterator(self: Self*) with next/value methods, data/length fields, or start/end fields.");

        diagnostics.Report(foreachStatement.Location, string.Join(" ", parts));
    }

    private RequirementMatch SatisfiesRequirement(
        string concreteType,
        string requirementName,
        IReadOnlyList<string>? requirementArguments = null) =>
        typeSystem.SatisfiesRequirement(concreteType, requirementName, requirementArguments);

    private static string FormatRequirementFailures(IReadOnlyList<string> failures) =>
        RequirementDeclarationAnalyzer.FormatRequirementFailures(failures);

    private static bool TryResolveVariableType(
        string expression,
        IReadOnlyDictionary<string, string> variables,
        out string type)
    {
        return variables.TryGetValue(expression.Trim(), out type!);
    }

    private static bool TryParseFixedArrayType(string type, out string elementType, out string length)
    {
        elementType = string.Empty;
        length = string.Empty;
        type = type.Trim();
        if (!type.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var openBracket = type.LastIndexOf('[');
        if (openBracket < 0)
        {
            return false;
        }

        elementType = type[..openBracket].Trim();
        length = type[(openBracket + 1)..^1].Trim();
        return !string.IsNullOrWhiteSpace(elementType) && !string.IsNullOrWhiteSpace(length);
    }
}
