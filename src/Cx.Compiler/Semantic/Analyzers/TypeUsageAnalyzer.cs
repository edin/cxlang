using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class TypeUsageAnalyzer(
    DiagnosticBag diagnostics,
    ProgramNode program,
    RequirementMatcher requirementMatcher,
    Func<TypeNode?, string> typeText,
    Func<string, bool> isKnownTypeName,
    Func<string, string?> findAliasSuggestionForType,
    Func<string, string?> findPartialImportSuggestionForType,
    Func<string, string?> findImportSuggestionForType)
{
    public void Analyze(
        TypeNode? typeNode,
        Location location,
        IReadOnlyList<string> inScopeTypeParameters) =>
        Analyze(typeText(typeNode), location, inScopeTypeParameters);

    public void Analyze(
        string type,
        Location location,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        foreach (var typeName in FindTypeNames(type)
            .Where(typeName => !inScopeTypeParameters.Contains(typeName, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal))
        {
            if (isKnownTypeName(typeName))
            {
                continue;
            }

            if (findAliasSuggestionForType(typeName) is { } aliasSuggestion)
            {
                diagnostics.Report(location, $"Unknown type '{typeName}'. Did you mean '{aliasSuggestion}'?");
            }
            else if (findPartialImportSuggestionForType(typeName) is { } partialSuggestion)
            {
                diagnostics.Report(location, $"Unknown type '{typeName}'. Did you mean '{partialSuggestion}'?");
            }
            else if (findImportSuggestionForType(typeName) is { } suggestion)
            {
                diagnostics.Report(location, $"Unknown type '{typeName}'. Did you mean to import {suggestion}?");
            }
        }

        foreach (var use in FindGenericStructUses(type))
        {
            var definition = program.Structs.FirstOrDefault(structNode =>
                structNode.Name == use.Name
                && structNode.TypeParameters.Count == use.Arguments.Count);
            if (definition is null || definition.GenericConstraints.Count == 0)
            {
                continue;
            }

            var substitutions = definition.TypeParameters
                .Zip(use.Arguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var constraint in definition.GenericConstraints)
            {
                if (!substitutions.TryGetValue(constraint.TypeParameter, out var concreteType))
                {
                    continue;
                }

                if (inScopeTypeParameters.Contains(concreteType, StringComparer.Ordinal))
                {
                    continue;
                }

                foreach (var requirement in constraint.Requirements)
                {
                    var arguments = requirement.TypeArgumentNodes
                        .Select(typeText)
                        .Select(argument => GenericTypeStringRewriter.Substitute(argument, substitutions))
                        .ToList();
                    var match = requirementMatcher.Match(concreteType, requirement.Name, arguments);
                    if (match.Success)
                    {
                        continue;
                    }

                    diagnostics.Report(
                        location,
                        $"Type '{concreteType}' used for '{definition.Name}.{constraint.TypeParameter}' does not satisfy requirement '{requirement.Name}': {string.Join(" ", match.Failures)}");
                }
            }
        }
    }

    private static IReadOnlyList<GenericStructUse> FindGenericStructUses(string type)
    {
        var uses = new List<GenericStructUse>();
        CollectGenericStructUses(TypeSyntaxParser.Parse(type), uses);
        return uses;
    }

    private static IReadOnlyList<string> FindTypeNames(string type)
    {
        var names = new List<string>();
        CollectTypeNames(TypeSyntaxParser.Parse(type), names);
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectGenericStructUses(TypeSyntaxNode? syntax, List<GenericStructUse> uses)
    {
        switch (syntax)
        {
            case null:
                break;
            case GenericTypeSyntaxNode generic:
                uses.Add(new GenericStructUse(
                    TypeSyntaxFormatter.ToCxString(generic.Target),
                    generic.Arguments.Select(TypeSyntaxFormatter.ToCxString).ToList()));
                CollectGenericStructUses(generic.Target, uses);
                foreach (var argument in generic.Arguments)
                {
                    CollectGenericStructUses(argument, uses);
                }
                break;
            case PointerTypeSyntaxNode pointer:
                CollectGenericStructUses(pointer.Element, uses);
                break;
            case FixedArrayTypeSyntaxNode fixedArray:
                CollectGenericStructUses(fixedArray.Element, uses);
                break;
            case FunctionTypeSyntaxNode function:
                foreach (var parameter in function.Parameters)
                {
                    CollectGenericStructUses(parameter, uses);
                }
                CollectGenericStructUses(function.ReturnType, uses);
                break;
        }
    }

    private static void CollectTypeNames(TypeSyntaxNode? syntax, List<string> names)
    {
        switch (syntax)
        {
            case null:
                break;
            case NamedTypeSyntaxNode named:
                names.Add(NormalizeTypeName(named.Name));
                break;
            case GenericTypeSyntaxNode generic:
                CollectTypeNames(generic.Target, names);
                foreach (var argument in generic.Arguments)
                {
                    CollectTypeNames(argument, names);
                }
                break;
            case PointerTypeSyntaxNode pointer:
                CollectTypeNames(pointer.Element, names);
                break;
            case FixedArrayTypeSyntaxNode fixedArray:
                CollectTypeNames(fixedArray.Element, names);
                break;
            case FunctionTypeSyntaxNode function:
                foreach (var parameter in function.Parameters)
                {
                    CollectTypeNames(parameter, names);
                }
                CollectTypeNames(function.ReturnType, names);
                break;
        }
    }

    private static string NormalizeTypeName(string type)
    {
        type = BuiltinTypes.Normalize(type);
        return BuiltinTypes.IsBuiltin(type) ? string.Empty : type;
    }

    private sealed record GenericStructUse(string Name, IReadOnlyList<string> Arguments);
}
