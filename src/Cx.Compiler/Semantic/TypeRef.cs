using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal abstract record TypeRef
{
    public sealed record Unknown : TypeRef;

    public sealed record Null : TypeRef;

    public sealed record Named(string Name, IReadOnlyList<TypeRef> Arguments) : TypeRef;

    public sealed record Alias(string Name, TypeRef Target) : TypeRef;

    public sealed record Pointer(TypeRef Element) : TypeRef;

    public sealed record FixedArray(TypeRef Element, string Length) : TypeRef;

    public sealed record Function(IReadOnlyList<TypeRef> Parameters, TypeRef ReturnType, bool IsVariadic = false) : TypeRef;
}

internal sealed class TypeRefParser(ProgramNode program)
{
    private readonly IReadOnlyDictionary<string, TypeNode?> _aliasNodes = program.TypeAliases
        .GroupBy(alias => alias.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().TargetTypeNode, StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _enumNames = program.Enums
        .Where(enumNode => !string.IsNullOrWhiteSpace(enumNode.Name))
        .Select(enumNode => enumNode.Name)
        .Concat(program.CDeclarations
            .SelectMany(declaration => declaration.Enums)
            .Where(enumNode => !string.IsNullOrWhiteSpace(enumNode.Name))
            .Select(enumNode => enumNode.Name))
        .ToHashSet(StringComparer.Ordinal);

    public bool IsEnumName(string name) => _enumNames.Contains(name);

    public TypeRef Parse(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return new TypeRef.Unknown();
        }

        if (typeNode.Semantic.Type is { } semanticType)
        {
            return semanticType;
        }

        if (string.IsNullOrWhiteSpace(typeNode.TypeName))
        {
            return new TypeRef.Unknown();
        }

        if (typeNode.Syntax is null)
        {
            throw new InvalidOperationException($"TypeNode '{typeNode.TypeName}' has no parsed type syntax.");
        }

        return Parse(typeNode.Syntax, []);
    }

    public TypeRef Parse(string? type)
    {
        var syntax = TypeSyntaxParser.Parse(type);
        if (syntax is null)
        {
            return new TypeRef.Unknown();
        }

        return Parse(syntax, []);
    }

    private TypeRef Parse(TypeSyntaxNode syntax, HashSet<string> resolvingAliases) =>
        syntax switch
        {
            NamedTypeSyntaxNode named => ParseNamedSyntax(named, resolvingAliases),
            GenericTypeSyntaxNode generic => ParseGenericSyntax(generic, resolvingAliases),
            PointerTypeSyntaxNode pointer => new TypeRef.Pointer(Parse(pointer.Element, resolvingAliases)),
            FixedArrayTypeSyntaxNode array => new TypeRef.FixedArray(Parse(array.Element, resolvingAliases), array.Length),
            FunctionTypeSyntaxNode function => new TypeRef.Function(
                function.Parameters
                    .Select(parameter => Parse(parameter, new HashSet<string>(resolvingAliases, StringComparer.Ordinal)))
                    .ToList(),
                Parse(function.ReturnType, resolvingAliases),
                function.IsVariadic),
            _ => new TypeRef.Unknown(),
        };

    private TypeRef ParseNamedSyntax(NamedTypeSyntaxNode named, HashSet<string> resolvingAliases)
    {
        if (string.IsNullOrWhiteSpace(named.Name))
        {
            return new TypeRef.Unknown();
        }

        if (string.Equals(named.Name, "null", StringComparison.Ordinal))
        {
            return new TypeRef.Null();
        }

        if (!_aliasNodes.TryGetValue(named.Name, out var targetType))
        {
            return new TypeRef.Named(named.Name, []);
        }

        if (!resolvingAliases.Add(named.Name))
        {
            return new TypeRef.Named(named.Name, []);
        }

        var target = ParseAliasTarget(named.Name, targetType, resolvingAliases);
        resolvingAliases.Remove(named.Name);
        return new TypeRef.Alias(named.Name, target);
    }

    private TypeRef ParseAliasTarget(
        string aliasName,
        TypeNode? targetType,
        HashSet<string> resolvingAliases)
    {
        if (targetType is null || string.IsNullOrWhiteSpace(targetType.TypeName))
        {
            return new TypeRef.Unknown();
        }

        if (targetType.Semantic.Type is { } semanticType)
        {
            return semanticType;
        }

        if (targetType.Syntax is null)
        {
            throw new InvalidOperationException($"Type alias '{aliasName}' target type '{targetType.TypeName}' has no parsed type syntax.");
        }

        return Parse(targetType.Syntax, resolvingAliases);
    }

    private TypeRef ParseGenericSyntax(GenericTypeSyntaxNode generic, HashSet<string> resolvingAliases)
    {
        var name = TypeSyntaxFormatter.ToCxString(generic.Target);
        return new TypeRef.Named(
            name,
            generic.Arguments
                .Select(argument => Parse(argument, new HashSet<string>(resolvingAliases, StringComparer.Ordinal)))
                .ToList());
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

        return CanAssign(parser.Parse(targetType), parser.Parse(sourceType), out reason);
    }

    public bool CanAssign(string targetType, TypeRef? sourceType, out string reason) =>
        CanAssign(parser.Parse(targetType), sourceType, out reason);

    public bool CanAssign(TypeRef targetType, TypeRef? sourceType, out string reason)
    {
        reason = string.Empty;
        if (sourceType is null)
        {
            return true;
        }

        var target = targetType;
        var source = sourceType;
        if (IsUnknown(target) || IsUnknown(source))
        {
            return true;
        }

        if (IsAssignable(target, source))
        {
            return true;
        }

        reason = $"cannot assign '{TypeRefFormatter.ToCxString(source)}' to '{TypeRefFormatter.ToCxString(target)}'";
        return false;
    }

    private bool IsAssignable(TypeRef target, TypeRef source)
    {
        target = UnwrapAlias(target);
        source = UnwrapAlias(source);

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
                && targetFunction.IsVariadic == sourceFunction.IsVariadic
                && targetFunction.Parameters.Zip(sourceFunction.Parameters).All(pair => IsAssignable(pair.First, pair.Second))
                && IsAssignable(targetFunction.ReturnType, sourceFunction.ReturnType);
        }

        return false;
    }

    private bool IsAssignablePointer(TypeRef target, TypeRef source)
    {
        target = UnwrapAlias(target);
        source = UnwrapAlias(source);

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
        UnwrapAlias(type) is TypeRef.Named { Name: "void" or "const void", Arguments: { Count: 0 } };

    private static bool IsUnknown(TypeRef type) => UnwrapAlias(type) is TypeRef.Unknown;

    private static TypeRef UnwrapAlias(TypeRef type)
    {
        while (type is TypeRef.Alias alias)
        {
            type = alias.Target;
        }

        return type;
    }

    private bool IsIntegerCompatible(string name)
    {
        name = StripConst(name.Trim());
        return BuiltinTypes.IsNumeric(name) || parser.IsEnumName(name);
    }

    private static string StripConst(string name) =>
        name.StartsWith("const ", StringComparison.Ordinal)
            ? name["const ".Length..].TrimStart()
            : name;

}
