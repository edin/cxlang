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
                    || !TryGetAdapterExpose(baseOwner, current.SourceName, out var next)
                    || next.IsStatic != current.IsStatic)
                {
                    return new ResolvedAdapterExpose(baseType, baseOwner, current.SourceName, baseArguments);
                }

                current = next;
                currentArguments = baseArguments;
            }
        }

        private bool TryGetAdapterExpose(
            string adapterName,
            string exposedName,
            out AdapterExposeInfo expose)
        {
            if (context.TryGetAdapterExpose($"{adapterName}.{exposedName}", out expose!))
            {
                return true;
            }

            var unqualifiedName = UnqualifiedName(adapterName);
            expose = context.GetInstanceAdapterExposes()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.ExposedName, exposedName, StringComparison.Ordinal)
                    && string.Equals(UnqualifiedName(candidate.AdapterName), unqualifiedName, StringComparison.Ordinal))!;
            return expose is not null;
        }

        private static string UnqualifiedName(string name) =>
            name.Contains('.', StringComparison.Ordinal)
                ? name[(name.LastIndexOf('.') + 1)..]
                : name;
    }
}
