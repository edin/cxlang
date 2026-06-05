using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class ExtensionMergePass
{
    public static ProgramNode Apply(ProgramNode program, DiagnosticBag diagnostics)
    {
        if (program.Extensions.Count == 0)
        {
            return program;
        }

        var extensionsByTarget = program.Extensions
            .GroupBy(extension => extension.TargetType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var structs = program.Structs
            .Select(structNode => MergeStructExtensions(structNode, extensionsByTarget))
            .ToList();
        var extensionMethods = program.Extensions
            .SelectMany(extension => extension.Methods)
            .ToList();
        var knownTargets = program.Structs
            .Select(structNode => structNode.Name)
            .Concat(program.TypeAdapters.Select(adapter => adapter.Name))
            .Concat(program.Interfaces.Select(interfaceNode => interfaceNode.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var extension in program.Extensions)
        {
            if (!knownTargets.Contains(extension.TargetType))
            {
                diagnostics.Report(extension.Location, $"Extension target type '{extension.TargetType}' was not found.");
            }
        }

        var declarations = new List<TopLevelNode>();
        var insertedStructs = false;
        foreach (var declaration in program.Declarations)
        {
            switch (declaration)
            {
                case ExtensionNode:
                    continue;
                case StructNode:
                    if (!insertedStructs)
                    {
                        declarations.AddRange(structs);
                        insertedStructs = true;
                    }

                    continue;
                case FunctionNode:
                    if (extensionMethods.Count > 0)
                    {
                        declarations.AddRange(extensionMethods);
                        extensionMethods = [];
                    }

                    declarations.Add(declaration);
                    break;
                default:
                    declarations.Add(declaration);
                    break;
            }
        }

        if (!insertedStructs)
        {
            declarations.AddRange(structs);
        }

        declarations.AddRange(extensionMethods);

        return program with { Declarations = declarations };
    }

    private static StructNode MergeStructExtensions(
        StructNode structNode,
        IReadOnlyDictionary<string, List<ExtensionNode>> extensionsByTarget)
    {
        if (!extensionsByTarget.TryGetValue(structNode.Name, out var extensions))
        {
            return structNode;
        }

        return structNode with
        {
            Methods = structNode.Methods
                .Concat(extensions.SelectMany(extension => extension.Methods))
                .ToList(),
        };
    }
}
