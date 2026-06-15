using Cx.Compiler.C;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class MemberAccessLowerer(
        CLoweringContext context,
        CLoweringScope scope,
        Func<ExpressionNode, string> lowerText,
        Func<ExpressionNode, CExpression> lowerExpression)
    {
        public string LowerText(MemberExpressionNode member)
        {
            if (TryLowerFunctionReferenceMember(member) is { } functionReference)
            {
                return functionReference;
            }

            var qualifiedMember = $"{ExpressionNameFacts.GetQualifiedName(member.Target)}.{member.MemberName}";
            if (context.TryGetEnumMemberAlias(qualifiedMember, out var enumMemberName))
            {
                return enumMemberName;
            }

            var staticMethodKey = $"{ExpressionNameFacts.GetQualifiedName(member.Target)}.{member.MemberName}";
            if (context.TryGetMethod(staticMethodKey, out var staticMethod))
            {
                return staticMethod.CName;
            }

            if (ExpressionNameFacts.GetQualifiedName(member.Target) is { } moduleTarget
                && context.IsModuleQualifierTarget(moduleTarget))
            {
                return member.MemberName;
            }

            if (TryLowerInterfaceVTableTypeIdText(member) is { } interfaceTypeIdAccess)
            {
                return interfaceTypeIdAccess;
            }

            if (TryGetNamedTarget(member, out var targetName, out var targetType, out var targetIsImplicitReference))
            {
                if (TryGetTaggedUnionAccess(member, targetType, targetIsImplicitReference) is { } taggedUnionAccess)
                {
                    return targetName.SourceText + taggedUnionAccess + member.MemberName;
                }

                if (targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal))
                {
                    return targetName.SourceText + "->" + member.MemberName;
                }
            }

            return $"{lowerText(member.Target)}.{member.MemberName}";
        }

        public CExpression LowerExpression(MemberExpressionNode member)
        {
            if (TryLowerFunctionReferenceMember(member) is { } functionReference)
            {
                return new CNameExpression(functionReference);
            }

            var qualifiedMember = $"{ExpressionNameFacts.GetQualifiedName(member.Target)}.{member.MemberName}";
            if (context.TryGetEnumMemberAlias(qualifiedMember, out var enumMemberName))
            {
                return new CNameExpression(enumMemberName);
            }

            var staticMethodKey = $"{ExpressionNameFacts.GetQualifiedName(member.Target)}.{member.MemberName}";
            if (context.TryGetMethod(staticMethodKey, out var staticMethod))
            {
                return new CNameExpression(staticMethod.CName);
            }

            if (ExpressionNameFacts.GetQualifiedName(member.Target) is { } moduleTarget
                && context.IsModuleQualifierTarget(moduleTarget))
            {
                return new CNameExpression(member.MemberName);
            }

            if (TryLowerInterfaceVTableTypeIdExpression(member) is { } interfaceTypeIdAccess)
            {
                return interfaceTypeIdAccess;
            }

            if (TryGetNamedTarget(member, out var targetName, out var targetType, out var targetIsImplicitReference))
            {
                if (TryGetTaggedUnionAccess(member, targetType, targetIsImplicitReference) is { } taggedUnionAccess)
                {
                    return new CMemberExpression(
                        new CNameExpression(targetName.SourceText),
                        taggedUnionAccess,
                        member.MemberName);
                }

                if (targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal))
                {
                    return new CMemberExpression(new CNameExpression(targetName.SourceText), "->", member.MemberName);
                }
            }

            return new CMemberExpression(lowerExpression(member.Target), ".", member.MemberName);
        }

        private string? TryLowerInterfaceVTableTypeIdText(MemberExpressionNode member)
        {
            if (!IsInterfaceVTableTypeIdAccess(member, out var interfaceValue, out var access))
            {
                return null;
            }

            return $"{lowerText(interfaceValue)}{access}vtable->type_id";
        }

        private CExpression? TryLowerInterfaceVTableTypeIdExpression(MemberExpressionNode member)
        {
            if (!IsInterfaceVTableTypeIdAccess(member, out var interfaceValue, out var access))
            {
                return null;
            }

            return new CMemberExpression(
                new CMemberExpression(lowerExpression(interfaceValue), access, "vtable"),
                "->",
                "type_id");
        }

        private bool IsInterfaceVTableTypeIdAccess(
            MemberExpressionNode member,
            out ExpressionNode interfaceValue,
            out string access)
        {
            interfaceValue = null!;
            access = ".";
            if (member is not { MemberName: "type_id", Target: MemberExpressionNode { MemberName: "vtable" } vtableAccess })
            {
                return false;
            }

            var targetType = ResolveExpressionType(vtableAccess.Target);
            if (targetType is null || !context.TryGetInterface(NormalizeType(targetType), out _))
            {
                return false;
            }

            interfaceValue = vtableAccess.Target;
            access = IsPointerLike(vtableAccess.Target, targetType) ? "->" : ".";
            return true;
        }

        private bool IsPointerLike(ExpressionNode expression, string type) =>
            type.TrimEnd().EndsWith("*", StringComparison.Ordinal)
            || expression is NameExpressionNode name && scope.IsImplicitReferenceLocal(name.SourceText);

        private string? ResolveExpressionType(ExpressionNode expression)
        {
            if (expression.Semantic.Type is { } semanticType)
            {
                return TypeRefFormatter.ToCxString(semanticType);
            }

            return expression is NameExpressionNode name && scope.TryGetVariableType(name.SourceText, out var type)
                ? type
                : null;
        }

        private bool TryGetNamedTarget(
            MemberExpressionNode member,
            out NameExpressionNode targetName,
            out string targetType,
            out bool targetIsImplicitReference)
        {
            if (member.Target is NameExpressionNode name
                && scope.TryGetVariableType(name.SourceText, out targetType!))
            {
                targetName = name;
                targetIsImplicitReference = scope.IsImplicitReferenceLocal(name.SourceText);
                return true;
            }

            targetName = null!;
            targetType = string.Empty;
            targetIsImplicitReference = false;
            return false;
        }

        private string? TryGetTaggedUnionAccess(
            MemberExpressionNode member,
            string targetType,
            bool targetIsImplicitReference)
        {
            var normalizedType = NormalizeType(targetType);
            if (!context.TryGetTaggedUnion(normalizedType, out var taggedUnion)
                || !taggedUnion.Variants.Any(variant => variant.Name == member.MemberName))
            {
                return null;
            }

            if (taggedUnion.IsRaw)
            {
                return targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal)
                    ? "->"
                    : ".";
            }

            return targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal)
                ? "->as."
                : ".as.";
        }

        private static string? TryLowerFunctionReferenceMember(MemberExpressionNode member) =>
            member.Semantic is { Symbol: { Kind: SymbolKind.Function } symbol, ResolvedCall.IsInstance: false }
                ? s_nameMangler.SymbolName(symbol)
                : null;

    }
}
