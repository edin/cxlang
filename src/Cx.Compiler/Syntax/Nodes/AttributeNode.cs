namespace Cx.Compiler.Syntax.Nodes;

public sealed record AttributeDeclarationNode(
    Location Location,
    string Name,
    IReadOnlyList<string> Targets,
    IReadOnlyList<AttributeFieldNode> Fields) : TopLevelNode(Location);

public sealed record AttributeFieldNode(
    Location Location,
    string Name,
    TypeNode? TypeNode = null) : SyntaxNode(Location)
{
    public AttributeFieldNode(
        Location Location,
        string Name,
        string Type)
        : this(Location, Name, TypeNode.CreateFromText(Location, Type))
    {
    }
}

public sealed record AttributeApplicationNode(
    Location Location,
    string Name,
    IReadOnlyList<AttributeArgumentNode> Arguments) : SyntaxNode(Location);

public sealed record AttributeArgumentNode(
    Location Location,
    string? Name,
    string Value) : SyntaxNode(Location);
