using Cx.Compiler.Lowering;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.C;

internal sealed class CAbiNameService(IReadOnlyList<TypeAdapterNode> typeAdapters)
{
    public string LowerType(string type, string? selfType = null) =>
        CTypeLowerer.LowerType(type, typeAdapters, selfType);

    public string LowerType(TypeRef type, TypeRef? selfType = null) =>
        CTypeLowerer.LowerType(type, typeAdapters, selfType);

    public string LowerType(TypeNode? typeNode, string fallbackType, string? selfType = null) =>
        TryUseStructuredType(typeNode, fallbackType, out var type)
            ? CTypeLowerer.LowerType(type, typeAdapters, GenericTypeSubstitutionBuilder.ParseType(selfType))
            : LowerType(fallbackType, selfType);

    public string LowerDeclaration(string type, string name, string? selfType = null) =>
        CTypeLowerer.LowerDeclaration(type, name, typeAdapters, selfType);

    public string LowerDeclaration(TypeNode? typeNode, string fallbackType, string name, string? selfType = null) =>
        TryUseStructuredType(typeNode, fallbackType, out var type)
            ? CTypeLowerer.LowerDeclaration(type, name, typeAdapters, GenericTypeSubstitutionBuilder.ParseType(selfType))
            : LowerDeclaration(fallbackType, name, selfType);

    public string LowerParameterDeclaration(ParameterNode parameter, string fallbackType, string? selfType = null) =>
        CTypeLowerer.LowerParameterDeclaration(
            parameter,
            TryUseStructuredType(parameter.TypeNode, fallbackType, out var type) ? type : null,
            typeAdapters,
            GenericTypeSubstitutionBuilder.ParseType(selfType));

    public string LowerFunctionTypeParameter(string parameter, string? selfType = null) =>
        parameter.Trim() == "..." ? "..." : LowerType(parameter, selfType);

    public string SanitizeTypeName(string type) =>
        CTypeLowerer.SanitizeTypeName(type);

    public string TypeIdName(string typeName) =>
        "CX_TYPE_" + SanitizeTypeName(LowerType(typeName));

    public string InterfaceVTableName(string interfaceName) =>
        $"{interfaceName}VTable";

    public string InterfaceVTableInstanceName(string structName, string interfaceName) =>
        $"{structName}_{interfaceName}_vtable";

    private static bool TryUseStructuredType(TypeNode? typeNode, string fallbackType, out TypeRef type)
    {
        type = null!;
        if (typeNode?.Semantic.Type is not { } semanticType)
        {
            return false;
        }

        var semanticText = TypeRefFormatter.ToCxString(semanticType);
        if (!string.Equals(semanticText, fallbackType, StringComparison.Ordinal)
            && !string.Equals(semanticText, typeNode.TypeName, StringComparison.Ordinal))
        {
            return false;
        }

        type = semanticType;
        return true;
    }
}
