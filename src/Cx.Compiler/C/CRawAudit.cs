namespace Cx.Compiler.C;

internal sealed record CRawAuditReport(IReadOnlyList<CRawAuditEntry> Entries)
{
    public bool HasEntries => Entries.Count > 0;
}

internal sealed record CRawAuditEntry(
    string Kind,
    string Category,
    string Path,
    string Text);

internal sealed class CRawAuditCollector
{
    private readonly List<CRawAuditEntry> _entries = [];

    public CRawAuditReport Collect(CTranslationUnit unit)
    {
        _entries.Clear();
        for (var i = 0; i < unit.Items.Count; i++)
        {
            VisitItem(unit.Items[i], GetItemPath(unit.Items[i], i));
        }

        return new CRawAuditReport(_entries.ToList());
    }

    private static string GetItemPath(CTranslationUnitItem item, int index) => item switch
    {
        CGlobalDeclaration global => $"global[{global.Declaration}]",
        CFunctionDeclaration function => $"prototype[{function.Name}]",
        CFunctionDefinition function => $"function[{function.Name}]",
        CStructDeclaration structDeclaration => $"struct[{structDeclaration.Name}]",
        CEnumDeclaration enumDeclaration => $"enum[{enumDeclaration.Name}]",
        CTaggedUnionDeclaration unionDeclaration => $"union[{unionDeclaration.Name}]",
        CTypeAliasDeclaration typeAlias => $"type[{typeAlias.Name}]",
        _ => $"item[{index}]",
    };

    private void VisitItem(CTranslationUnitItem item, string path)
    {
        switch (item)
        {
            case CGlobalDeclaration global:
                VisitOptionalExpression(global.Initializer, path + ".initializer");
                break;
            case CFunctionDefinition function:
                for (var i = 0; i < function.Body.Count; i++)
                {
                    VisitStatement(function.Body[i], $"{path}.body[{i}]");
                }
                break;
            case CRawTopLevel raw:
                Add("RawTopLevel", path, raw.Text);
                break;
        }
    }

    private void VisitStatement(CStatementNode statement, string path)
    {
        switch (statement)
        {
            case CLocalDeclarationStatement local:
                VisitOptionalExpression(local.Initializer, path + ".initializer");
                break;
            case CReturnStatement ret:
                VisitExpression(ret.Expression, path + ".return");
                break;
            case CExpressionStatement expression:
                VisitExpression(expression.Expression, path + ".expression");
                break;
            case CIfStatement ifStatement:
                VisitExpression(ifStatement.Condition, path + ".condition");
                VisitStatements(ifStatement.ThenBody, path + ".then");
                VisitElseClause(ifStatement.ElseClause, path + ".else");
                break;
            case CWhileStatement whileStatement:
                VisitExpression(whileStatement.Condition, path + ".condition");
                VisitStatements(whileStatement.Body, path + ".body");
                break;
            case CForStatement forStatement:
                VisitForInitializer(forStatement.Initializer, path + ".initializer");
                VisitExpression(forStatement.Condition, path + ".condition");
                VisitExpression(forStatement.Increment, path + ".increment");
                VisitStatements(forStatement.Body, path + ".body");
                break;
            case CSwitchStatement switchStatement:
                VisitExpression(switchStatement.Expression, path + ".expression");
                for (var i = 0; i < switchStatement.Cases.Count; i++)
                {
                    VisitStatements(switchStatement.Cases[i].Body, $"{path}.case[{i}]");
                }

                VisitStatements(switchStatement.DefaultBody, path + ".default");
                break;
            case CRawStatement raw:
                Add("RawStatement", path, raw.Text);
                break;
        }
    }

