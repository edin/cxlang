namespace Cx.Compiler.Syntax.Nodes;

public sealed record TypeAliasNode(
    Location Location,
    string Name,
    string TargetType,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false) : TopLevelNode(Location);
