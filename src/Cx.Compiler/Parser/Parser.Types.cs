using Cx.Compiler.Lexer;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Parser;

public sealed partial class Parser
{
    private TypeNode ParseTypeNode()
    {
        var location = Current.Location;
        if (Check(TokenType.Fn))
        {
            return ParseFunctionTypeNode();
        }

        var typeNames = ParseTypeName();
        if (typeNames.Count == 0)
        {
            _diagnostics.Report(Current.Location, "Expected type name.");
            return CreateTypeNode(location, string.Empty);
        }

        var parts = new List<string> { string.Join(" ", typeNames) };

        if (ConsumeOptional(TokenType.LessThan))
        {
            var typeArguments = new List<string>();

            if (!CheckTypeCloseAngle())
            {
                do
                {
                    typeArguments.Add(ParseTypeNode().TypeName);
                }
                while (ConsumeOptional(TokenType.Comma));
            }

            ExpectTypeCloseAngle("Expected '>' after generic type arguments.");
            parts.Add("<");
            parts.Add(string.Join(",", typeArguments));
            parts.Add(">");
        }

        if (_pendingTypeCloseAngles == 0)
        {
            while (ConsumeOptional(TokenType.Star))
            {
                parts.Add("*");
            }

            while (ConsumeOptional(TokenType.LBracket))
            {
                var length = ReadUntil(TokenType.RBracket);
                Expect(TokenType.RBracket, "Expected ']' after array length.");
                parts.Add("[");
                parts.Add(length);
                parts.Add("]");
            }
        }

        return CreateTypeNode(location, string.Join("", parts));
    }

    private List<string> ParseTypeName()
    {
        var names = new List<string>();

        if (Current.Type is TokenType.Struct or TokenType.Enum or TokenType.Union)
        {
            names.Add(Advance().Value);
            if (Expect(TokenType.Identifier, "Expected C tag type name.") is { } tagName)
            {
                names.Add(tagName.Value);
            }

            return names;
        }

        var isConst = Current.Type == TokenType.Const;
        var first = isConst
            ? Advance()
            : Expect(TokenType.Identifier, "Expected type name.");

        if (first is null)
        {
            return names;
        }

        names.Add(first.Value);
        if (isConst && Current.Type == TokenType.Identifier)
        {
            names.Add(Advance().Value);
        }

        while (ConsumeOptional(TokenType.Dot))
        {
            if (Expect(TokenType.Identifier, "Expected type name after '.'.") is { } part)
            {
                names[^1] = names[^1] + "." + part.Value;
            }
        }

        while (Current.Type == TokenType.Identifier && IsTypeNameContinuation(Current.Value))
        {
            names.Add(Advance().Value);
        }

        return names;
    }

    private static bool IsTypeNameContinuation(string value) =>
        value is "void" or "char" or "short" or "int" or "long" or "float" or "double";

    private TypeNode ParseFunctionTypeNode()
    {
        var location = Current.Location;
        Expect(TokenType.Fn, "Expected 'fn'.");
        Expect(TokenType.LParen, "Expected '(' after 'fn' in function type.");

        var parameterTypes = new List<string>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                if (Current.Type == TokenType.Identifier && PeekType() == TokenType.Colon)
                {
                    Advance();
                    Expect(TokenType.Colon, "Expected ':' after function type parameter name.");
                }

                if (Match(TokenType.Ellipsis) is not null)
                {
                    parameterTypes.Add("...");
                }
                else
                {
                    parameterTypes.Add(ParseTypeNode().TypeName);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicFunctionType(parameterTypes);
        Expect(TokenType.RParen, "Expected ')' after function type parameters.");
        Expect(TokenType.Arrow, "Expected '->' before function type return type.");
        var returnType = ParseTypeNode().TypeName;

        return CreateTypeNode(location, $"fn({string.Join(",", parameterTypes)})->{returnType}");
    }

    private static TypeNode CreateTypeNode(Location location, string type) =>
        TypeNode.Create(location, type);

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

    private bool ConsumeTypeCloseAngle()
    {
        if (_pendingTypeCloseAngles > 0)
        {
            _pendingTypeCloseAngles--;
            return true;
        }

        if (ConsumeOptional(TokenType.GreaterThan))
        {
            return true;
        }

        if (ConsumeOptional(TokenType.GreaterThanGreaterThan))
        {
            _pendingTypeCloseAngles++;
            return true;
        }

        return false;
    }

    private void ValidateVariadicFunctionType(IReadOnlyList<string> parameterTypes)
    {
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            if (parameterTypes[i] != "...")
            {
                continue;
            }

            if (i != parameterTypes.Count - 1)
            {
                _diagnostics.Report(Current.Location, "Variadic marker '...' must be the last function type parameter.");
            }

            if (i == 0)
            {
                _diagnostics.Report(Current.Location, "Variadic function types require at least one fixed parameter before '...'.");
            }
        }
    }
}
