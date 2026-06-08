using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class MemberCallLowerer(
        CLoweringContext context,
        CLoweringScope scope,
        GenericCallResolver genericCallResolver,
        ResolvedCallLowerer resolvedCallLowerer,
        InterfaceMemberCallLowerer interfaceMemberCallLowerer,
        AdapterExposeResolver adapterExposeResolver,
        ReceiverExpressionBuilder receiverExpressionBuilder,
        Func<ExpressionNode, string?> resolveExpressionType,
        Func<ExpressionNode, CExpression> lowerExpression)
    {
        public CExpression? TryLower(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (interfaceMemberCallLowerer.TryLower(member, arguments) is { } interfaceCall)
            {
                return interfaceCall;
            }

            if (TryLowerKnownTarget(member, arguments) is { } knownTargetCall)
            {
                return knownTargetCall;
            }

            if (member.Target is not NameExpressionNode targetName)
            {
                return null;
            }

            var target = targetName.SourceText;
            if (!scope.TryGetVariableType(target, out var targetType))
            {
                return TryLowerStaticOrModuleCall(target, member.MemberName, arguments);
            }

            if (resolvedCallLowerer.TryLowerInstance(member.Semantic.ResolvedCall, member, arguments) is { } resolvedInstanceCall)
            {
                return resolvedInstanceCall;
            }

            return TryLowerInstanceCall(target, targetType, member.MemberName, arguments);
        }

        public CExpression? TryLowerGenericMember(
            MemberExpressionNode member,
            IReadOnlyList<string> typeArguments,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (resolvedCallLowerer.TryLowerInstance(member.Semantic.ResolvedCall, member, arguments) is { } resolvedInstanceCall)
            {
                return resolvedInstanceCall;
            }

            if (member.Target is not NameExpressionNode targetName
                || !scope.TryGetVariableType(targetName.SourceText, out var targetType))
            {
                return null;
            }

            var target = targetName.SourceText;
            var concreteReceiverType = RemovePointer(targetType);
            if (genericCallResolver.FindGenericMemberExact(
                GetGenericBaseName(targetType),
                concreteReceiverType,
                member.MemberName,
                typeArguments) is not { } genericCall)
            {
                return null;
            }

            var loweredArguments = arguments.Select(lowerExpression).ToList();
            loweredArguments.Insert(0, receiverExpressionBuilder.Build(
                target,
                targetType.EndsWith("*", StringComparison.Ordinal),
                genericCall.TakesPointerSelf));
            return new CCallExpression(
                new CResolvedFunction(GetFunctionModule(genericCall.OwnerType, genericCall.Name), genericCall.CName),
                loweredArguments);
        }

        public CExpression? TryLowerAdapterExposeCall(
            AdapterExposeInfo adapterExpose,
            IReadOnlyList<string> receiverArguments,
            IReadOnlyList<ExpressionNode> arguments,
            string target,
            bool isPointer)
        {
            var resolvedExpose = adapterExposeResolver.Resolve(adapterExpose, receiverArguments);
            var baseOwner = resolvedExpose.BaseOwner;
            var typeArguments = resolvedExpose.TypeArguments;
            var restoredBaseType = genericCallResolver.RestoreSourceGenericType(resolvedExpose.BaseType);
            if (typeArguments.Count == 0
                && TryParseGenericUse(restoredBaseType, out var restoredOwner, out var restoredArguments))
            {
                baseOwner = restoredOwner;
                typeArguments = restoredArguments;
            }

            var genericBaseCall = genericCallResolver.FindInferredCall(
                baseOwner,
                resolvedExpose.SourceName,
                arguments,
                skipSelf: true,
                preferredTypeArguments: typeArguments)
                ?? genericCallResolver.FindExact(
                    baseOwner,
                    resolvedExpose.SourceName,
                    typeArguments);
            if (genericBaseCall is not null)
            {
                var loweredArguments = arguments.Select(lowerExpression).ToList();
                loweredArguments.Insert(0, receiverExpressionBuilder.Build(target, isPointer, takesPointerSelf: true));
                return new CCallExpression(
                    new CResolvedFunction(GetFunctionModule(genericBaseCall.OwnerType, genericBaseCall.Name), genericBaseCall.CName),
                    loweredArguments);
            }

            var baseMethodKey = $"{baseOwner}.{resolvedExpose.SourceName}";
            if (context.TryGetMethod(baseMethodKey, out var baseMethod))
            {
                var loweredArguments = arguments.Select(lowerExpression).ToList();
                loweredArguments.Insert(0, receiverExpressionBuilder.Build(target, isPointer, takesPointerSelf: true));
                return new CCallExpression(
                    new CResolvedFunction(GetFunctionModule(baseOwner, resolvedExpose.SourceName) ?? baseOwner, baseMethod.CName),
                    loweredArguments);
            }

            return null;
        }

        private CExpression? TryLowerStaticOrModuleCall(
            string target,
            string memberName,
            IReadOnlyList<ExpressionNode> arguments)
        {
            var staticGenericCall = genericCallResolver.FindInferredCall(target, memberName, arguments, skipSelf: false);
            if (staticGenericCall is not null)
            {
                return new CCallExpression(
                    new CResolvedFunction(GetFunctionModule(staticGenericCall.OwnerType, staticGenericCall.Name), staticGenericCall.CName),
                    arguments.Select(lowerExpression).ToList());
            }

            var staticMethodKey = $"{target}.{memberName}";
            if (context.TryGetMethod(staticMethodKey, out var staticMethod))
            {
                return new CCallExpression(
                    new CResolvedFunction(target, staticMethod.CName),
                    arguments.Select(lowerExpression).ToList());
            }

            return context.IsModuleQualifierTarget(target)
                ? new CCallExpression(
                    new CFunctionName(memberName),
                    arguments.Select(lowerExpression).ToList())
                : null;
        }

        private CExpression? TryLowerInstanceCall(
            string target,
            string targetType,
            string memberName,
            IReadOnlyList<ExpressionNode> arguments)
        {
            var normalizedType = NormalizeType(targetType);
            var isPointer = targetType.EndsWith("*", StringComparison.Ordinal);
            var receiverArguments = TryParseGenericUse(RemovePointer(normalizedType), out _, out var parsedReceiverArguments)
                ? parsedReceiverArguments
                : [];
            var adapterName = GetGenericBaseName(RemovePointer(normalizedType)) ?? RemovePointer(normalizedType);
            if (context.TryGetAdapterExpose($"{adapterName}.{memberName}", out var adapterExpose)
                && !adapterExpose.IsStatic
                && TryLowerAdapterExposeCall(
                    adapterExpose,
                    receiverArguments,
                    arguments,
                    target,
                    isPointer) is { } adapterExposeCall)
            {
                return adapterExposeCall;
            }

            var genericMemberCall = genericCallResolver.FindInferredMemberCall(
                GetGenericBaseName(RemovePointer(normalizedType)),
                RemovePointer(normalizedType),
                memberName,
                arguments,
                skipSelf: true,
                preferredTypeArguments: receiverArguments);
            if (genericMemberCall is not null)
            {
                var loweredArguments = arguments.Select(lowerExpression).ToList();
                loweredArguments.Insert(0, receiverExpressionBuilder.Build(target, isPointer, genericMemberCall.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(GetFunctionModule(genericMemberCall.OwnerType, genericMemberCall.Name), genericMemberCall.CName),
                    loweredArguments);
            }

            foreach (var methodInfo in context.GetInstanceMethodsForReceiver(normalizedType))
            {
                if (methodInfo.Name != memberName)
                {
                    continue;
                }

                var loweredArguments = arguments.Select(lowerExpression).ToList();
                loweredArguments.Insert(0, receiverExpressionBuilder.Build(target, isPointer, methodInfo.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(NormalizeType(targetType), methodInfo.CName),
                    loweredArguments);
            }

            return null;
        }

        private CExpression? TryLowerKnownTarget(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (member.Target is NameExpressionNode)
            {
                return null;
            }

            var targetType = resolveExpressionType(member.Target);
            if (targetType is null)
            {
                return null;
            }

            var normalizedType = NormalizeType(targetType);
            var isPointer = targetType.EndsWith("*", StringComparison.Ordinal);
            var receiverType = RemovePointer(normalizedType);
            var receiverArguments = TryParseGenericUse(receiverType, out _, out var parsedReceiverArguments)
                ? parsedReceiverArguments
                : [];
            var ownerType = GetGenericBaseName(receiverType) ?? receiverType;

            var genericMemberCall = genericCallResolver.FindInferredCall(
                ownerType,
                member.MemberName,
                arguments,
                skipSelf: true,
                preferredTypeArguments: receiverArguments);
            if (genericMemberCall is not null)
            {
                var targetExpression = lowerExpression(member.Target);
                var loweredArguments = arguments.Select(lowerExpression).ToList();
                loweredArguments.Insert(0, ReceiverExpressionBuilder.Build(targetExpression, isPointer, genericMemberCall.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(GetFunctionModule(genericMemberCall.OwnerType, genericMemberCall.Name), genericMemberCall.CName),
                    loweredArguments);
            }

            foreach (var methodInfo in context.GetInstanceMethodsForReceiver(receiverType))
            {
                if (methodInfo.Name != member.MemberName)
                {
                    continue;
                }

                var targetExpression = lowerExpression(member.Target);
                var loweredArguments = arguments.Select(lowerExpression).ToList();
                loweredArguments.Insert(0, ReceiverExpressionBuilder.Build(targetExpression, isPointer, methodInfo.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(receiverType, methodInfo.CName),
                    loweredArguments);
            }

            return null;
        }

        private static string GetFunctionModule(string? ownerType, string name) =>
            ownerType is null ? name : ownerType;
    }
}
