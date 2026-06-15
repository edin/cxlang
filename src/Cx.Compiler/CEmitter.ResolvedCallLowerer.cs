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
        CFunctionReferenceResolver functionReferences,
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
            if (resolvedCall is not { IsInstance: true } resolved
                || member.Target is not NameExpressionNode targetName
                || OwnerType(resolved.Function) is not { } ownerType
                || !TryGetReceiverPointerInfo(targetName.SourceText, out var isPointerReceiver))
            {
                return null;
            }

            var takesPointerSelf = resolved.TypeArguments.Count > 0
                ? genericCallResolver.FindResolved(resolvedCall)?.TakesPointerSelf
                : context.TryGetMethodTakesPointerSelf($"{ownerType}.{resolved.Function.Name}", out var methodTakesPointerSelf)
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
                isPointerReceiver,
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
                    : functionReferences.Resolve(
                        genericCall.OwnerType ?? OwnerType(resolvedCall.Function),
                        genericCall.Name,
                        genericCall.CName);
            }

            var ownerType = OwnerType(resolvedCall.Function);
            return functionReferences.Resolve(
                ownerType,
                resolvedCall.Function.Name,
                s_nameMangler.FunctionName(resolvedCall.Function));
        }

        private bool TryGetReceiverPointerInfo(string name, out bool isPointer)
        {
            if (scope.TryGetVariableTypeRef(name, out var typeRef))
            {
                isPointer = typeRef is TypeRef.Pointer;
                return true;
            }

            if (scope.TryGetVariableType(name, out var typeText))
            {
                isPointer = typeText.EndsWith("*", StringComparison.Ordinal);
                return true;
            }

            isPointer = false;
            return false;
        }

        private string? OwnerType(FunctionNode function)
        {
            if (function.OwnerTypeNode is null)
            {
                return null;
            }

            return scope.ResolveType(function.OwnerTypeNode) is { } ownerType
                ? TypeRefFormatter.ToCxString(ownerType)
                : null;
        }
    }
}
