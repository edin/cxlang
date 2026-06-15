using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class CallLowerer(
        CLoweringContext context,
        GenericCallResolver genericCallResolver,
        ResolvedCallLowerer resolvedCallLowerer,
        CFunctionReferenceResolver functionReferences,
        MemberCallLowerer memberCallLowerer,
        StructValueBuilder structValueBuilder,
        TaggedUnionValueBuilder taggedUnionValueBuilder,
        Func<NameExpressionNode, string> lowerFunctionReferenceName,
        Func<ExpressionNode, CExpression> lowerExpression)
    {
        public CExpression? TryLowerExpression(CallExpressionNode call)
        {
            if (resolvedCallLowerer.TryLowerStatic(call.Semantic.ResolvedCall, call.Arguments) is { } resolvedCall)
            {
                return resolvedCall;
            }

            if (call.Callee is MemberExpressionNode member)
            {
                if (TryLowerTaggedUnionConstructorExpression(member, call.Arguments) is { } taggedUnionConstructor)
                {
                    return taggedUnionConstructor;
                }

                return memberCallLowerer.TryLower(member, call.Arguments);
            }

            if (call.Callee is NameExpressionNode name)
            {
                if (context.TryGetStruct(name.SourceText, out var structNode))
                {
                    return structValueBuilder.BuildStructConstructorExpression(structNode, call.Arguments);
                }

                if (context.IsTaggedUnion(name.SourceText))
                {
                    return null;
                }

                var genericCall = genericCallResolver.FindInferredCall(null, name.SourceText, call.Arguments, skipSelf: false);
                if (genericCall is not null)
                {
                    return new CCallExpression(
                        functionReferences.Resolve(genericCall.OwnerType, genericCall.Name, genericCall.CName),
                        call.Arguments.Select(lowerExpression).ToList());
                }

                return new CCallExpression(
                    new CFunctionName(lowerFunctionReferenceName(name)),
                    call.Arguments.Select(lowerExpression).ToList());
            }

            return null;
        }

        private CExpression? TryLowerTaggedUnionConstructorExpression(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (ExpressionNameFacts.GetQualifiedName(member.Target) is not { } targetName)
            {
                return null;
            }

            return taggedUnionValueBuilder.TryBuildConstructorExpression(
                targetName,
                member.MemberName,
                arguments,
                LowerPayloadConstructorExpression);
        }

        private CExpression LowerPayloadConstructorExpression(
            string payloadType,
            IReadOnlyList<ExpressionNode> arguments) =>
            structValueBuilder.BuildPayloadExpression(payloadType, arguments);

    }
}
