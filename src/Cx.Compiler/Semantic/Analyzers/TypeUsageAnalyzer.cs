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

        for (var i = 0; i < type.Length; i++)
        {
            if (!IsIdentifierStart(type[i]))
            {
                continue;
            }

            var nameStart = i;
            while (i < type.Length && IsIdentifierPart(type[i]))
            {
                i++;
            }

            if (i >= type.Length || type[i] != '<')
            {
                continue;
            }

            var close = FindMatchingGenericClose(type, i);
            if (close < 0)
            {
                continue;
            }

            var name = type[nameStart..i];
            var arguments = SplitGenericArguments(type[(i + 1)..close]);
            uses.Add(new GenericStructUse(name, arguments));
            foreach (var argument in arguments)
            {
                uses.AddRange(FindGenericStructUses(argument));
            }

            i = close;
        }

        return uses;
    }

    private static IReadOnlyList<string> FindTypeNames(string type)
    {
        type = StripArraySuffix(StripPointer(type.Trim()));
        if (type.Length == 0)
        {
            return [];
        }

        if (TryParseFunctionType(type, out var parameterTypes, out var returnType, out _))
        {
            return parameterTypes
                .SelectMany(FindTypeNames)
                .Concat(FindTypeNames(returnType))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (TryParseGenericUse(type, out var genericName, out var arguments))
        {
            return new[] { NormalizeTypeName(genericName) }
                .Concat(arguments.SelectMany(FindTypeNames))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var normalized = NormalizeTypeName(type);
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : [normalized];
    }

    private static string NormalizeTypeName(string type)
    {
        type = StripArraySuffix(StripPointer(type.Trim()));
        if (type.StartsWith("const ", StringComparison.Ordinal))
        {
            type = type["const ".Length..].TrimStart();
        }

        return IsBuiltInTypeName(type) ? string.Empty : type;
    }

    private static bool TryParseGenericUse(string type, out string name, out IReadOnlyList<string> arguments)
    {
        type = StripPointer(type.Trim());
        var open = type.IndexOf('<');
        if (open <= 0 || !type.EndsWith(">", StringComparison.Ordinal))
        {
            name = string.Empty;
            arguments = [];
            return false;
        }

        var close = FindMatchingGenericClose(type, open);
        if (close != type.Length - 1)
        {
            name = string.Empty;
            arguments = [];
            return false;
        }

        name = type[..open].Trim();
        arguments = SplitGenericArguments(type[(open + 1)..^1]);
        return name.Length > 0;
    }

    private static bool TryParseFunctionType(
        string type,
        out IReadOnlyList<string> parameterTypes,
        out string returnType,
        out bool isVariadic)
    {
        parameterTypes = [];
        returnType = string.Empty;
        isVariadic = false;
        type = type.Trim();
        if (!type.StartsWith("fn(", StringComparison.Ordinal))
        {
            return false;
        }

        var close = FindMatching(type, 2, '(', ')');
        if (close < 0)
        {
            return false;
        }

        var rest = type[(close + 1)..].TrimStart();
        if (!rest.StartsWith("->", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = SplitGenericArguments(type[3..close]).ToList();
        if (parameters.LastOrDefault() == "...")
        {
            isVariadic = true;
            parameters.RemoveAt(parameters.Count - 1);
        }

        parameterTypes = parameters;
        returnType = rest[2..].Trim();
        return true;
    }

    private static int FindMatchingGenericClose(string type, int openIndex) =>
        FindMatching(type, openIndex, '<', '>');

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
                continue;
            }

            if (text[i] != close)
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitGenericArguments(string argumentsText)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return [];
        }

        var arguments = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var i = 0; i < argumentsText.Length; i++)
        {
            switch (argumentsText[i])
            {
                case '<': angleDepth++; break;
                case '>': angleDepth--; break;
                case '(': parenDepth++; break;
                case ')': parenDepth--; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
            }

            if (argumentsText[i] != ',' || angleDepth != 0 || parenDepth != 0 || bracketDepth != 0)
            {
                continue;
            }

            arguments.Add(argumentsText[start..i].Trim());
            start = i + 1;
        }

        arguments.Add(argumentsText[start..].Trim());
        return arguments;
    }

    private static string StripPointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    private static string StripArraySuffix(string type)
    {
        while (type.EndsWith("]", StringComparison.Ordinal))
        {
            var open = type.LastIndexOf('[');
            if (open < 0)
            {
                break;
            }

            type = type[..open].TrimEnd();
        }

        return type;
    }

    private static bool IsBuiltInTypeName(string name) =>
        name is
            "void" or
            "char" or
            "short" or
            "int" or
            "long" or
            "long long" or
            "float" or
            "double" or
            "bool";

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private sealed record GenericStructUse(string Name, IReadOnlyList<string> Arguments);
}
