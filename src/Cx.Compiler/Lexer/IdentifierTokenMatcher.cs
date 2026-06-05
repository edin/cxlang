namespace Cx.Compiler.Lexer;

public sealed class IdentifierTokenMatcher : ITokenMatcher
{
    private readonly IReadOnlyDictionary<string, TokenType> _tokenMap;

    public IdentifierTokenMatcher(IReadOnlyDictionary<string, TokenType> tokenMap)
    {
        _tokenMap = tokenMap;
    }

    public Token? Match(Lexer lexer)
    {
        if (lexer.IsAtEnd)
        {
            return null;
        }

        if (!char.IsLetter(lexer.Current) && lexer.Current != '_')
        {
            return null;
        }

        var location = lexer.Location;
        var value = lexer.TakeWhile(ch => char.IsLetterOrDigit(ch) || ch == '_');
        var type = _tokenMap.TryGetValue(value, out var mappedType)
            ? mappedType
            : TokenType.Identifier;

        return new Token(type, value, location.Position, location);
    }
}
