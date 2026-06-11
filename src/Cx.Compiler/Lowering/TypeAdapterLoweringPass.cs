using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;
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
        var typeRefParser = new TypeRefParser(program);

        var adapterMethods = new List<FunctionNode>();
        foreach (var adapter in program.TypeAdapters)
        {
            var baseType = TypeText(adapter.BaseTypeNode, typeRefParser);
            var baseName = GetGenericBaseName(baseType);
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
                diagnostics.Report(adapter.Location, $"Adapter base type '{baseType}' was not found.");
                continue;
            }

            var baseArguments = TryParseGenericUse(baseType, out _, out var parsedBaseArguments)
                ? parsedBaseArguments
                : [];
            if (baseTypeParameters.Count != baseArguments.Count)
            {
                diagnostics.Report(adapter.Location, $"Adapter base type '{baseType}' expects {baseTypeParameters.Count} type argument(s).");
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
        type = StripPointerSuffix(type);
        return TypeSyntaxParser.Parse(type) is GenericTypeSyntaxNode generic
            ? TypeSyntaxFormatter.ToCxString(generic.Target)
            : type;
    }

    private static bool TryParseGenericUse(string type, out string name, out IReadOnlyList<string> arguments)
    {
        type = StripPointerSuffix(type);
        if (TypeSyntaxParser.Parse(type) is not GenericTypeSyntaxNode generic)
        {
            name = type;
            arguments = [];
            return false;
        }

        name = TypeSyntaxFormatter.ToCxString(generic.Target);
        arguments = generic.Arguments.Select(TypeSyntaxFormatter.ToCxString).ToList();
        return true;
    }

    private static string StripPointerSuffix(string type)
    {
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            type = type[..^1].TrimEnd();
        }

        return type;
    }

    private static string TypeText(TypeNode? typeNode, TypeRefParser typeRefParser)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        var type = typeNode.ToTypeRef(typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

}
