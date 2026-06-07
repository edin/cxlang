using Cx.Compiler.Lowering;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class GenericCallResolver(
        IReadOnlyList<GenericCallInfo> calls,
        Func<ExpressionNode, string?> resolveExpressionType,
        Func<string, string, bool> canAssign)
    {
        public string RestoreSourceGenericType(string type)
        {
            var pointerSuffix = "";
            var normalized = type.Trim();
            while (normalized.EndsWith("*", StringComparison.Ordinal))
            {
                pointerSuffix += "*";
                normalized = normalized[..^1].TrimEnd();
            }

            foreach (var call in calls.Where(call => call.OwnerType is not null))
            {
                var concreteName = GenericTypeRewriter.LowerGenericTypeName(call.OwnerType!, call.TypeArguments);
                if (concreteName == normalized)
                {
                    return $"{call.OwnerType}<{string.Join(",", call.TypeArguments)}>{pointerSuffix}";
                }
            }

            return type;
        }

        public GenericCallInfo? FindIterator(string? sourceOwnerType, string concreteOwnerType) =>
            calls.FirstOrDefault(call =>
                !call.IsStatic
                && call.Name == "iterator"
                && MatchesGenericOwner(call, sourceOwnerType, concreteOwnerType));

        public GenericCallInfo? FindFreeExact(string name, IReadOnlyList<string> typeArguments) =>
            calls.FirstOrDefault(candidate =>
                candidate.OwnerType is null
                && candidate.Name == name
                && SameTypeArguments(candidate.TypeArguments, typeArguments));

        public GenericCallInfo? FindStaticExact(string calleeName, IReadOnlyList<string> typeArguments) =>
            calls.FirstOrDefault(candidate =>
                candidate.IsStatic
                && candidate.OwnerType is not null
                && calleeName == $"{candidate.OwnerType}.{candidate.Name}"
                && SameTypeArguments(candidate.TypeArguments, typeArguments));

        public GenericCallInfo? FindStaticExact(string ownerType, string name, IReadOnlyList<string> typeArguments) =>
            calls.FirstOrDefault(candidate =>
                candidate.IsStatic
                && string.Equals(candidate.OwnerType, ownerType, StringComparison.Ordinal)
                && candidate.Name == name
                && SameTypeArguments(candidate.TypeArguments, typeArguments));

        public GenericCallInfo? FindExact(string? ownerType, string name, IReadOnlyList<string> typeArguments) =>
            calls.FirstOrDefault(candidate =>
                string.Equals(candidate.OwnerType, ownerType, StringComparison.Ordinal)
                && candidate.Name == name
                && SameTypeArguments(candidate.TypeArguments, typeArguments));

        public GenericCallInfo? FindGenericMemberExact(
            string? sourceOwnerType,
            string concreteOwnerType,
            string name,
            IReadOnlyList<string> typeArguments) =>
            calls.FirstOrDefault(call =>
                !call.IsStatic
                && MatchesGenericOwner(call, sourceOwnerType, concreteOwnerType)
                && call.Name == name
                && SameTypeArguments(call.TypeArguments, typeArguments));

        public GenericCallInfo? FindInferredCall(
            string? ownerType,
            string name,
            IReadOnlyList<ExpressionNode> arguments,
            bool skipSelf,
            IReadOnlyList<string>? preferredTypeArguments = null)
        {
            var candidates = calls.Where(call =>
                string.Equals(call.OwnerType, ownerType, StringComparison.Ordinal)
                && call.Name == name);
            return FindInferred(candidates, arguments, skipSelf, preferredTypeArguments);
        }

        public GenericCallInfo? FindInferredMemberCall(
            string? sourceOwnerType,
            string concreteOwnerType,
            string name,
            IReadOnlyList<ExpressionNode> arguments,
            bool skipSelf,
            IReadOnlyList<string>? preferredTypeArguments = null)
        {
            var candidates = calls.Where(call =>
                !call.IsStatic
                && MatchesGenericOwner(call, sourceOwnerType, concreteOwnerType)
                && call.Name == name);
            return FindInferred(candidates, arguments, skipSelf, preferredTypeArguments);
        }

        public GenericCallInfo? FindResolved(ResolvedCallInfo resolvedCall) =>
            calls.FirstOrDefault(call =>
                string.Equals(call.OwnerType, resolvedCall.Function.OwnerType, StringComparison.Ordinal)
                && string.Equals(call.Name, resolvedCall.Function.Name, StringComparison.Ordinal)
                && SameTypeArguments(call.TypeArguments, resolvedCall.TypeArguments));

        public IEnumerable<GenericCallInfo> GetStaticOrFreeCalls() =>
            calls.Where(call => call.IsStatic || call.OwnerType is null);

        public IEnumerable<GenericCallInfo> GetInstanceCallsForOwner(string? ownerType) =>
            calls.Where(call => !call.IsStatic && call.OwnerType == ownerType);

        private GenericCallInfo? FindInferred(
            IEnumerable<GenericCallInfo> candidates,
            IReadOnlyList<ExpressionNode> arguments,
            bool skipSelf,
            IReadOnlyList<string>? preferredTypeArguments)
        {
            if (preferredTypeArguments is { Count: > 0 })
            {
                candidates = candidates
                    .OrderByDescending(call => SameTypeArguments(call.TypeArguments, preferredTypeArguments));
            }

            foreach (var candidate in candidates)
            {
                var parameterTypes = candidate.ParameterTypes
                    .Skip(skipSelf ? 1 : 0)
                    .ToList();
                if (parameterTypes.Count != arguments.Count)
                {
                    continue;
                }

                if (preferredTypeArguments is { Count: > 0 })
                {
                    if (SameTypeArguments(candidate.TypeArguments, preferredTypeArguments))
                    {
                        return candidate;
                    }

                    continue;
                }

                var matches = true;
                for (var i = 0; i < arguments.Count; i++)
                {
                    var argumentType = resolveExpressionType(arguments[i]);
                    if (argumentType is null || !canAssign(parameterTypes[i], argumentType))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return candidate;
                }
            }

            return null;
        }

        public static bool SameTypeArguments(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right) =>
            left.Count == right.Count
            && left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

        private static bool MatchesGenericOwner(
            GenericCallInfo call,
            string? sourceOwner,
            string concreteOwner)
        {
            if (string.Equals(call.OwnerType, sourceOwner, StringComparison.Ordinal))
            {
                return true;
            }

            return call.OwnerType is not null
                && GenericTypeRewriter.LowerGenericTypeName(call.OwnerType, call.TypeArguments) == concreteOwner;
        }
    }
}
