using System.Text;
using System.Text.RegularExpressions;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.C;

internal static class CTypeLowerer
{
    public static string LowerType(
        string type,
        IReadOnlyList<TypeAdapterNode> typeAdapters,
        string? selfType = null)
    {
        type = SubstituteSelfType(type, selfType);
        type = ResolveAdapterStorageType(type, typeAdapters);

        if (TryParseFunctionType(type, out _, out _))
        {
            return type;
        }

        type = SplitArrayType(type).ElementType;

        var pointerSuffix = "";
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1];
        }

        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        if (genericStart < 0)
        {
            return StripModuleQualifier(type) + pointerSuffix;
        }

        var genericEnd = type.LastIndexOf('>');
        if (genericEnd < genericStart)
        {
            return type + pointerSuffix;
        }

        var name = StripModuleQualifier(type[..genericStart]);
        var arguments = type[(genericStart + 1)..genericEnd]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(argument => LowerType(argument, typeAdapters, selfType))
            .Select(SanitizeTypeName);

        return $"{name}_{string.Join("_", arguments)}{pointerSuffix}";
    }

    public static string LowerDeclaration(
        string type,
        string name,
        IReadOnlyList<TypeAdapterNode> typeAdapters,
        string? selfType = null)
    {
        type = SubstituteSelfType(type, selfType);
        if (TryParseFunctionType(type, out var parameters, out var returnType))
        {
            return $"{LowerType(returnType, typeAdapters, selfType)} (*{name})({string.Join(", ", parameters.Select(parameter => LowerFunctionTypeParameter(parameter, typeAdapters, selfType)))})";
        }

        var (elementType, suffix) = SplitArrayType(type);
        return $"{LowerType(elementType, typeAdapters, selfType)} {name}{suffix}";
    }

    public static string LowerParameterDeclaration(
        ParameterNode parameter,
        IReadOnlyList<TypeAdapterNode> typeAdapters,
        string? selfType = null) =>
        parameter.IsVariadic
            ? "..."
            : LowerDeclaration(parameter.Type, parameter.Name, typeAdapters, selfType);

    public static string ResolveAdapterStorageType(
        string type,
        IReadOnlyList<TypeAdapterNode> typeAdapters)
    {
        var prefix = "";
        if (type.StartsWith("const ", StringComparison.Ordinal))
        {
            prefix = "const ";
            type = type["const ".Length..].TrimStart();
        }

        var pointerSuffix = "";
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var adapterName = GetGenericBaseName(type) ?? type;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var adapter = typeAdapters.LastOrDefault(adapter => adapter.Name == adapterName);
            if (adapter is null || !seen.Add(adapter.Name))
            {
                return prefix + type + pointerSuffix;
            }

            var receiverArguments = TryParseGenericUse(type, out _, out var parsedArguments)
                ? parsedArguments
                : [];
            type = SubstituteAdapterBaseType(adapter, receiverArguments);
            adapterName = GetGenericBaseName(type) ?? type;
        }
    }

    public static string SubstituteSelfType(string type, string? selfType) =>
        string.IsNullOrWhiteSpace(selfType)
            ? type
            : Regex.Replace(type, @"\bSelf\b", selfType);

    public static string NormalizeType(string type) => type.TrimEnd('*').TrimEnd();

    public static string RemovePointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    public static string? GetGenericBaseName(string type)
    {
        type = type.TrimEnd('*').TrimEnd();
        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        return genericStart < 0 ? null : type[..genericStart];
    }

    public static bool TryParseGenericUse(
        string type,
        out string name,
        out IReadOnlyList<string> arguments)
    {
        name = string.Empty;
        arguments = [];
        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        var genericEnd = type.LastIndexOf('>');
        if (genericStart <= 0 || genericEnd < genericStart)
        {
            return false;
        }

        name = type[..genericStart].Trim();
        arguments = SplitGenericArguments(type[(genericStart + 1)..genericEnd]);
        return true;
    }

    public static bool TryParseFixedArrayType(
        string type,
        out string elementType,
        out string length)
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

    public static bool ReferencesCompositeType(
        string type,
        IReadOnlySet<string> compositeTypeNames,
        IReadOnlyList<TypeAdapterNode> typeAdapters)
    {
        if (TryParseFunctionType(type, out var parameters, out var returnType))
        {
            return parameters.Any(parameter => ReferencesCompositeType(parameter, compositeTypeNames, typeAdapters))
                || ReferencesCompositeType(returnType, compositeTypeNames, typeAdapters);
        }

        var loweredType = LowerType(type, typeAdapters).TrimEnd('*');
        var arrayStart = loweredType.IndexOf('[', StringComparison.Ordinal);
        if (arrayStart >= 0)
        {
            loweredType = loweredType[..arrayStart];
        }

        return compositeTypeNames.Contains(loweredType);
    }

    public static string SanitizeTypeName(string type) =>
        type
            .Replace("*", "_ptr", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("<", "_", StringComparison.Ordinal)
            .Replace(">", "", StringComparison.Ordinal)
            .Replace(",", "_", StringComparison.Ordinal);

    public static bool TryParseFunctionType(
        string type,
        out IReadOnlyList<string> parameters,
        out string returnType)
    {
        parameters = [];
        returnType = string.Empty;

        if (!type.StartsWith("fn(", StringComparison.Ordinal))
        {
            return false;
        }

        var closeParen = type.IndexOf(")->", StringComparison.Ordinal);
        if (closeParen < 0)
        {
            return false;
        }

        var parametersText = type[3..closeParen];
        parameters = string.IsNullOrWhiteSpace(parametersText)
            ? []
            : parametersText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        returnType = type[(closeParen + 3)..];
        return !string.IsNullOrWhiteSpace(returnType);
    }

    public static IReadOnlyList<string> SplitGenericArguments(string argumentsText)
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
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
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

    private static string LowerFunctionTypeParameter(
        string parameter,
        IReadOnlyList<TypeAdapterNode> typeAdapters,
        string? selfType = null) =>
        parameter.Trim() == "..."
            ? "..."
            : LowerType(parameter, typeAdapters, selfType);

    private static string SubstituteAdapterBaseType(
        TypeAdapterNode adapter,
        IReadOnlyList<string> receiverArguments)
    {
        if (adapter.TypeParameters.Count == 0 || adapter.TypeParameters.Count != receiverArguments.Count)
        {
            return adapter.BaseType;
        }

        var substitutions = adapter.TypeParameters
            .Zip(receiverArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        return SubstituteGenericType(adapter.BaseType, substitutions);
    }

    private static string SubstituteGenericType(
        string type,
        IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var (parameter, argument) in substitutions)
        {
            type = Regex.Replace(type, $@"\b{Regex.Escape(parameter)}\b", argument);
        }

        return type;
    }

    private static string StripModuleQualifier(string type)
    {
        var prefix = "";
        if (type.StartsWith("const ", StringComparison.Ordinal))
        {
            prefix = "const ";
            type = type["const ".Length..].TrimStart();
        }

        var dot = type.LastIndexOf('.');
        return prefix + (dot < 0 ? type : type[(dot + 1)..]);
    }

    private static (string ElementType, string Suffix) SplitArrayType(string type)
    {
        var suffixBuilder = new StringBuilder();

        while (type.EndsWith("]", StringComparison.Ordinal))
        {
            var openBracket = type.LastIndexOf('[');
            if (openBracket < 0)
            {
                break;
            }

            suffixBuilder.Insert(0, type[openBracket..]);
            type = type[..openBracket];
        }

        return (type, suffixBuilder.ToString());
    }
}
