using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;

namespace Cx.Compiler.Lexer;

public sealed class Lexer
{
    private readonly SourceFile _sourceFile;
    private readonly string _input;
    private int _position;
    private int _line = 1;
    private int _column = 1;

    public Lexer(SourceFile sourceFile, DiagnosticBag? diagnostics = null)
    {
        _sourceFile = sourceFile;
        _input = sourceFile.Text;
        Diagnostics = diagnostics ?? new DiagnosticBag();
    }

    public DiagnosticBag Diagnostics { get; }

    public string Input => _input;

    public int Position => _position;

    public bool IsAtEnd => _position >= _input.Length;

    public char Current => _position < _input.Length ? _input[_position] : '\0';

    public string Remaining => _position < _input.Length ? _input[_position..] : string.Empty;

    public Location Location => new(_sourceFile, _position, _line, _column);

    public char Peek(int offset = 1)
    {
        var index = _position + offset;
        return index < _input.Length ? _input[index] : '\0';
    }

    public bool IsAt(string text)
    {
        if (string.IsNullOrEmpty(text) || _position + text.Length > _input.Length)
        {
            return false;
        }

        return string.Compare(_input, _position, text, 0, text.Length, StringComparison.Ordinal) == 0;
    }

    public bool TryTake(string text)
    {
        if (!IsAt(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            Advance();
        }

        return true;
    }

    public string TakeWhile(Func<char, bool> predicate)
    {
        var start = _position;

        while (!IsAtEnd && predicate(Current))
        {
            Advance();
        }

        return _input[start.._position];
    }

    public string TakeUntil(string text)
    {
        var start = _position;

        while (!IsAtEnd && !IsAt(text))
        {
            Advance();
        }

        return _input[start.._position];
    }

    public void Advance()
    {
        if (Current == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        _position++;
    }

    public IReadOnlyList<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (!IsAtEnd)
        {
            if (char.IsWhiteSpace(Current))
            {
                Advance();
                continue;
            }

            foreach (var definition in TokenDefinitions.All)
            {
                var savedPosition = _position;
                var savedLine = _line;
                var savedColumn = _column;

                var match = definition.Match(this);
                if (match is not null)
                {
                    tokens.Add(match);
                    goto NextToken;
                }

                _position = savedPosition;
                _line = savedLine;
                _column = savedColumn;
            }

            Diagnostics.Report(Location, $"Unexpected character '{Current}'.");
            Advance();

        NextToken:
            ;
        }

        tokens.Add(new Token(TokenType.Eof, string.Empty, _position, Location));
        return tokens;
    }
}
