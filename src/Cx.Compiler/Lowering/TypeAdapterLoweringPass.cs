using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class TypeAdapterLoweringPass
{
    public static ProgramNode Apply(ProgramNode program, DiagnosticBag diagnostics)
    {
        if (program.TypeAdapters.Count == 0)
        {
            return program;
        }

        var structs = program.Structs
            .GroupBy(structNode => structNode.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var adapters = program.TypeAdapters
            .GroupBy(adapter => adapter.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var adapterMethods = new List<FunctionNode>();
        foreach (var adapter in program.TypeAdapters)
        {
            var baseName = GetGenericBaseName(adapter.BaseType);
            var baseTypeParameters = Array.Empty<string>() as IReadOnlyList<string>;
            if (structs.TryGetValue(baseName, out var baseStruct))
            {
                baseTypeParameters = baseStruct.TypeParameters;
            }
            else if (adapters.TryGetValue(baseName, out var baseAdapter))
            {
                baseTypeParameters = baseAdapter.TypeParameters;
            }
            else
            {
                diagnostics.Report(adapter.Location, $"Adapter base type '{adapter.BaseType}' was not found.");
                continue;
            }

            var baseArguments = TryParseGenericUse(adapter.BaseType, out _, out var parsedBaseArguments)
                ? parsedBaseArguments
                : [];
            if (baseTypeParameters.Count != baseArguments.Count)
            {
                diagnostics.Report(adapter.Location, $"Adapter base type '{adapter.BaseType}' expects {baseTypeParameters.Count} type argument(s).");
                continue;
            }

            adapterMethods.AddRange(adapter.Methods);
        }

        var declarations = new List<TopLevelNode>();
        var insertedAdapterMethods = false;
        foreach (var declaration in program.Declarations)
        {
            if (declaration is FunctionNode && !insertedAdapterMethods)
            {
                declarations.AddRange(adapterMethods);
                insertedAdapterMethods = true;
            }

            declarations.Add(declaration);
        }

        if (!insertedAdapterMethods)
        {
            declarations.AddRange(adapterMethods);
        }

        return program with { Declarations = declarations };
    }

    private static string GetGenericBaseName(string type)
    {
        type = type.Trim().TrimEnd('*').TrimEnd();
        var index = type.IndexOf('<');
        return index < 0 ? type : type[..index].Trim();
    }

    private static bool TryParseGenericUse(string type, out string name, out IReadOnlyList<string> arguments)
    {
        type = type.Trim();
        var open = type.IndexOf('<');
        if (open < 0 || !type.EndsWith('>'))
        {
            name = type;
            arguments = [];
            return false;
        }

        name = type[..open].Trim();
        arguments = SplitTopLevel(type[(open + 1)..^1])
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        return true;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return text[start..];
    }
}
