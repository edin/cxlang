using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed record MatchArmBinding(MatchArmNode Arm, string? Type);

internal sealed class MatchSemanticAnalyzer(
    DiagnosticBag diagnostics,
    ProgramNode program,
    ExpressionTypeResolver expressionTypeResolver,
    Func<TypeNode?, string> typeText,
    Func<string, bool> isKnownTypeName)
{
    public IReadOnlyList<MatchArmBinding> AnalyzeMatch(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = expressionTypeResolver.Resolve(matchStatement.Expression, variables);
        TaggedUnionNode? matchedTaggedUnion = null;
        InterfaceNode? matchedInterface = null;
        if (matchExpressionType is not null
            && program.TaggedUnions.FirstOrDefault(union => union.Name == StripPointer(ResolveAlias(matchExpressionType))) is { IsRaw: true })
        {
            diagnostics.Report(matchStatement.Location, $"Cannot pattern match raw union type '{matchExpressionType}'.");
        }
        else if (ResolveMatchedTaggedUnion(matchStatement, variables) is { } taggedUnion)
        {
            matchedTaggedUnion = taggedUnion;
            AnalyzeTaggedUnionMatchArms(matchStatement, taggedUnion);
        }
        else if (ResolveMatchedInterface(matchStatement, variables) is { } interfaceNode)
        {
            matchedInterface = interfaceNode;
            AnalyzeInterfaceMatchArms(matchStatement, interfaceNode);
        }

        return matchStatement.Arms
            .Select(arm => new MatchArmBinding(arm, ResolveArmBindingType(arm, matchedTaggedUnion, matchedInterface)))
            .ToList();
    }

    private string? ResolveArmBindingType(
        MatchArmNode arm,
        TaggedUnionNode? matchedTaggedUnion,
        InterfaceNode? matchedInterface)
    {
        if (arm.BindingName is null || arm.Pattern == "_")
        {
            return null;
        }

        if (matchedTaggedUnion?.Variants.FirstOrDefault(variant => variant.Name == arm.Pattern) is { } variant)
        {
            return typeText(variant.TypeNode);
        }

        if (matchedInterface is not null && InterfaceImplementationExists(arm.Pattern, matchedInterface.Name))
        {
            return arm.Pattern + "*";
        }

        return null;
    }

    private void AnalyzeTaggedUnionMatchArms(MatchStatement matchStatement, TaggedUnionNode taggedUnion)
    {
        var variantNames = taggedUnion.Variants
            .Select(variant => variant.Name)
            .ToHashSet(StringComparer.Ordinal);
        var seenPatterns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var arm in matchStatement.Arms)
        {
            if (arm.Pattern == "_")
            {
                continue;
            }

            if (!variantNames.Contains(arm.Pattern))
            {
                diagnostics.Report(
                    arm.Location,
                    $"Unknown match arm '{arm.Pattern}' for union '{taggedUnion.Name}'.");
                continue;
            }

            if (!seenPatterns.Add(arm.Pattern))
            {
                diagnostics.Report(
                    arm.Location,
                    $"Duplicate match arm '{arm.Pattern}' for union '{taggedUnion.Name}'.");
            }
        }

        if (IsMatchExhaustive(matchStatement, taggedUnion))
        {
            return;
        }

        var covered = matchStatement.Arms
            .Select(arm => arm.Pattern)
            .ToHashSet(StringComparer.Ordinal);
        var missing = taggedUnion.Variants
            .Select(variant => variant.Name)
            .Where(variantName => !covered.Contains(variantName))
            .ToList();
        diagnostics.Report(
            matchStatement.Location,
            $"Match on union '{taggedUnion.Name}' is not exhaustive. Missing variants: {string.Join(", ", missing)}.");
    }

    private static bool IsMatchExhaustive(MatchStatement matchStatement, TaggedUnionNode? taggedUnion)
    {
        if (matchStatement.Arms.Any(arm => arm.Pattern == "_"))
        {
            return true;
        }

        if (taggedUnion is null)
        {
            return false;
        }

        var covered = matchStatement.Arms
            .Select(arm => arm.Pattern)
            .ToHashSet(StringComparer.Ordinal);
        return taggedUnion.Variants.All(variant => covered.Contains(variant.Name));
    }

    private TaggedUnionNode? ResolveMatchedTaggedUnion(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = expressionTypeResolver.Resolve(matchStatement.Expression, variables);
        if (matchExpressionType is null)
        {
            return null;
        }

        var normalizedType = StripPointer(ResolveAlias(matchExpressionType));
        return program.TaggedUnions.FirstOrDefault(union =>
            string.Equals(union.Name, normalizedType, StringComparison.Ordinal));
    }

    private InterfaceNode? ResolveMatchedInterface(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = expressionTypeResolver.Resolve(matchStatement.Expression, variables);
        if (matchExpressionType is null)
        {
            return null;
        }

        var normalizedType = StripPointer(ResolveAlias(matchExpressionType));
        return program.Interfaces.FirstOrDefault(interfaceNode =>
            string.Equals(interfaceNode.Name, normalizedType, StringComparison.Ordinal));
    }

    private void AnalyzeInterfaceMatchArms(MatchStatement matchStatement, InterfaceNode interfaceNode)
    {
        var seenPatterns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arm in matchStatement.Arms)
        {
            if (arm.Pattern == "_")
            {
                continue;
            }

            if (!InterfaceImplementationExists(arm.Pattern, interfaceNode.Name))
            {
                var message = isKnownTypeName(arm.Pattern)
                    ? $"Type '{arm.Pattern}' does not implement interface '{interfaceNode.Name}'."
                    : $"Unknown match arm '{arm.Pattern}' for interface '{interfaceNode.Name}'.";
                diagnostics.Report(
                    arm.Location,
                    message);
                continue;
            }

            if (!seenPatterns.Add(arm.Pattern))
            {
                diagnostics.Report(
                    arm.Location,
                    $"Duplicate match arm '{arm.Pattern}' for interface '{interfaceNode.Name}'.");
            }
        }
    }

    private bool InterfaceImplementationExists(string structName, string interfaceName) =>
        program.Structs.Any(structNode =>
            string.Equals(structNode.Name, structName, StringComparison.Ordinal)
            && structNode.Requirements.Any(requirement =>
                string.Equals(requirement.Name, interfaceName, StringComparison.Ordinal)));

    private string ResolveAlias(string type)
    {
        var pointerSuffix = string.Empty;
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1].TrimEnd();
            pointerSuffix += "*";
        }

        var aliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => typeText(group.First().TargetTypeNode), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(type, out var targetType) && seen.Add(type))
        {
            type = targetType;
        }

        return type + pointerSuffix;
    }

    private static string StripPointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }
}
