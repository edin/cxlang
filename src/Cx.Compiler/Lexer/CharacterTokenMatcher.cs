using System.Text;

namespace Cx.Compiler.Lexer;

public sealed class CharacterTokenMatcher : ITokenMatcher
{
    public Token? Match(Lexer lexer)
    {
        if (lexer.IsAtEnd || lexer.Current != '\'')
        {
            return null;
        }

        var location = lexer.Location;
        var builder = new StringBuilder();
        builder.Append(lexer.Current);
        lexer.Advance();

        while (!lexer.IsAtEnd)
        {
            if (lexer.Current == '\\')
            {
                builder.Append(lexer.Current);
                lexer.Advance();

                if (!lexer.IsAtEnd)
                {
                    builder.Append(lexer.Current);
                    lexer.Advance();
                }

                continue;
            }

            if (lexer.Current == '\'')
            {
                builder.Append(lexer.Current);
                lexer.Advance();
                return new Token(TokenType.Character, builder.ToString(), location.Position, location);
            }

            builder.Append(lexer.Current);
            lexer.Advance();
        }

        lexer.Diagnostics.Report(location, "Unterminated character literal.");
        return new Token(TokenType.Character, builder.ToString(), location.Position, location);
    }
}
