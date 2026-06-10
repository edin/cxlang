using Cx.Compiler.Lexer;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Parser;

public sealed partial class Parser
{
    private TypeNode ParseTypeNode()
    {
        var location = Current.Location;
        var tokens = ParseTypeTokens();
        if (tokens.Count == 0)
        {
            _diagnostics.Report(Current.Location, "Expected type name.");
            return CreateTypeNode(location, string.Empty);
        }

        return TypeTokenParser.Parse(tokens);
    }

    private List<Token> ParseTypeTokens()
    {
        if (Check(TokenType.Fn))
        {
            return ParseFunctionTypeTokens();
        }

        var tokens = ParsePrimaryTypeTokens();
        if (tokens.Count == 0)
        {
            return tokens;
        }

        if (Match(TokenType.LessThan) is { } openGeneric)
        {
            tokens.Add(openGeneric);

            if (!CheckTypeCloseAngle())
            {
                do
                {
                    tokens.AddRange(ParseTypeTokens());
                }
                while (Match(TokenType.Comma) is { } comma && AddToken(tokens, comma));
            }

            if (ConsumeTypeCloseAngle() is { FromPending: false, Token: { } closeGeneric })
            {
                tokens.Add(closeGeneric);
            }
            else if (!WasLastTypeCloseAngleFromPending)
            {
                _diagnostics.Report(Current.Location, "Expected '>' after generic type arguments.");
            }
        }

        if (_pendingTypeCloseAngles == 0)
        {
            while (Match(TokenType.Star) is { } pointer)
            {
                tokens.Add(pointer);
            }

            while (Match(TokenType.LBracket) is { } openArray)
            {
                tokens.Add(openArray);
                tokens.AddRange(Tokens.ReadBalancedUntil(TokenType.RBracket));
                if (Expect(TokenType.RBracket, "Expected ']' after array length.") is { } closeArray)
                {
                    tokens.Add(closeArray);
                }
            }
        }

        return tokens;
    }

    private List<Token> ParsePrimaryTypeTokens()
    {
        var tokens = new List<Token>();

        if (Current.Type is TokenType.Struct or TokenType.Enum or TokenType.Union)
        {
            tokens.Add(Advance());
            if (Expect(TokenType.Identifier, "Expected C tag type name.") is { } tagName)
            {
                tokens.Add(tagName);
            }

            return tokens;
        }

        var isConst = Current.Type == TokenType.Const;
        var first = isConst
            ? Advance()
            : Expect(TokenType.Identifier, "Expected type name.");

        if (first is null)
        {
            return tokens;
        }

        tokens.Add(first);
        if (isConst && Current.Type == TokenType.Identifier)
        {
            tokens.Add(Advance());
        }

        while (Match(TokenType.Dot) is { } dot)
        {
            tokens.Add(dot);
            if (Expect(TokenType.Identifier, "Expected type name after '.'.") is { } part)
            {
                tokens.Add(part);
            }
        }

        while (Current.Type == TokenType.Identifier && IsTypeNameContinuation(Current.Value))
        {
            tokens.Add(Advance());
        }

        return tokens;
    }

    private static bool IsTypeNameContinuation(string value) =>
        value is "void" or "char" or "short" or "int" or "long" or "float" or "double";

    private List<Token> ParseFunctionTypeTokens()
    {
        var tokens = new List<Token>();
        if (Expect(TokenType.Fn, "Expected 'fn'.") is { } fnToken)
        {
            tokens.Add(fnToken);
        }

        if (Expect(TokenType.LParen, "Expected '(' after 'fn' in function type.") is { } openParameters)
        {
            tokens.Add(openParameters);
        }

        var parameters = new List<FunctionTypeParameterTokens>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                if (Current.Type == TokenType.Identifier && PeekType() == TokenType.Colon)
                {
                    tokens.Add(Advance());
                    if (Expect(TokenType.Colon, "Expected ':' after function type parameter name.") is { } colon)
                    {
                        tokens.Add(colon);
                    }
                }

                if (Match(TokenType.Ellipsis) is { } ellipsis)
                {
                    tokens.Add(ellipsis);
                    parameters.Add(new FunctionTypeParameterTokens(ellipsis, IsVariadic: true));
                }
                else
                {
                    var parameterTokens = ParseTypeTokens();
                    tokens.AddRange(parameterTokens);
                    parameters.Add(new FunctionTypeParameterTokens(
                        parameterTokens.FirstOrDefault() ?? Current,
                        IsVariadic: false));
                }
            }
            while (Match(TokenType.Comma) is { } comma && AddToken(tokens, comma));
        }

        ValidateVariadicFunctionType(parameters);
        if (Expect(TokenType.RParen, "Expected ')' after function type parameters.") is { } closeParameters)
        {
            tokens.Add(closeParameters);
        }

        if (Expect(TokenType.Arrow, "Expected '->' before function type return type.") is { } arrow)
        {
            tokens.Add(arrow);
        }

        tokens.AddRange(ParseTypeTokens());

        return tokens;
    }

    private static TypeNode CreateTypeNode(Location location, string type) =>
        TypeNode.CreateFromText(location, type);

    private static bool AddToken(List<Token> tokens, Token token)
    {
        tokens.Add(token);
        return true;
    }

    private bool CheckTypeCloseAngle() =>
        _pendingTypeCloseAngles > 0
        || Check(TokenType.GreaterThan)
        || Check(TokenType.GreaterThanGreaterThan);

    private void ExpectTypeCloseAngle(string message)
    {
        if (ConsumeTypeCloseAngle())
        {
            return;
        }

        _diagnostics.Report(Current.Location, message);
    }

    private bool WasLastTypeCloseAngleFromPending { get; set; }

    private TypeCloseAngle ConsumeTypeCloseAngle()
    {
        WasLastTypeCloseAngleFromPending = false;
        if (_pendingTypeCloseAngles > 0)
        {
            _pendingTypeCloseAngles--;
            WasLastTypeCloseAngleFromPending = true;
            return new TypeCloseAngle(null, FromPending: true);
        }

        if (Match(TokenType.GreaterThan) is { } greaterThan)
        {
            return new TypeCloseAngle(greaterThan, FromPending: false);
        }

        if (Match(TokenType.GreaterThanGreaterThan) is { } doubleGreaterThan)
        {
            _pendingTypeCloseAngles++;
            return new TypeCloseAngle(doubleGreaterThan, FromPending: false);
        }

        return new TypeCloseAngle(null, FromPending: false);
    }

    private readonly record struct TypeCloseAngle(Token? Token, bool FromPending)
    {
        public static implicit operator bool(TypeCloseAngle closeAngle) =>
            closeAngle.Token is not null || closeAngle.FromPending;
    }

    private void ValidateVariadicFunctionType(IReadOnlyList<FunctionTypeParameterTokens> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!parameters[i].IsVariadic)
            {
                continue;
            }

            if (i != parameters.Count - 1)
            {
                _diagnostics.Report(parameters[i].Location, "Variadic marker '...' must be the last function type parameter.");
            }

            if (i == 0)
            {
                _diagnostics.Report(parameters[i].Location, "Variadic function types require at least one fixed parameter before '...'.");
            }
        }
    }

    private readonly record struct FunctionTypeParameterTokens(Token FirstToken, bool IsVariadic)
    {
        public Location Location => FirstToken.Location;
    }
}
