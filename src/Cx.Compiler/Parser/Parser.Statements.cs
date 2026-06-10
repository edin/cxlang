using Cx.Compiler.Lexer;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Parser;

public sealed partial class Parser
{
    private StatementNode? ParseStatement()
    {
        if (Match(TokenType.Let) is { } letToken)
        {
            return ParseVariableStatement(letToken, isConst: false);
        }

        if (Match(TokenType.Const) is { } constToken)
        {
            return ParseVariableStatement(constToken, isConst: true);
        }

        if (Match(TokenType.Return) is { } returnToken)
        {
            var returnExpression = Check(TokenType.Semicolon)
                ? null
                : ReadExpressionUntil(returnToken.Location, TokenType.Semicolon);
            Expect(TokenType.Semicolon, "Expected ';' after return statement.");
            return new ReturnStatement(returnToken.Location, returnExpression);
        }

        if (Check(TokenType.If))
        {
            return ParseIfStatement();
        }

        if (Check(TokenType.While))
        {
            return ParseWhileStatement();
        }

        if (Check(TokenType.For))
        {
            return ParseForStatement();
        }

        if (Check(TokenType.Foreach))
        {
            return ParseForeachStatement();
        }

        if (Check(TokenType.Switch))
        {
            return ParseSwitchStatement();
        }

        if (Check(TokenType.Match))
        {
            return ParseMatchStatement();
        }

        if (Match(TokenType.Break) is { } breakToken)
        {
            Expect(TokenType.Semicolon, "Expected ';' after break statement.");
            return new BreakStatement(breakToken.Location);
        }

        if (Match(TokenType.Continue) is { } continueToken)
        {
            Expect(TokenType.Semicolon, "Expected ';' after continue statement.");
            return new ContinueStatement(continueToken.Location);
        }

        var location = Current.Location;
        var expression = ReadExpressionUntil(location, TokenType.Semicolon);
        Expect(TokenType.Semicolon, "Expected ';' after expression statement.");
        return new CStatement(location, expression);
    }

    private StatementNode ParseVariableStatement(Token keywordToken, bool isConst)
    {
        var nameToken = Expect(TokenType.Identifier, "Expected variable name.");
        var typeNode = ParseOptionalVariableTypeNode("variable", keywordToken.Location);
        ExpressionNode? initializer = null;

        if (ConsumeOptional(TokenType.Equals))
        {
            initializer = ReadExpressionUntil(keywordToken.Location, TokenType.Semicolon);
        }

        if (typeNode is null && initializer is null)
        {
            _diagnostics.Report(keywordToken.Location, "Expected ':' or '=' after variable name.");
        }

        if (isConst && initializer is null)
        {
            _diagnostics.Report(keywordToken.Location, "Const variables require an initializer.");
        }

        Expect(TokenType.Semicolon, "Expected ';' after variable declaration.");
        return new LetStatement(keywordToken.Location, isConst, nameToken?.Value ?? string.Empty, initializer, typeNode);
    }

    private IfStatement? ParseIfStatement()
    {
        var ifToken = Expect(TokenType.If, "Expected 'if'.");
        var condition = ParseParenthesizedExpression("if condition");
        var thenBody = ParseBlock();
        StatementNode? elseBranch = null;

        if (ConsumeOptional(TokenType.Else))
        {
            if (Check(TokenType.If))
            {
                elseBranch = ParseIfStatement();
            }
            else
            {
                var elseLocation = Current.Location;
                elseBranch = new ElseBlockStatement(elseLocation, ParseBlock());
            }
        }

        return ifToken is null ? null : new IfStatement(ifToken.Location, condition, thenBody, elseBranch);
    }

    private WhileStatement? ParseWhileStatement()
    {
        var whileToken = Expect(TokenType.While, "Expected 'while'.");
        var condition = ParseParenthesizedExpression("while condition");
        var body = ParseBlock();

        return whileToken is null ? null : new WhileStatement(whileToken.Location, condition, body);
    }

    private ForStatement? ParseForStatement()
    {
        var forToken = Expect(TokenType.For, "Expected 'for'.");
        Expect(TokenType.LParen, "Expected '(' after 'for'.");
        var initializer = ParseForInitializer(forToken?.Location ?? Current.Location);
        Expect(TokenType.Semicolon, "Expected ';' after for initializer.");
        var condition = ReadExpressionUntil(forToken?.Location ?? Current.Location, TokenType.Semicolon);
        Expect(TokenType.Semicolon, "Expected ';' after for condition.");
        var increment = ReadExpressionUntil(forToken?.Location ?? Current.Location, TokenType.RParen);
        Expect(TokenType.RParen, "Expected ')' after for increment.");
        var body = ParseBlock();

        return forToken is null ? null : new ForStatement(forToken.Location, initializer, condition, increment, body);
    }

