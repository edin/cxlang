namespace Cx.Compiler.Syntax.Nodes;

public sealed record ExternFunctionNode(
    Location Location,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<ParameterNode> Parameters,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false,
    bool IsMacro = false,
    TypeNode? ReturnTypeNode = null) : TopLevelNode(Location)
{
    [Obsolete("Use ReturnTypeNode instead of the string compatibility property.")]
    public string ReturnType => ReturnTypeNode?.TypeName ?? string.Empty;
}
