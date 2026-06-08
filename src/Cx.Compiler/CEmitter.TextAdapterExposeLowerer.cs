using System.Text.RegularExpressions;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class TextAdapterExposeLowerer(
        CLoweringContext context,
        GenericCallResolver genericCallResolver,
        AdapterExposeResolver adapterExposeResolver)
    {
        public string LowerCalls(
            string expression,
            string variable,
            string receiver,
            string adapterName,
            IReadOnlyList<string> receiverArguments)
        {
            foreach (var expose in GetInstanceAdapterExposes(adapterName))
            {
                expression = LowerCall(expression, variable, receiver, expose, receiverArguments);
            }

            return expression;
        }

        public string LowerCall(
            string expression,
            string variable,
            string receiver,
            AdapterExposeInfo expose,
            IReadOnlyList<string> receiverArguments)
        {
            var resolvedExpose = adapterExposeResolver.Resolve(expose, receiverArguments);
            var baseOwner = resolvedExpose.BaseOwner;
            var typeArguments = resolvedExpose.TypeArguments;
            var restoredBaseType = genericCallResolver.RestoreSourceGenericType(resolvedExpose.BaseType);
            if (typeArguments.Count == 0
                && TryParseGenericUse(restoredBaseType, out var restoredOwner, out var restoredArguments))
            {
                baseOwner = restoredOwner;
                typeArguments = restoredArguments;
            }

            var genericBaseCall = genericCallResolver.FindExact(
                baseOwner,
                resolvedExpose.SourceName,
                typeArguments);

            var cName = genericBaseCall?.CName;
            if (cName is null)
            {
                if (typeArguments.Count > 0)
                {
                    cName = BuildSpecializedFunctionName(
                        baseOwner,
                        resolvedExpose.SourceName,
                        typeArguments);
                }
                else if (!context.TryGetMethod($"{baseOwner}.{resolvedExpose.SourceName}", out var baseMethod))
                {
                    return expression;
                }
                else
                {
                    cName = baseMethod.CName;
                }
            }

            expression = Regex.Replace(
                expression,
                $@"\b{Regex.Escape(variable)}\.{Regex.Escape(expose.ExposedName)}\(\s*\)",
                $"{cName}({receiver})");
            return Regex.Replace(
                expression,
                $@"\b{Regex.Escape(variable)}\.{Regex.Escape(expose.ExposedName)}\(",
                $"{cName}({receiver}, ");
        }

        private static string BuildSpecializedFunctionName(
            string ownerType,
            string name,
            IReadOnlyList<string> typeArguments) =>
            $"{ownerType}_{name}_{string.Join("_", typeArguments.Select(argument => SanitizeTypeName(LowerType(argument))))}";

        private IEnumerable<AdapterExposeInfo> GetInstanceAdapterExposes(string adapterName)
        {
            var found = context.GetInstanceAdapterExposes(adapterName).ToList();
            if (found.Count > 0)
            {
                return found;
            }

            var unqualifiedName = UnqualifiedName(adapterName);
            return context.GetInstanceAdapterExposes()
                .Where(expose => string.Equals(UnqualifiedName(expose.AdapterName), unqualifiedName, StringComparison.Ordinal));
        }

        private static string UnqualifiedName(string name) =>
            name.Contains('.', StringComparison.Ordinal)
                ? name[(name.LastIndexOf('.') + 1)..]
                : name;
    }
}
