namespace Cx.Compiler.Lexer;

[AttributeUsage(AttributeTargets.Field)]
public sealed class TokenAttribute : Attribute
{
    public TokenAttribute(TokenClass tokenClass)
    {
        Class = tokenClass;
    }

    public TokenAttribute(string text, TokenClass tokenClass)
    {
        Text = text;
        Class = tokenClass;
    }

    public string? Text { get; }

    public TokenClass Class { get; }
}