    private void VisitStatements(IReadOnlyList<CStatementNode> statements, string path)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            VisitStatement(statements[i], $"{path}[{i}]");
        }
    }

    private void VisitElseClause(CElseClause? elseClause, string path)
    {
        switch (elseClause)
        {
            case CElseIfClause elseIf:
                VisitStatement(elseIf.IfStatement, path);
                break;
            case CElseBlockClause elseBlock:
                VisitStatements(elseBlock.Body, path);
                break;
        }
    }

    private void VisitForInitializer(CForInitializerNode initializer, string path)
    {
        switch (initializer)
        {
            case CDeclarationForInitializer declaration:
                VisitOptionalExpression(declaration.Initializer, path + ".initializer");
                break;
            case CExpressionForInitializer expression:
                VisitExpression(expression.Expression, path + ".expression");
                break;
        }
    }

    private void VisitOptionalExpression(CExpression? expression, string path)
    {
        if (expression is not null)
        {
            VisitExpression(expression, path);
        }
    }

    private void VisitExpression(CExpression expression, string path)
    {
        switch (expression)
        {
            case CRawExpression raw:
                Add("RawExpression", path, raw.Text);
                break;
            case CParenthesizedExpression parenthesized:
                VisitExpression(parenthesized.Expression, path + ".inner");
                break;
            case CCastExpression cast:
                VisitExpression(cast.Expression, path + ".value");
                break;
            case CUnaryExpression unary:
                VisitExpression(unary.Operand, path + ".operand");
                break;
            case CPostfixExpression postfix:
                VisitExpression(postfix.Operand, path + ".operand");
                break;
            case CSizeOfExpression sizeOf:
                VisitExpression(sizeOf.Expression, path + ".value");
                break;
            case CBinaryExpression binary:
                VisitExpression(binary.Left, path + ".left");
                VisitExpression(binary.Right, path + ".right");
                break;
            case CConditionalExpression conditional:
                VisitExpression(conditional.Condition, path + ".condition");
                VisitExpression(conditional.WhenTrue, path + ".true");
                VisitExpression(conditional.WhenFalse, path + ".false");
                break;
            case CAssignmentExpression assignment:
                VisitExpression(assignment.Target, path + ".target");
                VisitExpression(assignment.Value, path + ".value");
                break;
            case CMemberExpression member:
                VisitExpression(member.Target, path + ".target");
                break;
            case CIndexExpression index:
                VisitExpression(index.Target, path + ".target");
                VisitExpression(index.Index, path + ".index");
                break;
            case CInitializerExpression initializer:
                for (var i = 0; i < initializer.Fields.Count; i++)
                {
                    VisitExpression(initializer.Fields[i].Value, $"{path}.field[{initializer.Fields[i].Name}]");
                }

                for (var i = 0; i < initializer.Values.Count; i++)
                {
                    VisitExpression(initializer.Values[i], $"{path}.value[{i}]");
                }
                break;
            case CCallExpression call:
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    VisitExpression(call.Arguments[i], $"{path}.arg[{i}]");
                }
                break;
        }
    }

    private void Add(string kind, string path, string text)
    {
        text = text.Trim();
        _entries.Add(new CRawAuditEntry(kind, Classify(kind, text), path, text));
    }

    private static string Classify(string kind, string text)
    {
        if (kind == "RawStatement")
        {
            return text.StartsWith("/* unable to lower", StringComparison.Ordinal)
                ? "LoweringFallbackStatement"
                : "FallbackStatement";
        }

        if (kind == "RawTopLevel")
        {
            return "FallbackTopLevel";
        }

        if (text.Contains("=>", StringComparison.Ordinal)
            || text.StartsWith("fn", StringComparison.Ordinal))
        {
            return "LambdaExpression";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b[A-Z][A-Z0-9_]*\s*\("))
        {
            return "MacroCall";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\([A-Za-z_][A-Za-z0-9_]*\)\s*\{\s*\.tag\s*="))
        {
            return "TaggedUnionInitializer";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\([A-Za-z_][A-Za-z0-9_]*\)\s*\{"))
        {
            return "StructInitializer";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\*\s*[A-Za-z_][A-Za-z0-9_]*\s*\("))
        {
            return "DereferenceCall";
        }

        if (text.Contains('='))
        {
            return "AssignmentExpression";
        }

        return "FallbackExpression";
    }
}
