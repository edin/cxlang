namespace Cx.Compiler.Syntax.Nodes;

public sealed record ImportNode(
    Location Location,
    string ModuleName,
    string? Alias) : TopLevelNode(Location);

public sealed record SymbolImportNode(
    Location Location,
    string ModuleName,
    IReadOnlyList<ImportedSymbolNode> Symbols) : TopLevelNode(Location);

public sealed record ImportedSymbolNode(
    Location Location,
    string Name,
    string? Alias) : SyntaxNode(Location);
