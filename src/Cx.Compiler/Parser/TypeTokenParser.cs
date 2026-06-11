using Cx.Compiler.Lexer;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Parser;

internal static class TypeTokenParser
{
    public static TypeNode Parse(IReadOnlyList<Token> tokens)
    {
        if (tokens.Count == 0)
        {
            return TypeNode.CreateFromText(Location.Synthetic("<type-token-parser>"), string.Empty);
        }

        var syntax = TryParseSyntax(tokens);
        if (syntax is not null)
        {
            return TypeNode.Create(tokens[0].Location, syntax);
        }

        var typeName = ToTypeName(tokens);
        return new TypeNode(tokens[0].Location, typeName, new NamedTypeSyntaxNode(typeName));
    }

    public static string ToTypeName(IReadOnlyList<Token> tokens) =>
        Normalize(TokenText.ToSourceText(tokens));

    public static string Normalize(string type)
    {
        string previous;
        do
        {
            previous = type;
            type = type
                .Replace(" <", "<", StringComparison.Ordinal)
                .Replace("< ", "<", StringComparison.Ordinal)
                .Replace(" >", ">", StringComparison.Ordinal)
                .Replace("> ", ">", StringComparison.Ordinal)
                .Replace(" ,", ",", StringComparison.Ordinal)
                .Replace(", ", ",", StringComparison.Ordinal)
                .Replace(" *", "*", StringComparison.Ordinal)
                .Replace("* ", "*", StringComparison.Ordinal)
                .Replace(" [", "[", StringComparison.Ordinal)
                .Replace("[ ", "[", StringComparison.Ordinal)
                .Replace(" ]", "]", StringComparison.Ordinal)
                .Replace("] ", "]", StringComparison.Ordinal)
                .Trim();
        }
        while (!string.Equals(previous, type, StringComparison.Ordinal));

        return type;
    }

    private static TypeSyntaxNode? TryParseSyntax(IReadOnlyList<Token> tokens)
    {
        var parser = new Parser(tokens);
        var syntax = parser.ParseType();
        return syntax is not null && parser.IsAtEnd ? syntax : null;
    }

    private sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private int _position;
        private int _pendingCloseAngles;

        public bool IsAtEnd => _position >= tokens.Count;

        public TypeSyntaxNode? ParseType()
        {
            if (Match(TokenType.Fn) is not null)
            {
                return ParseFunctionType();
            }

            var type = ParsePrimaryType();
            if (type is null)
            {
                return null;
            }

            while (!IsAtEnd && _pendingCloseAngles == 0)
            {
                if (Match(TokenType.Star) is not null)
                {
                    type = new PointerTypeSyntaxNode(type);
                    continue;
                }

                if (Match(TokenType.LBracket) is not null)
                {
                    var lengthTokens = ReadUntilBalanced(TokenType.RBracket);
                    if (Match(TokenType.RBracket) is null)
                    {
                        return null;
                    }

                    type = new FixedArrayTypeSyntaxNode(type, TokenText.ToSourceText(lengthTokens));
                    continue;
                }

                break;
            }

            return type;
        }

        private TypeSyntaxNode? ParseFunctionType()
        {
            if (Match(TokenType.LParen) is null)
            {
                return null;
            }

            var parameters = new List<TypeSyntaxNode>();
            var isVariadic = false;
            if (!Check(TokenType.RParen))
            {
                do
                {
                    if (Match(TokenType.Ellipsis) is not null)
                    {
                        isVariadic = true;
                        break;
                    }

                    if (Current.Type == TokenType.Identifier && PeekType() == TokenType.Colon)
                    {
                        Advance();
                        Advance();
                    }

                    var parameter = ParseType();
                    if (parameter is null)
                    {
                        return null;
                    }

                    parameters.Add(parameter);
                }
                while (Match(TokenType.Comma) is not null);
            }

            if (Match(TokenType.RParen) is null || Match(TokenType.Arrow) is null)
            {
                return null;
            }

            var returnType = ParseType();
            return returnType is null
                ? null
                : new FunctionTypeSyntaxNode(parameters, returnType, isVariadic);
        }

        private TypeSyntaxNode? ParsePrimaryType()
        {
            var nameTokens = new List<Token>();

            if (Current.Type is TokenType.Struct or TokenType.Enum or TokenType.Union)
            {
                nameTokens.Add(Advance());
                if (Current.Type != TokenType.Identifier)
                {
                    return null;
                }

                nameTokens.Add(Advance());
            }
            else
            {
                if (Current.Type == TokenType.Const)
                {
                    nameTokens.Add(Advance());
                }

                if (Current.Type != TokenType.Identifier)
                {
                    return null;
                }

                nameTokens.Add(Advance());
                while (!IsAtEnd && Current.Type == TokenType.Identifier && IsTypeNameContinuation(Current.Value))
                {
                    nameTokens.Add(Advance());
                }
            }

            TypeSyntaxNode type = new NamedTypeSyntaxNode(TokenText.ToSourceText(nameTokens));
            if (Match(TokenType.LessThan) is not null)
            {
                var arguments = new List<TypeSyntaxNode>();
                if (!CheckTypeCloseAngle())
                {
                    do
                    {
                        var argument = ParseType();
                        if (argument is null)
                        {
                            return null;
                        }

                        arguments.Add(argument);
                    }
                    while (Match(TokenType.Comma) is not null);
                }

                if (!ConsumeTypeCloseAngle())
                {
                    return null;
                }

                type = new GenericTypeSyntaxNode(type, arguments);
            }

            return type;
        }

        private IReadOnlyList<Token> ReadUntilBalanced(TokenType terminator)
        {
            var result = new List<Token>();
            var depth = 0;
            while (!IsAtEnd)
            {
                if (depth == 0 && Check(terminator))
                {
                    break;
                }

                if (Current.Type is TokenType.LParen or TokenType.LBracket or TokenType.LBrace or TokenType.LessThan)
                {
                    depth++;
                }
                else if (Current.Type is TokenType.RParen or TokenType.RBracket or TokenType.RBrace or TokenType.GreaterThan)
                {
                    depth--;
                }

                result.Add(Advance());
            }

            return result;
        }

        private bool CheckTypeCloseAngle() =>
            _pendingCloseAngles > 0 || Check(TokenType.GreaterThan) || Check(TokenType.GreaterThanGreaterThan);

        private bool ConsumeTypeCloseAngle()
        {
            if (_pendingCloseAngles > 0)
            {
                _pendingCloseAngles--;
                return true;
            }

            if (Match(TokenType.GreaterThan) is not null)
            {
                return true;
            }

            if (Match(TokenType.GreaterThanGreaterThan) is not null)
            {
                _pendingCloseAngles++;
                return true;
            }

            return false;
        }

        private Token? Match(TokenType type) => Check(type) ? Advance() : null;

        private bool Check(TokenType type) => !IsAtEnd && Current.Type == type;

        private Token Advance()
        {
            var current = Current;
            if (!IsAtEnd)
            {
                _position++;
            }

            return current;
        }

        private TokenType PeekType(int offset = 1)
        {
            var index = _position + offset;
            return index < tokens.Count ? tokens[index].Type : TokenType.Eof;
        }

        private Token Current => tokens[_position];

        private static bool IsTypeNameContinuation(string value) =>
            value is "void" or "char" or "short" or "int" or "long" or "float" or "double";
    }
}
