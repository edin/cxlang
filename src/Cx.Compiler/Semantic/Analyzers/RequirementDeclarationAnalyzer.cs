using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class RequirementDeclarationAnalyzer(
    DiagnosticBag diagnostics,
    ProgramNode program,
    RequirementMatcher requirementMatcher,
    Func<TypeNode?, string> typeText)
{
    public void AnalyzeGenericConstraints(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<GenericConstraintNode> constraints,
        Location location)
    {
        foreach (var constraint in constraints)
        {
            if (!typeParameters.Contains(constraint.TypeParameter, StringComparer.Ordinal))
            {
                diagnostics.Report(
                    constraint.Location,
                    $"Unknown generic type parameter '{constraint.TypeParameter}' in where clause.");
            }

            foreach (var requirement in constraint.Requirements)
            {
                AnalyzeRequirementReference(requirement, allowInferredTypeArguments: false);
            }
        }
    }

    public void AnalyzeStructRequirements(StructNode structNode)
    {
        foreach (var requirement in structNode.Requirements)
        {
            AnalyzeRequirementReference(requirement, allowInferredTypeArguments: true);
            var selfType = GetStructSelfType(structNode);
            var arguments = TypeArguments(requirement.TypeArgumentNodes).Count > 0
                ? TypeArguments(requirement.TypeArgumentNodes)
                : structNode.TypeParameters;
            var match = requirementMatcher.Match(selfType, requirement.Name, arguments);
            if (match.Success)
            {
                continue;
            }

            diagnostics.Report(
                requirement.Location,
                $"Struct '{selfType}' declares '{FormatRequirementReference(requirement)}' but does not satisfy it: {FormatRequirementFailures(match.Failures)}");
        }
    }

    private void AnalyzeRequirementReference(
        StructRequirementNode reference,
        bool allowInferredTypeArguments)
    {
        var requirement = program.Requirements.FirstOrDefault(requirement =>
            string.Equals(requirement.Name, reference.Name, StringComparison.Ordinal));
        if (requirement is not null)
        {
            var typeArguments = TypeArguments(reference.TypeArgumentNodes);
            if (typeArguments.Count > 0
                && typeArguments.Count != requirement.TypeParameters.Count)
            {
                diagnostics.Report(
                    reference.Location,
                    $"Requirement '{reference.Name}' expects {requirement.TypeParameters.Count} type argument(s), but {typeArguments.Count} were provided.");
            }
            else if (!allowInferredTypeArguments
                && typeArguments.Count == 0
                && requirement.TypeParameters.Count > 0)
            {
                diagnostics.Report(
                    reference.Location,
                    $"Requirement '{reference.Name}' in a where clause needs explicit type arguments: {reference.Name}<{string.Join(", ", requirement.TypeParameters)}>.");
            }

            return;
        }

        var interfaceNode = program.Interfaces.FirstOrDefault(interfaceNode =>
            string.Equals(interfaceNode.Name, reference.Name, StringComparison.Ordinal));
        if (interfaceNode is not null)
        {
            if (TypeArguments(reference.TypeArgumentNodes).Count > 0)
            {
                diagnostics.Report(
                    reference.Location,
                    $"Interface '{reference.Name}' does not take type arguments.");
            }

            return;
        }

        diagnostics.Report(reference.Location, $"Unknown requirement '{reference.Name}'.");
    }

    private string FormatRequirementReference(StructRequirementNode requirement) =>
        TypeArguments(requirement.TypeArgumentNodes).Count == 0
            ? requirement.Name
            : $"{requirement.Name}<{string.Join(", ", TypeArguments(requirement.TypeArgumentNodes))}>";

    private IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> nodes) =>
        nodes.Select(typeText).ToList();

    private static string GetStructSelfType(StructNode structNode) =>
        structNode.TypeParameters.Count == 0
            ? structNode.Name
            : $"{structNode.Name}<{string.Join(",", structNode.TypeParameters)}>";

    public static string FormatRequirementFailures(IReadOnlyList<string> failures) =>
        failures.Count == 0
            ? "no details available."
            : string.Join(" ", failures.Select(failure => failure.Trim()));
}
