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
        var targetName = ExpressionNameFacts.GetQualifiedName(member.Target);
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

        var instanceReceiverType = NormalizeInstanceReceiverType(targetType);
        return instanceReceiverType is not null
            && typeSystem.FindMethod(instanceReceiverType, member.MemberName, isStatic: false, argumentCount) is { } instanceMethod
            ? new ResolvedMethodCall($"{TypeRefFormatter.ToCxString(instanceReceiverType)}.{member.MemberName}", instanceMethod, SkipSelf: true)
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

    private TypeRef? NormalizeInstanceReceiverType(string type)
    {
        var parsed = typeSystem.Parse(type);
        if (parsed is TypeRef.Unknown)
        {
            return null;
        }

        return TypeRefFacts.StripPointersAndAliases(parsed);
    }

}
