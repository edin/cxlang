namespace Cx.Compiler.Lexer;

public static class KeywordDefinitions
{
    public static readonly IReadOnlyDictionary<TokenType, string> All =
        new Dictionary<TokenType, string>
        {
            [TokenType.Fn] = "fn",
            [TokenType.Static] = "static",
            [TokenType.Let] = "let",
            [TokenType.Const] = "const",
            [TokenType.Return] = "return",
            [TokenType.Module] = "module",
            [TokenType.Import] = "import",
            [TokenType.From] = "from",
            [TokenType.As] = "as",
            [TokenType.Include] = "include",
            [TokenType.Declare] = "declare",
            [TokenType.Link] = "link",
            [TokenType.Macro] = "macro",
            [TokenType.Extern] = "extern",
            [TokenType.Raw] = "raw",
            [TokenType.Struct] = "struct",
            [TokenType.Extension] = "extension",
            [TokenType.Interface] = "interface",
            [TokenType.Enum] = "enum",
            [TokenType.Type] = "type",
            [TokenType.Using] = "using",
            [TokenType.Over] = "over",
            [TokenType.Expose] = "expose",
            [TokenType.Opaque] = "opaque",
            [TokenType.Union] = "union",
            [TokenType.If] = "if",
            [TokenType.Else] = "else",
            [TokenType.Switch] = "switch",
            [TokenType.Case] = "case",
            [TokenType.Default] = "default",
            [TokenType.Break] = "break",
            [TokenType.Continue] = "continue",
            [TokenType.While] = "while",
            [TokenType.For] = "for",
            [TokenType.Foreach] = "foreach",
            [TokenType.In] = "in",
            [TokenType.Requires] = "requires",
            [TokenType.Where] = "where",
            [TokenType.Match] = "match",
            [TokenType.True] = "true",
            [TokenType.False] = "false",
            [TokenType.Null] = "null",
            [TokenType.Attribute] = "attribute",
            [TokenType.On] = "on",
        };

    public static readonly IReadOnlyDictionary<string, TokenType> TokenTypes =
        All.ToDictionary(
            pair => pair.Value,
            pair => pair.Key,
            StringComparer.Ordinal);
}
