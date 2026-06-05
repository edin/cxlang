using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class AstCompletenessAnalyzer(DiagnosticBag diagnostics)
{
    public void Analyze(IEnumerable<ProgramNode> programs)
    {
        foreach (var program in programs)
        {
            AnalyzeProgram(program);
        }
    }

    private void AnalyzeProgram(ProgramNode program)
    {
        foreach (var global in program.GlobalVariables)
        {
            AnalyzeExpression(global.Initializer);
        }

        foreach (var function in program.Functions)
        {
            AnalyzeStatements(function.Body);
        }

        foreach (var structNode in program.Structs)
        {
            foreach (var method in structNode.Methods)
            {
                AnalyzeStatements(method.Body);
            }
        }

        foreach (var union in program.TaggedUnions)
        {
            foreach (var method in union.Methods)
            {
                AnalyzeStatements(method.Body);
            }
        }
    }

    private void AnalyzeStatements(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    AnalyzeExpression(let.Initializer);
                    break;
                case ReturnStatement ret:
                    AnalyzeExpression(ret.Expression);
                    break;
                case BreakStatement:
                case ContinueStatement:
                    break;
                case CStatement c:
                    AnalyzeExpression(c.Expression);
                    break;
                case IfStatement ifStatement:
                    AnalyzeExpression(ifStatement.Condition);
                    AnalyzeStatements(ifStatement.ThenBody);
                    if (ifStatement.ElseBranch is not null)
                    {
                        AnalyzeStatements([ifStatement.ElseBranch]);
                    }

                    break;
                case ElseBlockStatement elseBlock:
                    AnalyzeStatements(elseBlock.Body);
                    break;
                case WhileStatement whileStatement:
                    AnalyzeExpression(whileStatement.Condition);
                    AnalyzeStatements(whileStatement.Body);
                    break;
                case ForStatement forStatement:
                    AnalyzeForInitializer(forStatement.Initializer);
                    AnalyzeExpression(forStatement.Condition);
                    AnalyzeExpression(forStatement.Increment);
                    AnalyzeStatements(forStatement.Body);
                    break;
                case ForeachStatement foreachStatement:
                    AnalyzeExpression(foreachStatement.IterableExpression);
                    AnalyzeStatements(foreachStatement.Body);
                    break;
                case SwitchStatement switchStatement:
                    AnalyzeExpression(switchStatement.Expression);
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        AnalyzeExpression(switchCase.Pattern);
                        AnalyzeStatements(switchCase.Body);
                    }

                    AnalyzeStatements(switchStatement.DefaultBody);
                    break;
                case MatchStatement matchStatement:
                    AnalyzeExpression(matchStatement.Expression);
                    foreach (var arm in matchStatement.Arms)
                    {
                        AnalyzeStatements(arm.Body);
                    }

                    break;
            }
        }
    }

    private void AnalyzeForInitializer(ForInitializerNode initializer)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                AnalyzeExpression(declaration.Initializer);
                break;
            case ForExpressionInitializerNode expression:
                AnalyzeExpression(expression.Expression);
                break;
        }
    }

    private void AnalyzeExpression(ExpressionNode? expression)
    {
        if (expression is null)
        {
            return;
        }

        switch (expression)
        {
            case RawExpressionNode raw:
                diagnostics.Report(raw.Location, $"AST fallback raw expression remains: '{TrimForDiagnostic(raw.SourceText)}'.");
                break;
            case ParenthesizedExpressionNode parenthesized:
                AnalyzeExpression(parenthesized.Expression);
                break;
            case CastExpressionNode cast:
                AnalyzeExpression(cast.Expression);
                break;
            case UnaryExpressionNode unary:
                AnalyzeExpression(unary.Operand);
                break;
            case PostfixExpressionNode postfix:
                AnalyzeExpression(postfix.Operand);
                break;
            case SizeOfExpressionNode sizeOf:
                AnalyzeExpression(sizeOf.ExpressionOperand);
                break;
            case BinaryExpressionNode binary:
                AnalyzeExpression(binary.Left);
                AnalyzeExpression(binary.Right);
                break;
            case ScalarRangeExpressionNode range:
                AnalyzeExpression(range.Start);
                AnalyzeExpression(range.End);
                break;
            case ConditionalExpressionNode conditional:
                AnalyzeExpression(conditional.Condition);
                AnalyzeExpression(conditional.WhenTrue);
                AnalyzeExpression(conditional.WhenFalse);
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    AnalyzeExpression(field.Value);
                }

                foreach (var value in initializer.Values)
                {
                    AnalyzeExpression(value);
                }

                break;
            case FunctionExpressionNode function:
                AnalyzeExpression(function.ExpressionBody);
                if (function.BlockBody is not null)
                {
                    AnalyzeStatements(function.BlockBody);
                }

                break;
            case AssignmentExpressionNode assignment:
                AnalyzeExpression(assignment.Target);
                AnalyzeExpression(assignment.Value);
                break;
            case CallExpressionNode call:
                AnalyzeExpression(call.Callee);
                foreach (var argument in call.Arguments)
                {
                    AnalyzeExpression(argument);
                }

                break;
            case GenericCallExpressionNode call:
                AnalyzeExpression(call.Callee);
                foreach (var argument in call.Arguments)
                {
                    AnalyzeExpression(argument);
                }

                break;
            case MemberExpressionNode member:
                AnalyzeExpression(member.Target);
                break;
            case IndexExpressionNode index:
                AnalyzeExpression(index.Target);
                AnalyzeExpression(index.Index);
                break;
        }
    }

    private static string TrimForDiagnostic(string text)
    {
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 120 ? text : text[..117] + "...";
    }
}
