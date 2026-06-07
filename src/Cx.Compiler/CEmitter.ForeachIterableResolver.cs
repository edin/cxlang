using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class ForeachIterableResolver(
        CLoweringScope scope,
        GenericCallResolver genericCallResolver,
        RequirementLookup requirementLookup,
        Func<ExpressionNode, string?> resolveExpressionType,
        Func<ExpressionNode, CExpression> lowerExpression)
    {
        public ContiguousIterableInfo? ResolveContiguousIterable(string expression)
        {
            expression = expression.Trim();
            if (!scope.TryGetVariableType(expression, out var type))
            {
                return null;
            }

            var isPointer = type.EndsWith("*", StringComparison.Ordinal);
            if (TryParseFixedArrayType(type, out var arrayElementType, out var arrayLength))
            {
                return new ContiguousIterableInfo(
                    arrayElementType,
                    arrayLength,
                    expression);
            }

            var access = isPointer ? "->" : ".";
            var contiguous = requirementLookup.Match(type, "Contiguous");
            if (contiguous.Success && contiguous.TypeBindings.TryGetValue("T", out var valueType))
            {
                return new ContiguousIterableInfo(
                    valueType,
                    $"{expression}{access}length",
                    $"{expression}{access}data");
            }

            var range = requirementLookup.Match(type, "ContiguousRange");
            if (range.Success && range.TypeBindings.TryGetValue("T", out var rangeValueType))
            {
                return new ContiguousIterableInfo(
                    rangeValueType,
                    $"({expression}{access}end - {expression}{access}start)",
                    $"{expression}{access}start");
            }

            return null;
        }

        public IteratorIterableInfo? ResolveIteratorIterable(ExpressionNode expression, bool keyValue)
        {
            var source = expression.SourceText.Trim();
            if (!scope.TryGetVariableType(source, out var type))
            {
                return null;
            }

            var sourceType = genericCallResolver.RestoreSourceGenericType(type);
            var iterableRequirement = keyValue ? "KeyValueIterable" : "Iterable";
            var iterable = requirementLookup.Match(sourceType, iterableRequirement);
            if (!iterable.Success || !iterable.TypeBindings.TryGetValue("I", out var iteratorType))
            {
                return ResolveIteratorIterableFromSpecializedIterator(type, expression, keyValue)
                    ?? ResolveIteratorIterableFromIteratorMethod(expression, keyValue);
            }

            if (keyValue)
            {
                if (!iterable.TypeBindings.TryGetValue("K", out var keyType)
                    || !iterable.TypeBindings.TryGetValue("V", out var valueType))
                {
                    return null;
                }

                return new IteratorIterableInfo(
                    iteratorType,
                    valueType,
                    keyType,
                    BuildIteratorInitializer(expression));
            }

            if (!iterable.TypeBindings.TryGetValue("T", out var itemType))
            {
                return null;
            }

            return new IteratorIterableInfo(
                iteratorType,
                itemType,
                KeyType: null,
                BuildIteratorInitializer(expression));
        }

        private IteratorIterableInfo? ResolveIteratorIterableFromSpecializedIterator(
            string type,
            ExpressionNode expression,
            bool keyValue)
        {
            var concreteType = RemovePointer(type.Trim());
            var iterator = genericCallResolver.FindIterator(GetGenericBaseName(type), concreteType);
            if (iterator is null)
            {
                return null;
            }

            if (keyValue)
            {
                if (iterator.TypeArguments.Count < 2)
                {
                    return null;
                }

                return new IteratorIterableInfo(
                    iterator.ReturnType,
                    iterator.TypeArguments[1],
                    iterator.TypeArguments[0],
                    BuildIteratorInitializer(expression));
            }

            if (iterator.TypeArguments.Count < 1)
            {
                return null;
            }

            return new IteratorIterableInfo(
                iterator.ReturnType,
                iterator.TypeArguments[0],
                KeyType: null,
                BuildIteratorInitializer(expression));
        }

        private IteratorIterableInfo? ResolveIteratorIterableFromIteratorMethod(ExpressionNode expression, bool keyValue)
        {
            var iteratorType = resolveExpressionType(BuildIteratorCall(expression));
            if (iteratorType is null)
            {
                return null;
            }

            var iteratorRequirement = keyValue ? "KeyValueIterator" : "Iterator";
            var iterator = requirementLookup.Match(iteratorType, iteratorRequirement);
            if (!iterator.Success)
            {
                return null;
            }

            if (keyValue)
            {
                if (!iterator.TypeBindings.TryGetValue("K", out var keyType)
                    || !iterator.TypeBindings.TryGetValue("V", out var valueType))
                {
                    return null;
                }

                return new IteratorIterableInfo(
                    iteratorType,
                    valueType,
                    keyType,
                    BuildIteratorInitializer(expression));
            }

            if (!iterator.TypeBindings.TryGetValue("T", out var itemType))
            {
                return null;
            }

            return new IteratorIterableInfo(
                iteratorType,
                itemType,
                KeyType: null,
                BuildIteratorInitializer(expression));
        }

        private CExpression BuildIteratorInitializer(ExpressionNode expression) =>
            lowerExpression(BuildIteratorCall(expression));

        private static CallExpressionNode BuildIteratorCall(ExpressionNode expression)
        {
            var source = expression.SourceText.Trim();
            return new CallExpressionNode(
                expression.Location,
                source + ".iterator()",
                new MemberExpressionNode(
                    expression.Location,
                    source + ".iterator",
                    expression,
                    "iterator"),
                []);
        }

        private static string RemovePointer(string type)
        {
            while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
            {
                type = type.TrimEnd()[..^1];
            }

            return type.TrimEnd();
        }
    }
}
