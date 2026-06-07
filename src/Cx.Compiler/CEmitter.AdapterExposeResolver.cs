namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class AdapterExposeResolver(CLoweringContext context)
    {
        public string SubstituteBaseType(
            AdapterExposeInfo adapter,
            IReadOnlyList<string> receiverArguments)
        {
            if (adapter.TypeParameters.Count == 0 || adapter.TypeParameters.Count != receiverArguments.Count)
            {
                return adapter.BaseType;
            }

            var substitutions = adapter.TypeParameters
                .Zip(receiverArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            return SubstituteGenericType(adapter.BaseType, substitutions);
        }

        public ResolvedAdapterExpose Resolve(
            AdapterExposeInfo expose,
            IReadOnlyList<string> receiverArguments)
        {
            var current = expose;
            var currentArguments = receiverArguments;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                var baseType = SubstituteBaseType(current, currentArguments);
                var baseOwner = GetGenericBaseName(baseType) ?? baseType;
                var baseArguments = TryParseGenericUse(baseType, out _, out var parsedBaseArguments)
                    ? parsedBaseArguments
                    : [];
                var key = $"{current.AdapterName}.{current.ExposedName}";
                if (!seen.Add(key)
                    || !context.TryGetAdapterExpose($"{baseOwner}.{current.SourceName}", out var next)
                    || next.IsStatic != current.IsStatic)
                {
                    return new ResolvedAdapterExpose(baseType, baseOwner, current.SourceName, baseArguments);
                }

                current = next;
                currentArguments = baseArguments;
            }
        }
    }
}
