namespace Cx.Compiler.Lexer;

public sealed class NumberTokenMatcher : ITokenMatcher
{
    public Token? Match(Lexer lexer)
    {
        if (lexer.IsAtEnd || !char.IsDigit(lexer.Current))
        {
            return null;
        }

        var location = lexer.Location;
        var value = lexer.TakeWhile(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_');
        return new Token(TokenType.Number, value, location.Position, location);
    }
}
