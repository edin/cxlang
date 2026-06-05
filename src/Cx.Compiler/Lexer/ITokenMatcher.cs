namespace Cx.Compiler.Lexer;

public interface ITokenMatcher
{
    Token? Match(Lexer lexer);
}
