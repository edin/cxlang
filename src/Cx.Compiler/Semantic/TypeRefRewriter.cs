namespace Cx.Compiler.Semantic;

internal static class TypeRefRewriter
{
    public static TypeRef Substitute(
        TypeRef type,
        IReadOnlyDictionary<string, TypeRef> substitutions) =>
        Rewrite(type, named =>
        {
            if (named.Arguments.Count == 0 && substitutions.TryGetValue(named.Name, out var replacement))
            {
                return replacement;
            }

            return null;
        });

    public static TypeRef SubstituteSelf(TypeRef type, TypeRef selfType) =>
        Rewrite(type, named =>
            named is { Name: "Self", Arguments.Count: 0 }
                ? selfType
                : null);

    public static TypeRef RewriteConcreteGenericNames(
        TypeRef type,
        Func<string, IReadOnlyList<string>, string> lowerGenericName,
        IReadOnlySet<string> concreteTypeNames) =>
        Rewrite(type, named =>
        {
            if (named.Arguments.Count == 0)
            {
                return null;
            }

            var concreteName = lowerGenericName(
                named.Name,
                named.Arguments.Select(TypeRefFormatter.ToCxString).ToList());
            return concreteTypeNames.Contains(concreteName)
                ? new TypeRef.Named(concreteName, [])
                : null;
        });

    private static TypeRef Rewrite(TypeRef type, Func<TypeRef.Named, TypeRef?> rewriteNamed) =>
        type switch
        {
            TypeRef.Named named => RewriteNamed(named, rewriteNamed),
            TypeRef.Alias alias => new TypeRef.Alias(alias.Name, Rewrite(alias.Target, rewriteNamed)),
            TypeRef.Pointer pointer => new TypeRef.Pointer(Rewrite(pointer.Element, rewriteNamed)),
            TypeRef.FixedArray array => new TypeRef.FixedArray(Rewrite(array.Element, rewriteNamed), array.Length),
            TypeRef.Function function => new TypeRef.Function(
                function.Parameters.Select(parameter => Rewrite(parameter, rewriteNamed)).ToList(),
                Rewrite(function.ReturnType, rewriteNamed)),
            _ => type,
        };

    private static TypeRef RewriteNamed(TypeRef.Named named, Func<TypeRef.Named, TypeRef?> rewriteNamed)
    {
        var rewrittenArguments = named.Arguments
            .Select(argument => Rewrite(argument, rewriteNamed))
            .ToList();
        var rewrittenNamed = rewrittenArguments.SequenceEqual(named.Arguments)
            ? named
            : new TypeRef.Named(named.Name, rewrittenArguments);

        return rewriteNamed(rewrittenNamed) ?? rewrittenNamed;
    }
}
