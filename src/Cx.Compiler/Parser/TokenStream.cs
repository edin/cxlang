using Cx.Compiler.Lexer;

namespace Cx.Compiler.Parser;

internal sealed class TokenStream
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _position;

    public TokenStream(IReadOnlyList<Token> tokens)
    {
        if (tokens.Count == 0)
        {
            throw new ArgumentException("TokenStream requires at least one token.", nameof(tokens));
        }

        _tokens = tokens;
    }

    public int Position => _position;

    public Token Current => At(_position);

    public bool IsAtEnd => Current.Type == TokenType.Eof;

    public Token Peek(int offset = 1) => At(_position + offset);

    public TokenType PeekType(int offset = 1) => Peek(offset).Type;

    public Token Advance()
    {
        var current = Current;
        if (!IsAtEnd)
        {
            _position++;
        }

        return current;
    }

    public bool Check(TokenType type) => Current.Type == type;

    public bool CurrentIs(TokenType type) => Check(type);

    public bool PeekIs(TokenType type, int offset = 1) => Peek(offset).Type == type;

    public bool CheckAny(params TokenType[] types)
    {
        foreach (var type in types)
        {
            if (Check(type))
            {
                return true;
            }
        }

        return false;
    }

    public Token? Match(TokenType type) => Check(type) ? Advance() : null;

    public Token? MatchAny(params TokenType[] types)
    {
        foreach (var type in types)
        {
            var match = Match(type);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public T? OneOf<T>(params Func<T?>[] parsers)
    {
        foreach (var parser in parsers)
        {
            var position = Save();
            var result = parser();
            if (result is not null)
            {
                return result;
            }

            Restore(position);
        }

        return default;
    }

    public T? Optional<T>(Func<T?> parser)
    {
        var position = Save();
        var result = parser();
        if (result is not null)
        {
            return result;
        }

        Restore(position);
        return default;
    }

    public List<T> Many<T>(Func<T?> parser)
    {
        var items = new List<T>();

        while (!IsAtEnd)
        {
            var position = Save();
            var result = parser();
            if (result is null)
            {
                Restore(position);
                break;
            }

            items.Add(result);

            if (Save() == position)
            {
                throw new InvalidOperationException("TokenStream.Many parser must consume at least one token.");
            }
        }

        return items;
    }

    public List<T>? OneOrMore<T>(Func<T?> parser)
    {
        var items = Many(parser);
        return items.Count == 0 ? null : items;
    }

    public int Save() => _position;

    public void Restore(int position)
    {
        _position = Math.Clamp(position, 0, _tokens.Count - 1);
    }

    private Token At(int position)
    {
        if (position < 0)
        {
            return _tokens[0];
        }

        return position < _tokens.Count ? _tokens[position] : _tokens[^1];
    }
}
