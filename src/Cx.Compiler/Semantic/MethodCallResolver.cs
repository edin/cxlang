using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed record ResolvedMethodCall(
    string DisplayName,
    ResolvedMethod Method,
    bool SkipSelf);

internal sealed class MethodCallResolver(ProgramNode program, TypeSystem typeSystem)
{
    public ResolvedMethodCall? Resolve(
        MemberExpressionNode member,
        IReadOnlyList<string> typeArguments,
        int argumentCount,
        IReadOnlyDictionary<string, string> variables)
    {
        var targetName = GetQualifiedName(member.Target);
        if (targetName is null)
        {
            return null;
        }

        if (!variables.TryGetValue(targetName, out var targetType))
        {
            var staticReceiverType = BuildStaticReceiverType(targetName, typeArguments);
            return typeSystem.FindMethod(staticReceiverType, member.MemberName, isStatic: true, argumentCount) is { } staticMethod
                ? new ResolvedMethodCall($"{staticReceiverType}.{member.MemberName}", staticMethod, SkipSelf: false)
                : null;
        }

        var instanceReceiverType = StripPointer(ResolveAlias(targetType));
        return typeSystem.FindMethod(instanceReceiverType, member.MemberName, isStatic: false, argumentCount) is { } instanceMethod
            ? new ResolvedMethodCall($"{instanceReceiverType}.{member.MemberName}", instanceMethod, SkipSelf: true)
            : null;
    }

    private string BuildStaticReceiverType(string targetName, IReadOnlyList<string> typeArguments)
    {
        if (typeArguments.Count == 0)
        {
            return targetName;
        }

        var typeParameterCount = program.Structs
            .FirstOrDefault(structNode => string.Equals(structNode.Name, targetName, StringComparison.Ordinal))
            ?.TypeParameters.Count
            ?? program.TypeAdapters
                .FirstOrDefault(adapter => string.Equals(adapter.Name, targetName, StringComparison.Ordinal))
                ?.TypeParameters.Count
            ?? 0;
        return typeParameterCount == typeArguments.Count
            ? $"{targetName}<{string.Join(",", typeArguments)}>"
            : targetName;
    }

    private string ResolveAlias(string type)
    {
        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var aliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TypeText(group.First().TargetTypeNode), StringComparer.Ordinal);
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

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private static string TypeText(TypeNode? typeNode) => typeNode?.TypeName ?? string.Empty;
}
