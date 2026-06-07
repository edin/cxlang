using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class InterfaceMemberCallLowerer(
        CLoweringContext context,
        Func<ExpressionNode, string?> resolveExpressionType,
        Func<ExpressionNode, CExpression> lowerExpression)
    {
        private readonly CExpressionEmitter _expressionEmitter = new();

        public CExpression? TryLower(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            var targetType = resolveExpressionType(member.Target);
            if (targetType is null)
            {
                return null;
            }

            var normalizedType = NormalizeType(RemovePointer(targetType));
            if (!context.InterfaceHasMethod(normalizedType, member.MemberName))
            {
                return null;
            }

            var isPointer = targetType.EndsWith("*", StringComparison.Ordinal);
            var access = isPointer ? "->" : ".";
            var targetExpression = lowerExpression(member.Target);
            var loweredArguments = arguments.Select(lowerExpression).ToList();
            loweredArguments.Insert(0, new CMemberExpression(targetExpression, access, "state"));

            var targetText = _expressionEmitter.Emit(targetExpression);
            return new CCallExpression(
                new CFunctionName($"{targetText}{access}vtable->{member.MemberName}"),
                loweredArguments);
        }
    }
}
