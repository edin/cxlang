using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class GenericTypeSubstitutionBuilder
{
    private static readonly TypeRefParser TypeParser = new(new ProgramNode(
        Location.Synthetic("<generic-type-substitutions>"),
        []));

    public static IReadOnlyDictionary<string, TypeRef> Build(IReadOnlyDictionary<string, string> substitutions) =>
        substitutions.ToDictionary(
            pair => pair.Key,
            pair => TypeParser.Parse(pair.Value),
            StringComparer.Ordinal);

    public static TypeRef? ParseType(string? type) =>
        type is null ? null : TypeParser.Parse(type);
}
