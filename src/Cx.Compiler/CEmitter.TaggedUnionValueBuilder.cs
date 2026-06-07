using Cx.Compiler.C;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class TaggedUnionValueBuilder(
        CLoweringContext context,
        Func<string, string?> inferExpressionType,
        Func<string, TypeRef?> inferExpressionTypeRef,
        Func<string, string> lowerCxType,
        Func<TypeRef, string> lowerTypeRef,
        Func<TypeRef?> selfTypeProvider)
    {
        public string? TryWrap(string targetType, string sourceExpression, string loweredExpression)
        {
            if (TryWrapExpression(targetType, sourceExpression, new CRawExpression(loweredExpression)) is not { } expression)
            {
                return null;
            }

            return new CExpressionEmitter().Emit(expression);
        }

        public string? TryWrap(TypeRef targetType, string sourceExpression, string loweredExpression)
        {
            if (TryWrapExpression(targetType, sourceExpression, new CRawExpression(loweredExpression)) is not { } expression)
            {
                return null;
            }

            return new CExpressionEmitter().Emit(expression);
        }

        public CExpression? TryWrapExpression(
            string targetType,
            string sourceExpression,
            CExpression loweredExpression)
        {
            var normalizedTargetType = NormalizeType(targetType);
            if (!context.TryGetTaggedUnion(normalizedTargetType, out var taggedUnion)
                || taggedUnion.IsRaw)
            {
                return null;
            }

            var expressionType = inferExpressionType(sourceExpression);
            if (expressionType is null)
            {
                return null;
            }

            var matchingVariants = taggedUnion.Variants
                .Where(variant => lowerCxType(variant.Type) == lowerCxType(expressionType))
                .ToList();

            if (matchingVariants.Count != 1)
            {
                return null;
            }

            var variant = matchingVariants[0];
            return BuildInitializer(lowerCxType(taggedUnion.Name), taggedUnion.Name, variant.Name, loweredExpression);
        }

        public CExpression? TryBuildConstructorExpression(
            string unionName,
            string variantName,
            IReadOnlyList<ExpressionNode> arguments,
            Func<string, IReadOnlyList<ExpressionNode>, CExpression> buildPayload)
        {
            if (!context.TryGetTaggedUnionVariant(unionName, variantName, out var taggedUnion, out var variant)
                || taggedUnion.IsRaw)
            {
                return null;
            }

            return BuildInitializer(
                lowerCxType(taggedUnion.Name),
                taggedUnion.Name,
                variant.Name,
                buildPayload(variant.Type, arguments));
        }

        public string BuildConstructorText(
            TaggedUnionNode taggedUnion,
            TaggedUnionVariantNode variant,
            IReadOnlyList<string> arguments,
            Func<string, IReadOnlyList<string>, string> buildPayload)
        {
            var payload = buildPayload(variant.Type, arguments);
            return $"({taggedUnion.Name}){{ .tag = {taggedUnion.Name}_Tag_{variant.Name}, .as.{variant.Name} = {payload} }}";
        }

        public CExpression? TryWrapExpression(
            TypeRef targetType,
            string sourceExpression,
            CExpression loweredExpression)
        {
            var normalizedTargetType = NormalizeType(TypeRefFormatter.ToCxString(targetType));
            if (!context.TryGetTaggedUnion(normalizedTargetType, out var taggedUnion)
                || taggedUnion.IsRaw)
            {
                return null;
            }

            var expressionType = inferExpressionTypeRef(sourceExpression);
            if (expressionType is null)
            {
                return null;
            }

            var matchingVariants = taggedUnion.Variants
                .Where(variant => AreSameLoweredType(variant.TypeNode?.Semantic.Type, variant.Type, expressionType))
                .ToList();

            if (matchingVariants.Count != 1)
            {
                return null;
            }

            var matchedVariant = matchingVariants[0];
            return BuildInitializer(lowerTypeRef(targetType), taggedUnion.Name, matchedVariant.Name, loweredExpression);
        }

        private CExpression BuildInitializer(
            string loweredUnionType,
            string unionName,
            string variantName,
            CExpression loweredExpression) =>
            new CInitializerExpression(
                loweredUnionType,
                [
                    new CInitializerField("tag", new CNameExpression($"{unionName}_Tag_{variantName}")),
                    new CInitializerField("as." + variantName, loweredExpression),
                ],
                []);

        private bool AreSameLoweredType(TypeRef? leftType, string leftFallback, TypeRef rightType)
        {
            var loweredLeft = leftType is null
                ? lowerCxType(leftFallback)
                : CTypeLowerer.LowerType(leftType, s_typeAdapters, selfTypeProvider());
            var loweredRight = lowerTypeRef(rightType);
            return string.Equals(loweredLeft, loweredRight, StringComparison.Ordinal);
        }
    }
}
