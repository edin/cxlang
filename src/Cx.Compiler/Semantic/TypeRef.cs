using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal abstract record TypeRef
{
    public sealed record Unknown : TypeRef;

    public sealed record Null : TypeRef;

    public sealed record Named(string Name, IReadOnlyList<TypeRef> Arguments) : TypeRef;

    public sealed record Pointer(TypeRef Element) : TypeRef;

    public sealed record FixedArray(TypeRef Element, string Length) : TypeRef;

    public sealed record Function(IReadOnlyList<TypeRef> Parameters, TypeRef ReturnType) : TypeRef;
}

internal sealed class TypeRefParser(ProgramNode program)
{
    private readonly IReadOnlyDictionary<string, string> _aliases = program.TypeAliases
        .GroupBy(alias => alias.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().TargetType, StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _enumNames = program.Enums
        .Where(enumNode => !string.IsNullOrWhiteSpace(enumNode.Name))
        .Select(enumNode => enumNode.Name)
        .Concat(program.CDeclarations
            .SelectMany(declaration => declaration.Enums)
            .Where(enumNode => !string.IsNullOrWhiteSpace(enumNode.Name))
            .Select(enumNode => enumNode.Name))
        .ToHashSet(StringComparer.Ordinal);

    public bool IsEnumName(string name) => _enumNames.Contains(name);

    public TypeRef Parse(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return new TypeRef.Unknown();
        }

        return Parse(type.Trim(), []);
    }

    private TypeRef Parse(string type, HashSet<string> resolvingAliases)
    {
        type = type.Trim();
        if (type.Length == 0)
        {
            return new TypeRef.Unknown();
        }

        if (string.Equals(type, "null", StringComparison.Ordinal))
        {
            return new TypeRef.Null();
        }

        var pointerDepth = 0;
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerDepth++;
            type = type[..^1].TrimEnd();
        }

        var result = ParseWithoutPointerSuffix(type, resolvingAliases);
        for (var i = 0; i < pointerDepth; i++)
        {
            result = new TypeRef.Pointer(result);
        }

        return result;
    }

    private TypeRef ParseWithoutPointerSuffix(string type, HashSet<string> resolvingAliases)
    {
        if (TryParseFunction(type, resolvingAliases, out var function))
        {
            return function;
        }

        if (TryParseFixedArray(type, resolvingAliases, out var fixedArray))
        {
            return fixedArray;
        }

        if (_aliases.TryGetValue(type, out var targetType) && resolvingAliases.Add(type))
        {
            return Parse(targetType, resolvingAliases);
        }

        if (TryParseGenericUse(type, out var name, out var arguments))
        {
            var parsedArguments = arguments
                .Select(argument => Parse(argument, new HashSet<string>(resolvingAliases, StringComparer.Ordinal)))
                .ToList();
            return new TypeRef.Named(name, parsedArguments);
        }

        return new TypeRef.Named(type, []);
    }

    private bool TryParseFunction(string type, HashSet<string> resolvingAliases, out TypeRef function)
    {
        function = new TypeRef.Unknown();
        if (!type.StartsWith("fn(", StringComparison.Ordinal))
        {
            return false;
        }

        var close = FindMatching(type, 2, '(', ')');
        if (close < 0 || close + 2 >= type.Length || type[close + 1] != '-' || type[close + 2] != '>')
        {
            return false;
        }

        var parameters = SplitTopLevel(type[3..close])
            .Select(parameter => Parse(parameter, new HashSet<string>(resolvingAliases, StringComparer.Ordinal)))
            .ToList();
        var returnType = Parse(type[(close + 3)..], resolvingAliases);
        function = new TypeRef.Function(parameters, returnType);
        return true;
    }

    private bool TryParseFixedArray(string type, HashSet<string> resolvingAliases, out TypeRef fixedArray)
    {
        fixedArray = new TypeRef.Unknown();
        if (!type.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var open = FindLastTopLevelOpenBracket(type);
        if (open < 0)
        {
            return false;
        }

        var length = type[(open + 1)..^1].Trim();
        if (length.Length == 0)
        {
            return false;
        }

        fixedArray = new TypeRef.FixedArray(Parse(type[..open], resolvingAliases), length);
        return true;
    }

    private static bool TryParseGenericUse(string type, out string name, out IReadOnlyList<string> arguments)
    {
        name = string.Empty;
        arguments = [];
        var open = type.IndexOf('<', StringComparison.Ordinal);
        if (open <= 0 || !type.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var close = FindMatching(type, open, '<', '>');
        if (close != type.Length - 1)
        {
            return false;
        }

        name = type[..open].Trim();
        arguments = SplitTopLevel(type[(open + 1)..close]);
        return name.Length > 0 && arguments.Count > 0;
    }

    private static int FindLastTopLevelOpenBracket(string type)
    {
        var open = -1;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var i = 0; i < type.Length; i++)
        {
            switch (type[i])
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
                case '[' when angleDepth == 0 && parenDepth == 0 && bracketDepth == 0:
                    open = i;
                    bracketDepth++;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
            }
        }

        return open;
    }

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

    private static IReadOnlyList<string> SplitTopLevel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var values = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
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
                case ',' when angleDepth == 0 && parenDepth == 0 && bracketDepth == 0:
                    values.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        values.Add(text[start..].Trim());
        return values.Where(value => value.Length > 0).ToList();
    }
}

