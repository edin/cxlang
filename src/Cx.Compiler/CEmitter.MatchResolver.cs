using Cx.Compiler.C;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class MatchResolver(
        CLoweringScope scope,
        CLoweringContext context)
    {
        public MatchInfo? ResolveMatch(string expression)
        {
            expression = expression.Trim();
            if (!scope.TryGetVariableType(expression, out var type))
            {
                return null;
            }

            var normalizedType = NormalizeType(type);
            if (context.TryGetTaggedUnion(normalizedType, out var taggedUnion))
            {
                var isPointer = type.EndsWith("*", StringComparison.Ordinal);
                return MatchInfo.ForTaggedUnion(
                    taggedUnion,
                    isPointer ? $"{expression}->tag" : $"{expression}.tag",
                    isPointer ? $"{expression}->as" : $"{expression}.as");
            }

            if (context.TryGetInterface(normalizedType, out var interfaceNode))
            {
                var isPointer = type.EndsWith("*", StringComparison.Ordinal);
                var access = isPointer ? "->" : ".";
                return MatchInfo.ForInterface(
                    interfaceNode,
                    $"{expression}{access}vtable->type_id",
                    $"{expression}{access}state",
                    context.GetInterfaceImplementationsByStruct(interfaceNode.Name));
            }

            return null;
        }
    }
}
