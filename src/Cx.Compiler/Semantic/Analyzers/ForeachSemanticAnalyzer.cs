using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed record ForeachAnalysisResult(
    [property: Cx.Compiler.LegacyStringType("Compatibility foreach scope map. Prefer TypeEnvironment.")]
    Dictionary<string, string> Variables,
    TypeEnvironment TypeEnvironment,
    Dictionary<string, LocalMutability> Mutability);

internal sealed class ForeachSemanticAnalyzer(
    DiagnosticBag diagnostics,
    TypeSystem typeSystem,
    TypeCompatibility typeCompatibility,
    ExpressionTypeResolver expressionTypeResolver,
    TypeRefParser typeRefParser)
{
    public ForeachAnalysisResult AnalyzeForeach(
        ForeachStatement foreachStatement,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, LocalMutability> mutability) =>
        AnalyzeForeach(
            foreachStatement,
            TypeEnvironment.FromLegacyStrings(typeRefParser, variables),
            mutability);

    public ForeachAnalysisResult AnalyzeForeach(
        ForeachStatement foreachStatement,
        TypeEnvironment variables,
        IReadOnlyDictionary<string, LocalMutability> mutability)
    {
        var foreachTypeEnvironment = variables.Clone();
        var foreachVariables = new Dictionary<string, string>(foreachTypeEnvironment.ToLegacyStrings(), StringComparer.Ordinal);
        var foreachMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
        var iterableExpression = foreachStatement.IterableExpression.SourceText;
        if (foreachStatement.IterableExpression is ScalarRangeExpressionNode rangeExpression)
        {
            if (foreachStatement.KeyBinding is not null)
            {
                diagnostics.Report(foreachStatement.Location, "Key/value foreach is not supported for scalar ranges.");
            }

            var rangeType = expressionTypeResolver.ResolveTypeRef(rangeExpression, variables)
                ?? new TypeRef.Named("int", []);
            AddForeachScalarRangeBindings(
                foreachStatement,
                foreachVariables,
                foreachTypeEnvironment,
                foreachMutability,
                rangeType);
        }
        else if (!TryResolveVariableType(iterableExpression, variables, out var iterableTypeRef))
        {
            diagnostics.Report(
                foreachStatement.Location,
                $"Cannot resolve foreach iterable '{iterableExpression}'. Use a visible local/global value, fixed array, scalar range like 0..10, or a type satisfying foreach requirements.");
        }
        else if (foreachStatement.KeyBinding is not null)
        {
            var iterableType = TypeRefFormatter.ToCxString(iterableTypeRef);
            if (TryResolveForeachTypes(
                iterableType,
                keyValue: true,
                out var keyValueElementType,
                out var keyValueKeyType))
            {
                if (keyValueKeyType is not null)
                {
                    var keyBindingType = TypeRefOrNull(foreachStatement.KeyBinding.TypeNode)
                        ?? typeRefParser.Parse(keyValueKeyType);
                    SetVariableType(foreachVariables, foreachTypeEnvironment, foreachStatement.KeyBinding.Name, keyBindingType);
                    foreachMutability[foreachStatement.KeyBinding.Name] = LocalMutability.ForeachKey;
                }

                AddForeachValueBindings(
                    foreachStatement,
                    foreachVariables,
                    foreachTypeEnvironment,
                    foreachMutability,
                    typeRefParser.Parse(keyValueElementType));
            }
            else
            {
                ReportForeachRequirementFailure(
                    foreachStatement,
                    iterableType,
                    SatisfiesRequirement(iterableType, "Contiguous"));
            }
        }
        else if (TryGetFixedArrayElementType(iterableTypeRef, out var arrayElementType))
        {
            AddForeachValueBindings(foreachStatement, foreachVariables, foreachTypeEnvironment, foreachMutability, arrayElementType);
        }
        else
        {
            var iterableType = TypeRefFormatter.ToCxString(iterableTypeRef);
            if (TryResolveForeachTypes(
                iterableType,
                keyValue: false,
                out var iteratorElementType,
                out _))
            {
                AddForeachValueBindings(
                    foreachStatement,
                    foreachVariables,
                    foreachTypeEnvironment,
                    foreachMutability,
                    typeRefParser.Parse(iteratorElementType));
            }
            else if (SatisfiesRequirement(iterableType, "Contiguous") is { Success: true } contiguous
                && contiguous.TypeBindings.TryGetValue("T", out var contiguousElementType))
            {
                AddForeachValueBindings(
                    foreachStatement,
                    foreachVariables,
                    foreachTypeEnvironment,
                    foreachMutability,
                    typeRefParser.Parse(contiguousElementType));
            }
            else if (SatisfiesRequirement(iterableType, "ContiguousRange") is { Success: true } range
                && range.TypeBindings.TryGetValue("T", out var rangeElementType))
            {
                AddForeachValueBindings(
                    foreachStatement,
                    foreachVariables,
                    foreachTypeEnvironment,
                    foreachMutability,
                    typeRefParser.Parse(rangeElementType));
            }
            else if (SatisfiesRequirement(iterableType, "Contiguous") is { } match && !match.Success)
            {
                ReportForeachRequirementFailure(foreachStatement, iterableType, match);
            }
        }

        return new ForeachAnalysisResult(foreachVariables, foreachTypeEnvironment, foreachMutability);
    }

    private void AddForeachValueBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        Dictionary<string, LocalMutability> mutability,
        TypeRef elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            var indexType = TypeRefOrNull(indexBinding.TypeNode)
                ?? new TypeRef.Named("usize", []);
            SetVariableType(variables, typeEnvironment, indexBinding.Name, indexType);
            mutability[indexBinding.Name] = LocalMutability.ForeachIndex;
        }

        var valueBindingType = TypeRefOrNull(foreachStatement.ValueBinding.TypeNode);
        var declaredElementType = valueBindingType ?? elementType;
        if (valueBindingType is not null
            && !typeCompatibility.CanAssign(
                TypeRefFormatter.ToCxString(valueBindingType),
                TypeRefFormatter.ToCxString(elementType),
                out var reason))
        {
            diagnostics.Report(
                foreachStatement.ValueBinding.Location,
                $"Type mismatch for foreach value '{foreachStatement.ValueBinding.Name}': {reason}.");
        }

        SetVariableType(variables, typeEnvironment, foreachStatement.ValueBinding.Name, declaredElementType);
        mutability[foreachStatement.ValueBinding.Name] = foreachStatement.ValueBinding.IsConst
            ? LocalMutability.ForeachConstItem
            : LocalMutability.Mutable;
    }

    private void AddForeachScalarRangeBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        Dictionary<string, LocalMutability> mutability,
        TypeRef elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            var indexType = TypeRefOrNull(indexBinding.TypeNode)
                ?? new TypeRef.Named("usize", []);
            SetVariableType(variables, typeEnvironment, indexBinding.Name, indexType);
            mutability[indexBinding.Name] = LocalMutability.ForeachIndex;
        }

        var declaredElementType = TypeRefOrNull(foreachStatement.ValueBinding.TypeNode) ?? elementType;
        SetVariableType(variables, typeEnvironment, foreachStatement.ValueBinding.Name, declaredElementType);
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
        TypeEnvironment variables,
        out TypeRef type)
    {
        return variables.TryGet(expression.Trim(), out type!);
    }

    private TypeRef? TypeRefOrNull(TypeNode? typeNode)
    {
        var type = typeNode.ToTypeRef(typeRefParser);
        return type is TypeRef.Unknown ? null : type;
    }

    private static void SetVariableType(
        Dictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        string name,
        TypeRef type)
    {
        variables[name] = TypeRefFormatter.ToCxString(type);
        typeEnvironment.Set(name, type);
    }

    private static bool TryGetFixedArrayElementType(TypeRef type, out TypeRef elementType)
    {
        elementType = null!;
        type = TypeRefFacts.UnwrapAlias(type);
        if (type is not TypeRef.FixedArray fixedArray)
        {
            return false;
        }

        elementType = fixedArray.Element;
        return true;
    }
}