    private ForInitializerNode ParseForInitializer(Location location)
    {
        if (Match(TokenType.Let) is { } letToken)
        {
            return ParseForDeclarationInitializer(letToken.Location, isConst: false);
        }

        if (Match(TokenType.Const) is { } constToken)
        {
            return ParseForDeclarationInitializer(constToken.Location, isConst: true);
        }

        return new ForExpressionInitializerNode(
            location,
            ReadExpressionUntil(location, TokenType.Semicolon));
    }

    private ForDeclarationInitializerNode ParseForDeclarationInitializer(Location location, bool isConst)
    {
        var nameToken = Expect(TokenType.Identifier, "Expected for initializer variable name.");
        var typeNode = ParseOptionalVariableTypeNode("for initializer variable", location);
        ExpressionNode? initializer = null;

        if (ConsumeOptional(TokenType.Equals))
        {
            initializer = ReadExpressionUntil(location, TokenType.Semicolon);
        }

        if (typeNode is null && initializer is null)
        {
            _diagnostics.Report(location, "Expected ':' or '=' after for initializer variable name.");
        }

        return new ForDeclarationInitializerNode(
            location,
            isConst,
            nameToken?.Value ?? string.Empty,
            initializer,
            typeNode);
    }

    private string ParseOptionalVariableType(string subject, Location location)
    {
        return ParseOptionalVariableTypeNode(subject, location)?.TypeName ?? string.Empty;
    }

    private TypeNode? ParseOptionalVariableTypeNode(string subject, Location location)
    {
        if (!ConsumeOptional(TokenType.Colon))
        {
            return null;
        }

        var type = ParseTypeNode();
        if (string.IsNullOrWhiteSpace(type.TypeName))
        {
            _diagnostics.Report(location, $"Expected type after ':' in {subject} declaration.");
        }

        return type;
    }

    private ForeachStatement? ParseForeachStatement()
    {
        var foreachToken = Expect(TokenType.Foreach, "Expected 'foreach'.");
        var firstBinding = ParseForeachBinding("Expected foreach binding name.");
        ForeachBinding? indexBinding = null;
        ForeachBinding? keyBinding = null;
        ForeachBinding? valueBinding = null;

        if (ConsumeOptional(TokenType.Comma))
        {
            var secondBinding = ParseForeachBinding("Expected foreach binding name after ','.");
            if (ConsumeOptional(TokenType.FatArrow))
            {
                indexBinding = firstBinding;
                keyBinding = secondBinding;
                valueBinding = ParseForeachBinding("Expected foreach value binding after '=>'.");
            }
            else
            {
                indexBinding = firstBinding;
                valueBinding = secondBinding;
            }
        }
        else if (ConsumeOptional(TokenType.FatArrow))
        {
            keyBinding = firstBinding;
            valueBinding = ParseForeachBinding("Expected foreach value binding after '=>'.");
        }
        else
        {
            valueBinding = firstBinding;
        }

        Expect(TokenType.In, "Expected 'in' after foreach binding.");
        var iterableExpression = ReadExpressionUntil(foreachToken?.Location ?? Current.Location, TokenType.LBrace);
        var body = ParseBlock();

        return foreachToken is null
            ? null
            : new ForeachStatement(
                foreachToken.Location,
                indexBinding,
                keyBinding,
                valueBinding ?? new ForeachBinding(foreachToken.Location, string.Empty, IsReference: true, IsConst: false),
                iterableExpression,
                body);
    }

    private ForeachBinding ParseForeachBinding(string message)
    {
        var isConst = ConsumeOptional(TokenType.Const);
        var isReference = ConsumeOptional(TokenType.Ampersand);
        var nameToken = Expect(TokenType.Identifier, message);
        var typeNode = ParseOptionalVariableTypeNode("foreach binding", nameToken?.Location ?? Current.Location);
        return new ForeachBinding(
            nameToken?.Location ?? Current.Location,
            nameToken?.Value ?? string.Empty,
            isReference,
            isConst,
            typeNode);
    }

