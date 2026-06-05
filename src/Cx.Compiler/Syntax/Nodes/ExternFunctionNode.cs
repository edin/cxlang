namespace Cx.Compiler.Syntax.Nodes;

public sealed record ExternFunctionNode(
    Location Location,
    string Name,
    IReadOnlyList<string> TypeParameters,
    string ReturnType,
    IReadOnlyList<ParameterNode> Parameters,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false,
    bool IsMacro = false) : TopLevelNode(Location);
