using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class AssignmentSemanticAnalyzer(
    DiagnosticBag diagnostics,
    ProgramNode program,
    ExpressionTypeResolver expressionTypeResolver,
    TypeCompatibility typeCompatibility,
    TypeSystem typeSystem,
    TypeRefParser typeRefParser)
{
    public void AnalyzeAssignmentExpression(
        AssignmentExpressionNode assignment,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, LocalMutability>? mutability,
        Action<ExpressionNode, Location, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, LocalMutability>?> analyzeExpression) =>
        AnalyzeAssignmentExpression(
            assignment,
            variables,
            TypeEnvironment.FromLegacyStrings(typeRefParser, variables),
            mutability,
            analyzeExpression);

    public void AnalyzeAssignmentExpression(
        AssignmentExpressionNode assignment,
        IReadOnlyDictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        IReadOnlyDictionary<string, LocalMutability>? mutability,
        Action<ExpressionNode, Location, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, LocalMutability>?> analyzeExpression)
    {
        analyzeExpression(assignment.Value, assignment.Location, variables, mutability);
        AnalyzeAssignmentMutability(assignment, mutability);

        var targetTypeRef = expressionTypeResolver.ResolveTypeRef(assignment.Target, typeEnvironment);
        if (targetTypeRef is null)
        {
            return;
        }

        if (assignment.Operator == "=")
        {
            if (IsBareNull(assignment.Value) && !IsNullableType(targetTypeRef))
            {
                diagnostics.Report(assignment.Location, $"Cannot assign null to non-pointer type '{FormatTypeRef(targetTypeRef)}'.");
            }

            CheckAssignmentCompatibility(assignment.Location, targetTypeRef, assignment.Value, typeEnvironment, "assignment");
            return;
        }

        CheckCompoundAssignmentCompatibility(assignment.Location, targetTypeRef, assignment.Operator, assignment.Value, typeEnvironment);
    }

    public void AnalyzeMutationTarget(
        ExpressionNode target,
        Location location,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        if (mutability is null || GetAssignmentRootName(target) is not { } name)
        {
            return;
        }

        if (!mutability.TryGetValue(name, out var localMutability))
        {
            return;
        }

        var message = localMutability switch
        {
            LocalMutability.Const => $"Cannot assign to const local '{name}'.",
            LocalMutability.ConstGlobal => $"Cannot assign to const global '{name}'.",
            LocalMutability.ForeachIndex => $"Cannot assign to foreach index '{name}'.",
            LocalMutability.ForeachKey => $"Cannot assign to foreach key '{name}'.",
            LocalMutability.ForeachConstItem => $"Cannot assign to const foreach item '{name}'.",
            _ => null,
        };

        if (message is not null)
        {
            diagnostics.Report(location, message);
        }
    }

    public void CheckAssignmentCompatibility(
        Location location,
        string targetType,
        ExpressionNode? sourceExpression,
        IReadOnlyDictionary<string, string> variables,
        string subject) =>
        CheckAssignmentCompatibility(
            location,
            typeRefParser.Parse(targetType),
            sourceExpression,
            TypeEnvironment.FromLegacyStrings(typeRefParser, variables),
            subject);

    public void CheckAssignmentCompatibility(
        Location location,
        TypeRef? targetType,
        ExpressionNode? sourceExpression,
        IReadOnlyDictionary<string, string> variables,
        string subject) =>
        CheckAssignmentCompatibility(
            location,
            targetType,
            sourceExpression,
            TypeEnvironment.FromLegacyStrings(typeRefParser, variables),
            subject);

    public void CheckAssignmentCompatibility(
        Location location,
        string targetType,
        ExpressionNode? sourceExpression,
        TypeEnvironment typeEnvironment,
        string subject) =>
        CheckAssignmentCompatibility(location, typeRefParser.Parse(targetType), sourceExpression, typeEnvironment, subject);

    public void CheckAssignmentCompatibility(
        Location location,
        TypeRef? targetType,
        ExpressionNode? sourceExpression,
        TypeEnvironment typeEnvironment,
        string subject)
    {
        if (targetType is null || sourceExpression is null)
        {
            return;
        }

        if (sourceExpression is InitializerExpressionNode { TypeNameNode: null })
        {
            return;
        }

        var sourceType = expressionTypeResolver.ResolveTypeRef(sourceExpression, typeEnvironment);
        if (IsTaggedUnionVariantAssignment(targetType, sourceType))
        {
            return;
        }

        if (IsInterfaceBindingAssignment(targetType, sourceType))
        {
            return;
        }

        if (IsSelfPointerAssignment(targetType, sourceType))
        {
            return;
        }

        if (!typeCompatibility.CanAssign(targetType, sourceType, out var reason))
        {
            diagnostics.Report(location, $"Type mismatch for {subject}: {reason}.");
        }
    }

    private void AnalyzeAssignmentMutability(
        AssignmentExpressionNode assignment,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        AnalyzeMutationTarget(assignment.Target, assignment.Location, mutability);
    }

    private void CheckCompoundAssignmentCompatibility(
        Location location,
        TypeRef targetType,
        string assignmentOperator,
        ExpressionNode value,
        TypeEnvironment typeEnvironment)
    {
        var valueType = expressionTypeResolver.ResolveTypeRef(value, typeEnvironment);
        if (valueType is null)
        {
            return;
        }

        if (IsPointerType(targetType)
            && assignmentOperator is "+=" or "-="
            && IsNumericLikeType(valueType))
        {
            return;
        }

        if (IsNumericLikeType(targetType)
            && IsNumericLikeType(valueType))
        {
            return;
        }

        if (assignmentOperator == "+="
            && typeCompatibility.CanAssign(targetType, valueType, out _))
        {
            return;
        }

        diagnostics.Report(
            location,
            $"Type mismatch for compound assignment: cannot apply '{assignmentOperator}' to '{FormatTypeRef(targetType)}' and '{FormatTypeRef(valueType)}'.");
    }

    private bool IsInterfaceBindingAssignment(TypeRef targetType, TypeRef? sourceType)
    {
        if (sourceType is null)
        {
            return false;
        }

        var target = typeSystem.ResolveDefinition(targetType);
        if (target.Symbol is not TypeSymbol.Interface interfaceSymbol)
        {
            return false;
        }

        return typeSystem.SatisfiesRequirement(sourceType, interfaceSymbol.Name) is { Success: true };
    }

    private bool IsTaggedUnionVariantAssignment(TypeRef targetType, TypeRef? sourceType)
    {
        if (sourceType is null)
        {
            return false;
        }

        var targetTypeName = TypeRefFacts.GetBaseName(targetType);
        var taggedUnion = program.TaggedUnions.FirstOrDefault(union =>
            !union.IsRaw
            && string.Equals(union.Name, targetTypeName, StringComparison.Ordinal));
        return taggedUnion is not null
            && taggedUnion.Variants.Any(variant => SameType(variant.TypeNode?.ToTypeRef(typeRefParser), sourceType));
    }

    private bool IsNumericLikeType(TypeRef type)
    {
        var unwrapped = TypeRefFacts.UnwrapAlias(type);
        return unwrapped is TypeRef.Named named
            && IsNumericLikeType(named.Name);
    }

    private bool IsNumericLikeType(string type)
    {
        type = StripConst(type.Trim());
        return BuiltinTypes.IsNumeric(type)
            || string.Equals(BuiltinTypes.Normalize(type), "bool", StringComparison.Ordinal);
    }

    private static string? GetAssignmentRootName(ExpressionNode target) => target switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetAssignmentRootName(parenthesized.Expression),
        MemberExpressionNode member => GetAssignmentRootName(member.Target),
        IndexExpressionNode index => GetAssignmentRootName(index.Target),
        UnaryExpressionNode { Operator: "*" } unary => GetAssignmentRootName(unary.Operand),
        _ => null,
    };

    private static bool IsSelfPointerAssignment(TypeRef targetType, TypeRef? sourceType) =>
        TypeRefFacts.TryGetPointerElement(sourceType, out var sourceElement)
        && TypeRefFacts.UnwrapAlias(sourceElement) is TypeRef.Named { Name: "Self", Arguments.Count: 0 }
        && TypeRefFacts.IsPointer(targetType);

    private static bool IsNullableType(TypeRef? type) =>
        TypeRefFacts.IsPointer(type);

    private static bool IsPointerType(TypeRef type) =>
        TypeRefFacts.IsPointer(type);

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

    private static bool SameType(TypeRef? left, TypeRef? right) =>
        TypeRefFacts.SameType(left, right);

    private static bool IsBareNull(ExpressionNode expression) =>
        string.Equals(expression.SourceText.Trim(), "null", StringComparison.Ordinal);

    private static string StripConst(string type) =>
        type.StartsWith("const ", StringComparison.Ordinal)
            ? type["const ".Length..].TrimStart()
            : type;
}