    private SwitchStatement? ParseSwitchStatement()
    {
        var switchToken = Expect(TokenType.Switch, "Expected 'switch'.");
        var expression = ParseParenthesizedExpression("switch expression");
        Expect(TokenType.LBrace, "Expected '{' before switch body.");

        var cases = new List<SwitchCaseNode>();
        var defaultBody = new List<StatementNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            if (Match(TokenType.Case) is { } caseToken)
            {
                var pattern = ReadExpressionUntil(caseToken.Location, TokenType.Colon);
                Expect(TokenType.Colon, "Expected ':' after case pattern.");
                cases.Add(new SwitchCaseNode(caseToken.Location, pattern, ParseSwitchArmBody()));
                continue;
            }

            if (Match(TokenType.Default) is { })
            {
                Expect(TokenType.Colon, "Expected ':' after default.");
                defaultBody.AddRange(ParseSwitchArmBody());
                continue;
            }

            _diagnostics.Report(Current.Location, $"Unexpected token '{Current.Value}' in switch body.");
            SynchronizeStatement();
        }

        Expect(TokenType.RBrace, "Expected '}' after switch body.");
        return switchToken is null
            ? null
            : new SwitchStatement(switchToken.Location, expression, cases, defaultBody);
    }

    private IReadOnlyList<StatementNode> ParseSwitchArmBody()
    {
        if (Check(TokenType.LBrace))
        {
            var body = ParseBlock().ToList();
            if (Match(TokenType.Break) is { } breakToken)
            {
                Expect(TokenType.Semicolon, "Expected ';' after break statement.");
                body.Add(new BreakStatement(breakToken.Location));
            }

            return body;
        }

        var statements = new List<StatementNode>();
        while (!IsAtEnd
            && !Check(TokenType.Case)
            && !Check(TokenType.Default)
            && !Check(TokenType.RBrace))
        {
            if (ParseStatement() is { } statement)
            {
                statements.Add(statement);
            }
            else
            {
                SynchronizeStatement();
            }
        }

        return statements;
    }

    private MatchStatement? ParseMatchStatement()
    {
        var matchToken = Expect(TokenType.Match, "Expected 'match'.");
        var expression = ReadExpressionUntil(matchToken?.Location ?? Current.Location, TokenType.LBrace);
        Expect(TokenType.LBrace, "Expected '{' before match arms.");

        var arms = new List<MatchArmNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var arm = ParseMatchArm();
            if (arm is not null)
            {
                arms.Add(arm);
            }
            else
            {
                SynchronizeMatchArm();
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after match arms.");
        return matchToken is null ? null : new MatchStatement(matchToken.Location, expression, arms);
    }

    private MatchArmNode? ParseMatchArm()
    {
        var patternToken = Expect(TokenType.Identifier, "Expected match pattern.");
        string? bindingName = null;

        if (ConsumeOptional(TokenType.Colon))
        {
            bindingName = Expect(TokenType.Identifier, "Expected binding name after ':'.")?.Value;
        }

        Expect(TokenType.FatArrow, "Expected '=>' after match pattern.");

        IReadOnlyList<StatementNode> body;
        if (Check(TokenType.LBrace))
        {
            body = ParseBlock();
        }
        else if (ParseStatement() is { } statement)
        {
            body = [statement];
        }
        else
        {
            body = [];
        }

        return patternToken is null
            ? null
            : new MatchArmNode(patternToken.Location, patternToken.Value, bindingName, body);
    }

    private IReadOnlyList<StatementNode> ParseBlock()
    {
        Expect(TokenType.LBrace, "Expected '{' before block.");
        var statements = ParseBlockBody(TokenType.RBrace);
        Expect(TokenType.RBrace, "Expected '}' after block.");
        return statements;
    }

    private IReadOnlyList<StatementNode> ParseBlockBody(TokenType terminator)
    {
        var statements = new List<StatementNode>();

        while (!IsAtEnd && !Check(terminator))
        {
            var statement = ParseStatement();
            if (statement is not null)
            {
                statements.Add(statement);
            }
            else
            {
                SynchronizeStatement();
            }
        }

        return statements;
    }

    internal IReadOnlyList<StatementNode> ParseBlockBodyTokens(IReadOnlyList<Token> bodyTokens, Location location)
    {
        var eofLocation = bodyTokens.Count > 0
            ? bodyTokens[^1].Location
            : location;
        _tokens = new TokenStream(bodyTokens, eofLocation);
        _pendingTypeCloseAngles = 0;
        return ParseBlockBody(TokenType.Eof);
    }
}
