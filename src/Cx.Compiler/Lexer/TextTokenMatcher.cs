namespace Cx.Compiler.Lexer;

public sealed class TextTokenMatcher : ITokenMatcher
{
    private readonly TokenType _type;
    private readonly string _text;

    public TextTokenMatcher(TokenType type, string text)
    {
        _type = type;
        _text = text;
    }

    public Token? Match(Lexer lexer)
    {
        var location = lexer.Location;

        if (!lexer.TryTake(_text))
        {
            return null;
        }

        return new Token(_type, _text, location.Position, location);
    }
}