internal sealed class TypeCompatibility(TypeRefParser parser)
{
    public bool CanAssign(string targetType, string? sourceType, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return true;
        }

        var target = parser.Parse(targetType);
        var source = parser.Parse(sourceType);
        if (IsUnknown(target) || IsUnknown(source))
        {
            return true;
        }

        if (IsAssignable(target, source))
        {
            return true;
        }

        reason = $"cannot assign '{Format(source)}' to '{Format(target)}'";
        return false;
    }

    private bool IsAssignable(TypeRef target, TypeRef source)
    {
        if (target is TypeRef.Unknown || source is TypeRef.Unknown)
        {
            return true;
        }

        if (target is TypeRef.Named { Name: "any" } || source is TypeRef.Named { Name: "any" })
        {
            return true;
        }

        if (source is TypeRef.Null)
        {
            return target is TypeRef.Pointer;
        }

        if (target is TypeRef.Pointer targetPointer && source is TypeRef.Pointer sourcePointer)
        {
            return IsAssignablePointer(targetPointer.Element, sourcePointer.Element);
        }

        if (target is TypeRef.Named targetNamed && source is TypeRef.Named sourceNamed)
        {
            if (IsIntegerCompatible(targetNamed.Name) && IsIntegerCompatible(sourceNamed.Name))
            {
                return true;
            }

            return string.Equals(targetNamed.Name, sourceNamed.Name, StringComparison.Ordinal)
                && targetNamed.Arguments.Count == sourceNamed.Arguments.Count
                && targetNamed.Arguments.Zip(sourceNamed.Arguments).All(pair => IsAssignable(pair.First, pair.Second));
        }

        if (target is TypeRef.FixedArray targetArray && source is TypeRef.FixedArray sourceArray)
        {
            return string.Equals(targetArray.Length, sourceArray.Length, StringComparison.Ordinal)
                && IsAssignable(targetArray.Element, sourceArray.Element);
        }

        if (target is TypeRef.Function targetFunction && source is TypeRef.Function sourceFunction)
        {
            return targetFunction.Parameters.Count == sourceFunction.Parameters.Count
                && targetFunction.Parameters.Zip(sourceFunction.Parameters).All(pair => IsAssignable(pair.First, pair.Second))
                && IsAssignable(targetFunction.ReturnType, sourceFunction.ReturnType);
        }

        return false;
    }

    private bool IsAssignablePointer(TypeRef target, TypeRef source)
    {
        if (IsAssignable(target, source))
        {
            return true;
        }

        if (IsVoidPointerElement(target) || IsVoidPointerElement(source))
        {
            return true;
        }

        if (target is TypeRef.Named { Name: var targetName, Arguments: { Count: 0 } }
            && source is TypeRef.Named { Name: var sourceName, Arguments: { Count: 0 } }
            && targetName.StartsWith("const ", StringComparison.Ordinal)
            && string.Equals(targetName["const ".Length..], sourceName, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsVoidPointerElement(TypeRef type) =>
        type is TypeRef.Named { Name: "void" or "const void", Arguments: { Count: 0 } };

    private static bool IsUnknown(TypeRef type) => type is TypeRef.Unknown;

    private bool IsIntegerCompatible(string name)
    {
        name = StripConst(name.Trim());
        return IsNumeric(name) || parser.IsEnumName(name);
    }

    private static bool IsNumeric(string name)
    {
        name = StripConst(name.Trim());
        return name is
            "char" or
            "signed char" or
            "unsigned char" or
            "short" or
            "unsigned short" or
            "int" or
            "unsigned int" or
            "long" or
            "unsigned long" or
            "long long" or
            "unsigned long long" or
            "int8_t" or
            "uint8_t" or
            "int16_t" or
            "uint16_t" or
            "int32_t" or
            "uint32_t" or
            "int64_t" or
            "uint64_t" or
            "float" or
            "double" or
            "long double" or
            "size_t" or
            "clock_t";
    }

    private static string StripConst(string name) =>
        name.StartsWith("const ", StringComparison.Ordinal)
            ? name["const ".Length..].TrimStart()
            : name;

    private static string Format(TypeRef type) => type switch
    {
        TypeRef.Unknown => "unknown",
        TypeRef.Null => "null",
        TypeRef.Named named when named.Arguments.Count == 0 => named.Name,
        TypeRef.Named named => $"{named.Name}<{string.Join(", ", named.Arguments.Select(Format))}>",
        TypeRef.Pointer pointer => $"{Format(pointer.Element)}*",
        TypeRef.FixedArray array => $"{Format(array.Element)}[{array.Length}]",
        TypeRef.Function function => $"fn({string.Join(", ", function.Parameters.Select(Format))}) -> {Format(function.ReturnType)}",
        _ => "unknown",
    };
}
