namespace Cx.Compiler.Syntax.Nodes;

public sealed record CDeclareNode(
    Location Location,
    string HeaderPath,
    bool IsSystemHeader,
    IReadOnlyList<CLinkNode> Links,
    IReadOnlyList<TypeAliasNode> TypeAliases,
    IReadOnlyList<EnumNode> Enums,
    IReadOnlyList<StructNode> Structs,
    IReadOnlyList<TaggedUnionNode> Unions,
    IReadOnlyList<GlobalVariableNode> Constants,
    IReadOnlyList<ExternFunctionNode> Functions) : TopLevelNode(Location);

public sealed record CLinkNode(
    Location Location,
    string? Platform,
    string Library) : SyntaxNode(Location);
