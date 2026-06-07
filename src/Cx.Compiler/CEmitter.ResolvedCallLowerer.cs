using Cx.Compiler.C;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class ResolvedCallLowerer(
        CLoweringContext context,
        CLoweringScope scope,
        GenericCallResolver genericCallResolver,
        ReceiverExpressionBuilder receiverExpressionBuilder,
        Func<ExpressionNode, CExpression> lowerExpression)
    {
        public CExpression? TryLowerStatic(
            ResolvedCallInfo? resolvedCall,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (resolvedCall is not { IsInstance: false })
            {
                return null;
            }

            var functionReference = ResolveFunctionReference(resolvedCall);
            return functionReference is null
                ? null
                : new CCallExpression(functionReference, arguments.Select(lowerExpression).ToList());
        }

        public CExpression? TryLowerInstance(
            ResolvedCallInfo? resolvedCall,
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (resolvedCall is not { IsInstance: true, Function.OwnerType: not null } resolved
                || member.Target is not NameExpressionNode targetName
                || !scope.TryGetVariableType(targetName.SourceText, out var targetType))
            {
                return null;
            }

            var takesPointerSelf = resolved.TypeArguments.Count > 0
                ? genericCallResolver.FindResolved(resolvedCall)?.TakesPointerSelf
                : context.TryGetMethodTakesPointerSelf($"{resolved.Function.OwnerType}.{resolved.Function.Name}", out var methodTakesPointerSelf)
                    ? methodTakesPointerSelf
                    : null;
            if (takesPointerSelf is null)
            {
                return null;
            }

            var functionReference = ResolveFunctionReference(resolvedCall);
            if (functionReference is null)
            {
                return null;
            }

            var loweredArguments = arguments.Select(lowerExpression).ToList();
            loweredArguments.Insert(0, receiverExpressionBuilder.Build(
                targetName.SourceText,
                targetType.EndsWith("*", StringComparison.Ordinal),
                takesPointerSelf.Value));
            return new CCallExpression(functionReference, loweredArguments);
        }

        private CFunctionReference? ResolveFunctionReference(ResolvedCallInfo resolvedCall)
        {
            if (resolvedCall.TypeArguments.Count > 0)
            {
                var genericCall = genericCallResolver.FindResolved(resolvedCall);
                return genericCall is null
                    ? null
                    : new CResolvedFunction(
                        GetFunctionModule(genericCall.OwnerType, genericCall.Name) ?? resolvedCall.Function.OwnerType ?? genericCall.Name,
                        genericCall.CName);
            }

            return new CResolvedFunction(
                GetFunctionModule(resolvedCall.Function.OwnerType, resolvedCall.Function.Name)
                    ?? resolvedCall.Function.OwnerType
                    ?? resolvedCall.Function.Name,
                s_nameMangler.FunctionName(resolvedCall.Function));
        }

        private static string? GetFunctionModule(string? ownerType, string name) =>
            ownerType is null ? name : ownerType;
    }
}
