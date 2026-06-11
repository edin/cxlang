using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class AttributeSemanticAnalyzer(DiagnosticBag diagnostics)
{
    public void Analyze(ProgramNode program, Action<TypeNode?, Location, IReadOnlyList<string>> analyzeType)
    {
        var declarations = BuiltInAttributeDeclarations()
            .Concat(program.AttributeDeclarations)
            .GroupBy(attribute => attribute.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (var group in program.AttributeDeclarations.GroupBy(attribute => attribute.Name, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                diagnostics.Report(group.Last().Location, $"Attribute '{group.Key}' is declared more than once.");
            }
        }

        foreach (var declaration in program.AttributeDeclarations)
        {
            foreach (var field in declaration.Fields)
            {
                analyzeType(field.TypeNode, field.Location, []);
            }
        }

        foreach (var typeAlias in program.TypeAliases)
        {
            AnalyzeAttributeApplications(typeAlias.Attributes, "type_alias", declarations);
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            AnalyzeAttributeApplications(externFunction.Attributes, "extern", declarations);
            foreach (var parameter in externFunction.Parameters)
            {
                AnalyzeAttributeApplications(parameter.Attributes, "parameter", declarations);
            }
        }

        foreach (var global in program.GlobalVariables)
        {
            AnalyzeAttributeApplications(global.Attributes, "global", declarations);
        }

        foreach (var enumNode in program.Enums)
        {
            AnalyzeAttributeApplications(enumNode.Attributes, "enum", declarations);
            foreach (var member in enumNode.Members)
            {
                AnalyzeAttributeApplications(member.Attributes, "variant", declarations);
            }
        }

        foreach (var structNode in program.Structs)
        {
            AnalyzeAttributeApplications(structNode.Attributes, "struct", declarations);
            foreach (var field in structNode.Fields)
            {
                AnalyzeAttributeApplications(field.Attributes, "field", declarations);
            }
        }

        foreach (var taggedUnion in program.TaggedUnions)
        {
            AnalyzeAttributeApplications(taggedUnion.Attributes, "union", declarations);
            foreach (var variant in taggedUnion.Variants)
            {
                AnalyzeAttributeApplications(variant.Attributes, "variant", declarations);
            }
        }

        foreach (var function in program.Functions)
        {
            AnalyzeFunctionAttributes(function, declarations);
        }
    }

    private void AnalyzeFunctionAttributes(
        FunctionNode function,
        IReadOnlyDictionary<string, AttributeDeclarationNode> declarations)
    {
        AnalyzeAttributeApplications(function.Attributes, "fn", declarations);
        foreach (var parameter in function.Parameters)
        {
            AnalyzeAttributeApplications(parameter.Attributes, "parameter", declarations);
        }
    }

    private void AnalyzeAttributeApplications(
        IReadOnlyList<AttributeApplicationNode> applications,
        string target,
        IReadOnlyDictionary<string, AttributeDeclarationNode> declarations)
    {
        foreach (var duplicate in applications
            .GroupBy(attribute => attribute.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            diagnostics.Report(duplicate.Last().Location, $"Attribute '{duplicate.Key}' cannot be applied more than once.");
        }

        foreach (var application in applications)
        {
            if (!declarations.TryGetValue(application.Name, out var declaration))
            {
                diagnostics.Report(application.Location, $"Unknown attribute '{application.Name}'.");
                continue;
            }

            if (!declaration.Targets.Contains(target, StringComparer.Ordinal))
            {
                diagnostics.Report(application.Location, $"Attribute '{application.Name}' cannot be applied to {target}.");
            }

            if (application.Name == "derive")
            {
                continue;
            }

            var namedArguments = application.Arguments.Where(argument => argument.Name is not null).ToList();
            foreach (var argument in namedArguments)
            {
                if (!declaration.Fields.Any(field => field.Name == argument.Name))
                {
                    diagnostics.Report(argument.Location, $"Attribute '{application.Name}' has no field named '{argument.Name}'.");
                }
            }

            if (namedArguments.Count > 0)
            {
                foreach (var field in declaration.Fields)
                {
                    if (!namedArguments.Any(argument => argument.Name == field.Name))
                    {
                        diagnostics.Report(application.Location, $"Attribute '{application.Name}' requires argument '{field.Name}'.");
                    }
                }

                continue;
            }

            if (application.Arguments.Count != declaration.Fields.Count)
            {
                diagnostics.Report(application.Location, $"Attribute '{application.Name}' expects {declaration.Fields.Count} argument(s).");
            }
        }
    }

    private static IReadOnlyList<AttributeDeclarationNode> BuiltInAttributeDeclarations() =>
    [
        new AttributeDeclarationNode(
            Location.Synthetic("<built-in>"),
            "derive",
            ["struct", "union", "enum"],
            [])
    ];
}
