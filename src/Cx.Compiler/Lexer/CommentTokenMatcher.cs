namespace Cx.Compiler.Lexer;

public sealed class CommentTokenMatcher : ITokenMatcher
{
    public Token? Match(Lexer lexer)
    {
        var location = lexer.Location;

        if (lexer.TryTake("//"))
        {
            var value = lexer.TakeWhile(ch => ch is not '\r' and not '\n');
            return new Token(TokenType.Comment, value, location.Position, location);
        }

        if (lexer.TryTake("/*"))
        {
            var value = lexer.TakeUntil("*/");
            if (lexer.TryTake("*/"))
            {
                return new Token(TokenType.MultilineComment, value, location.Position, location);
            }

            lexer.Diagnostics.Report(location, "Unterminated multiline comment.");
            return new Token(TokenType.MultilineComment, value, location.Position, location);
        }

        return null;
    }
}
