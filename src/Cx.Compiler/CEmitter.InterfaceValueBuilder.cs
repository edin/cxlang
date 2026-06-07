using Cx.Compiler.C;
using Cx.Compiler.Semantic;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class InterfaceValueBuilder(
        CLoweringContext context,
        CLoweringScope scope,
        Func<string, string> lowerCxType,
        Func<TypeRef, string> lowerTypeRef)
    {
        public CExpression? TryBuild(string targetType, string sourceExpression)
        {
            var interfaceName = NormalizeType(targetType);
            return TryBuild(interfaceName, sourceExpression, lowerCxType(interfaceName));
        }

        public CExpression? TryBuild(TypeRef targetType, string sourceExpression)
        {
            var interfaceName = NormalizeType(TypeRefFormatter.ToCxString(targetType));
            return TryBuild(interfaceName, sourceExpression, lowerTypeRef(targetType));
        }

        private CExpression? TryBuild(
            string interfaceName,
            string sourceExpression,
            string loweredInterfaceName)
        {
            if (!context.IsInterface(interfaceName))
            {
                return null;
            }

            var trimmedSource = sourceExpression.Trim();
            if (!scope.TryGetVariableType(trimmedSource, out var sourceType))
            {
                return null;
            }

            var normalizedSourceType = scope.TryGetVariableTypeRef(trimmedSource, out var sourceTypeRef)
                ? lowerTypeRef(sourceTypeRef)
                : lowerCxType(NormalizeType(sourceType));
            if (!context.HasInterfaceImplementation(normalizedSourceType, interfaceName))
            {
                return null;
            }

            var sourceIsPointer = sourceType.TrimEnd().EndsWith("*", StringComparison.Ordinal);
            CExpression state = sourceIsPointer
                ? new CNameExpression(trimmedSource)
                : new CUnaryExpression("&", new CNameExpression(trimmedSource));
            return new CInitializerExpression(
                loweredInterfaceName,
                [
                    new CInitializerField("state", state),
                    new CInitializerField(
                        "vtable",
                        new CUnaryExpression("&", new CNameExpression(GetInterfaceVTableInstanceName(normalizedSourceType, interfaceName)))),
                ],
                []);
        }
    }
}
