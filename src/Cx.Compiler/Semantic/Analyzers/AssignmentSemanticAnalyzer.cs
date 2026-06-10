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
    TypeRefParser typeRefParser,
    Func<TypeNode?, string> typeText)
{
    public void AnalyzeAssignmentExpression(
        AssignmentExpressionNode assignment,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, LocalMutability>? mutability,
        Action<ExpressionNode, Location, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, LocalMutability>?> analyzeExpression)
    {
        analyzeExpression(assignment.Value, assignment.Location, variables, mutability);
        AnalyzeAssignmentMutability(assignment, mutability);

        var targetTypeRef = expressionTypeResolver.ResolveTypeRef(assignment.Target, variables);
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

            CheckAssignmentCompatibility(assignment.Location, targetTypeRef, assignment.Value, variables, "assignment");
            return;
        }

        CheckCompoundAssignmentCompatibility(assignment.Location, targetTypeRef, assignment.Operator, assignment.Value, variables);
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
        CheckAssignmentCompatibility(location, typeRefParser.Parse(targetType), sourceExpression, variables, subject);

    public void CheckAssignmentCompatibility(
        Location location,
        TypeRef? targetType,
        ExpressionNode? sourceExpression,
        IReadOnlyDictionary<string, string> variables,
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

        var sourceType = expressionTypeResolver.ResolveTypeRef(sourceExpression, variables);
        var targetTypeText = FormatTypeRef(targetType);
        var sourceTypeText = FormatTypeRef(sourceType);
        if (targetTypeText is not null && IsTaggedUnionVariantAssignment(targetTypeText, sourceTypeText))
        {
            return;
        }

        if (IsInterfaceBindingAssignment(targetType, sourceType))
        {
            return;
        }

        if (targetTypeText is not null && IsSelfPointerAssignment(targetTypeText, sourceTypeText))
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
        IReadOnlyDictionary<string, string> variables)
    {
        var valueType = expressionTypeResolver.ResolveTypeRef(value, variables);
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

    private bool IsTaggedUnionVariantAssignment(string targetType, string? sourceType)
    {
        if (sourceType is null)
        {
            return false;
        }

        targetType = StripPointer(ResolveAlias(targetType));
        sourceType = ResolveAlias(sourceType);
        var taggedUnion = program.TaggedUnions.FirstOrDefault(union =>
            !union.IsRaw
            && string.Equals(union.Name, targetType, StringComparison.Ordinal));
        return taggedUnion is not null
            && taggedUnion.Variants.Any(variant => SameTypeName(ResolveAlias(typeText(variant.TypeNode)), sourceType));
    }

    private string ResolveAlias(string type)
    {
        var pointerSuffix = string.Empty;
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1].TrimEnd();
            pointerSuffix += "*";
        }

        var aliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => typeText(group.First().TargetTypeNode), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(type, out var targetType) && seen.Add(type))
        {
            type = targetType;
        }

        return type + pointerSuffix;
    }

    private bool IsNumericLikeType(TypeRef type)
    {
        var unwrapped = UnwrapAlias(type);
        return unwrapped is TypeRef.Named named
            && IsNumericLikeType(named.Name);
    }

    private bool IsNumericLikeType(string type)
    {
        type = StripConst(StripPointerSuffix(ResolveAlias(type)).Trim());
        return type is
            "char" or
            "signed char" or
            "unsigned char" or
            "short" or
            "unsigned short" or
            "int" or
            "unsigned int" or
            "long" or
            "unsigned long" or
            "long long" or
            "unsigned long long" or
            "int8_t" or
            "uint8_t" or
            "int16_t" or
            "uint16_t" or
            "int32_t" or
            "uint32_t" or
            "int64_t" or
            "uint64_t" or
            "float" or
            "double" or
            "long double" or
            "size_t" or
            "usize" or
            "u8" or
            "u16" or
            "u32" or
            "u64" or
            "clock_t" or
            "bool";
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

    private static bool IsSelfPointerAssignment(string targetType, string? sourceType) =>
        string.Equals(sourceType?.Trim(), "Self*", StringComparison.Ordinal)
        && targetType.TrimEnd().EndsWith("*", StringComparison.Ordinal);

    private static bool IsNullableType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Pointer;

    private static bool IsPointerType(TypeRef type) =>
        UnwrapAlias(type) is TypeRef.Pointer;

    private static TypeRef? UnwrapAlias(TypeRef? type)
    {
        while (type is TypeRef.Alias alias)
        {
            type = alias.Target;
        }

        return type;
    }

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

    private static bool SameTypeName(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static bool IsBareNull(ExpressionNode expression) =>
        string.Equals(expression.SourceText.Trim(), "null", StringComparison.Ordinal);

    private static string StripPointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    private static string StripConst(string type) =>
        type.StartsWith("const ", StringComparison.Ordinal)
            ? type["const ".Length..].TrimStart()
            : type;

    private static string StripPointerSuffix(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }
}
