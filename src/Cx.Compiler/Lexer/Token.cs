using Cx.Compiler.Syntax;

namespace Cx.Compiler.Lexer;

public sealed record Token(TokenType Type, string Value, int Position, Location Location)
{
    public override string ToString() => $"{Type} '{Value}' at {Location.Line}:{Location.Column}";
}
