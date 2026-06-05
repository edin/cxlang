using System.Text;

namespace Cx.Compiler.C;

internal sealed class CTranslationUnitEmitter
{
    private static readonly CExpressionEmitter _expressionEmitter = new();

    public string Emit(CTranslationUnit unit)
    {
        var builder = new StringBuilder();
        foreach (var item in unit.Items)
        {
            switch (item)
            {
                case CComment comment:
                    builder.Append("/* ");
                    builder.Append(comment.Text);
                    builder.AppendLine(" */");
                    break;
                case CBlankLine:
                    builder.AppendLine();
                    break;
                case CInclude include:
                    builder.AppendLine(include.IsSystem
                        ? $"#include <{include.Path}>"
                        : $"#include \"{include.Path}\"");
                    break;
                case CEnumDeclaration enumDeclaration:
                    EmitEnum(builder, enumDeclaration);
                    break;
                case CStructDeclaration structDeclaration:
                    EmitStruct(builder, structDeclaration);
                    break;
                case CTaggedUnionDeclaration taggedUnionDeclaration:
                    EmitTaggedUnion(builder, taggedUnionDeclaration);
                    break;
                case CTypeAliasDeclaration typeAliasDeclaration:
                    EmitTypeAlias(builder, typeAliasDeclaration);
                    break;
                case CFunctionDeclaration functionDeclaration:
                    EmitFunctionDeclaration(builder, functionDeclaration);
                    break;
                case CFunctionDefinition functionDefinition:
                    EmitFunctionDefinition(builder, functionDefinition);
                    break;
                case CGlobalDeclaration globalDeclaration:
                    EmitGlobalDeclaration(builder, globalDeclaration);
                    break;
                case CRawTopLevel raw:
                    builder.Append(raw.Text);
                    if (!raw.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    {
                        builder.AppendLine();
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    private static void EmitEnum(StringBuilder builder, CEnumDeclaration enumDeclaration)
    {
        builder.AppendLine("typedef enum");
        builder.AppendLine("{");

        for (var i = 0; i < enumDeclaration.Members.Count; i++)
        {
            var member = enumDeclaration.Members[i];
            var value = string.IsNullOrWhiteSpace(member.Value) ? "" : " = " + member.Value;
            var comma = i == enumDeclaration.Members.Count - 1 ? "" : ",";
            builder.AppendLine($"    {member.Name}{value}{comma}");
        }

        builder.AppendLine($"}} {enumDeclaration.Name};");
    }

    private static void EmitStruct(StringBuilder builder, CStructDeclaration structDeclaration)
    {
        builder.AppendLine($"typedef struct {structDeclaration.Name}");
        builder.AppendLine("{");

        foreach (var field in structDeclaration.FieldDeclarations)
        {
            builder.AppendLine($"    {field};");
        }

        builder.AppendLine($"}} {structDeclaration.Name};");
    }

    private static void EmitTaggedUnion(StringBuilder builder, CTaggedUnionDeclaration taggedUnionDeclaration)
    {
        if (taggedUnionDeclaration.IsRaw)
        {
            builder.AppendLine($"typedef union {taggedUnionDeclaration.Name}");
            builder.AppendLine("{");
            foreach (var variant in taggedUnionDeclaration.Variants)
            {
                builder.AppendLine($"    {variant.FieldDeclaration};");
            }

            builder.AppendLine($"}} {taggedUnionDeclaration.Name};");
            return;
        }

        builder.AppendLine("typedef enum");
        builder.AppendLine("{");
        foreach (var variant in taggedUnionDeclaration.Variants)
        {
            builder.AppendLine($"    {taggedUnionDeclaration.Name}_Tag_{variant.Name},");
        }

        builder.AppendLine($"}} {taggedUnionDeclaration.Name}Tag;");
        builder.AppendLine();
        builder.AppendLine("typedef struct");
        builder.AppendLine("{");
        builder.AppendLine($"    {taggedUnionDeclaration.Name}Tag tag;");
        builder.AppendLine("    union");
        builder.AppendLine("    {");
        foreach (var variant in taggedUnionDeclaration.Variants)
        {
            builder.AppendLine($"        {variant.TypeName} {variant.Name};");
        }

        builder.AppendLine("    } as;");
        builder.AppendLine($"}} {taggedUnionDeclaration.Name};");
    }

    private static void EmitTypeAlias(StringBuilder builder, CTypeAliasDeclaration typeAliasDeclaration)
    {
        if (typeAliasDeclaration.FunctionParameterTypes is not null)
        {
            builder.Append("typedef ");
            builder.Append(typeAliasDeclaration.TargetType);
            builder.Append(" (*");
            builder.Append(typeAliasDeclaration.Name);
            builder.Append(")(");
            builder.Append(string.Join(", ", typeAliasDeclaration.FunctionParameterTypes));
            builder.AppendLine(");");
            return;
        }

        builder.Append("typedef ");
        builder.Append(typeAliasDeclaration.TargetType);
        builder.Append(' ');
        builder.Append(typeAliasDeclaration.Name);
        builder.AppendLine(";");
    }

    private static void EmitFunctionDeclaration(StringBuilder builder, CFunctionDeclaration functionDeclaration)
    {
        builder.Append(functionDeclaration.ReturnType);
        builder.Append(' ');
        builder.Append(functionDeclaration.Name);
        builder.Append('(');
        builder.Append(string.Join(", ", functionDeclaration.ParameterDeclarations));
        builder.AppendLine(");");
    }

    private static void EmitFunctionDefinition(StringBuilder builder, CFunctionDefinition functionDefinition)
    {
        builder.Append(functionDefinition.ReturnType);
        builder.Append(' ');
        builder.Append(functionDefinition.Name);
        builder.Append('(');
        builder.Append(string.Join(", ", functionDefinition.ParameterDeclarations));
        builder.AppendLine(")");
        builder.AppendLine("{");
        foreach (var statement in functionDefinition.Body)
        {
            EmitStatement(builder, statement, indentLevel: 1);
        }
        builder.AppendLine("}");
    }

    private static void EmitStatement(StringBuilder builder, CStatementNode statement, int indentLevel)
    {
        switch (statement)
        {
            case CBlockStatement block:
                EmitBlock(builder, block.Body, indentLevel);
                break;
            case CLocalDeclarationStatement localDeclaration:
                AppendIndent(builder, indentLevel);
                builder.Append(localDeclaration.Declaration);
                if (localDeclaration.Initializer is not null)
                {
                    builder.Append(" = ");
                    builder.Append(_expressionEmitter.Emit(localDeclaration.Initializer));
                }
                builder.AppendLine(";");
                break;
            case CReturnStatement returnStatement:
                AppendIndent(builder, indentLevel);
                builder.Append("return ");
                builder.Append(_expressionEmitter.Emit(returnStatement.Expression));
                builder.AppendLine(";");
                break;
            case CBreakStatement:
                AppendIndent(builder, indentLevel);
                builder.AppendLine("break;");
                break;
            case CContinueStatement:
                AppendIndent(builder, indentLevel);
                builder.AppendLine("continue;");
                break;
            case CExpressionStatement expressionStatement:
                AppendIndent(builder, indentLevel);
                builder.Append(_expressionEmitter.Emit(expressionStatement.Expression));
                builder.AppendLine(";");
                break;
            case CIfStatement ifStatement:
                EmitIfStatement(builder, ifStatement, indentLevel);
                break;
            case CWhileStatement whileStatement:
                EmitWhileStatement(builder, whileStatement, indentLevel);
                break;
            case CForStatement forStatement:
                EmitForStatement(builder, forStatement, indentLevel);
                break;
            case CSwitchStatement switchStatement:
                EmitSwitchStatement(builder, switchStatement, indentLevel);
                break;
            case CRawStatement raw:
                builder.Append(raw.Text);
                if (!raw.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                {
                    builder.AppendLine();
                }
                break;
        }
    }

    private static void EmitWhileStatement(StringBuilder builder, CWhileStatement whileStatement, int indentLevel)
    {
        AppendIndent(builder, indentLevel);
        builder.Append("while (");
        builder.Append(_expressionEmitter.Emit(whileStatement.Condition));
        builder.AppendLine(")");
        EmitBlock(builder, whileStatement.Body, indentLevel);
    }

    private static void EmitForStatement(StringBuilder builder, CForStatement forStatement, int indentLevel)
    {
        AppendIndent(builder, indentLevel);
        builder.Append("for (");
        builder.Append(EmitForInitializer(forStatement.Initializer));
        builder.Append("; ");
        builder.Append(_expressionEmitter.Emit(forStatement.Condition));
        builder.Append("; ");
        builder.Append(_expressionEmitter.Emit(forStatement.Increment));
        builder.AppendLine(")");
        EmitBlock(builder, forStatement.Body, indentLevel);
    }

    private static string EmitForInitializer(CForInitializerNode initializer) => initializer switch
    {
        CEmptyForInitializer => string.Empty,
        CDeclarationForInitializer declaration when declaration.Initializer is null => declaration.Declaration,
        CDeclarationForInitializer { Initializer: { } value } declaration => $"{declaration.Declaration} = {_expressionEmitter.Emit(value)}",
        CExpressionForInitializer expression => _expressionEmitter.Emit(expression.Expression),
        _ => throw new InvalidOperationException($"Unexpected C for initializer node {initializer.GetType().Name}."),
    };

    private static void EmitSwitchStatement(StringBuilder builder, CSwitchStatement switchStatement, int indentLevel)
    {
        AppendIndent(builder, indentLevel);
        builder.Append("switch (");
        builder.Append(_expressionEmitter.Emit(switchStatement.Expression));
        builder.AppendLine(")");
        AppendIndent(builder, indentLevel);
        builder.AppendLine("{");

        foreach (var switchCase in switchStatement.Cases)
        {
            AppendIndent(builder, indentLevel + 1);
            builder.Append("case ");
            builder.Append(switchCase.Pattern);
            builder.AppendLine(":");
            EmitBlock(builder, switchCase.Body, indentLevel + 1);
        }

        if (switchStatement.DefaultBody.Count > 0)
        {
            AppendIndent(builder, indentLevel + 1);
            builder.AppendLine("default:");
            EmitBlock(builder, switchStatement.DefaultBody, indentLevel + 1);
        }

        AppendIndent(builder, indentLevel);
        builder.AppendLine("}");
    }

    private static void EmitIfStatement(StringBuilder builder, CIfStatement ifStatement, int indentLevel)
    {
        AppendIndent(builder, indentLevel);
        builder.Append("if (");
        builder.Append(_expressionEmitter.Emit(ifStatement.Condition));
        builder.AppendLine(")");
        EmitBlock(builder, ifStatement.ThenBody, indentLevel);

        switch (ifStatement.ElseClause)
        {
            case CElseIfClause elseIf:
                AppendIndent(builder, indentLevel);
                builder.Append("else ");
                EmitIfStatementHeaderAndBody(builder, elseIf.IfStatement, indentLevel);
                break;
            case CElseBlockClause elseBlock:
                AppendIndent(builder, indentLevel);
                builder.AppendLine("else");
                EmitBlock(builder, elseBlock.Body, indentLevel);
                break;
        }
    }

    private static void EmitIfStatementHeaderAndBody(StringBuilder builder, CIfStatement ifStatement, int indentLevel)
    {
        builder.Append("if (");
        builder.Append(_expressionEmitter.Emit(ifStatement.Condition));
        builder.AppendLine(")");
        EmitBlock(builder, ifStatement.ThenBody, indentLevel);

        switch (ifStatement.ElseClause)
        {
            case CElseIfClause elseIf:
                AppendIndent(builder, indentLevel);
                builder.Append("else ");
                EmitIfStatementHeaderAndBody(builder, elseIf.IfStatement, indentLevel);
                break;
            case CElseBlockClause elseBlock:
                AppendIndent(builder, indentLevel);
                builder.AppendLine("else");
                EmitBlock(builder, elseBlock.Body, indentLevel);
                break;
        }
    }

    private static void EmitBlock(StringBuilder builder, IReadOnlyList<CStatementNode> statements, int indentLevel)
    {
        AppendIndent(builder, indentLevel);
        builder.AppendLine("{");
        foreach (var statement in statements)
        {
            EmitStatement(builder, statement, indentLevel + 1);
        }

        AppendIndent(builder, indentLevel);
        builder.AppendLine("}");
    }

    private static void EmitGlobalDeclaration(StringBuilder builder, CGlobalDeclaration globalDeclaration)
    {
        builder.Append(globalDeclaration.Declaration);
        if (globalDeclaration.Initializer is not null)
        {
            builder.Append(" = ");
            builder.Append(_expressionEmitter.Emit(globalDeclaration.Initializer));
        }

        builder.AppendLine(";");
    }

    private static void AppendIndent(StringBuilder builder, int indentLevel) =>
        builder.Append(new string(' ', indentLevel * 4));
}
