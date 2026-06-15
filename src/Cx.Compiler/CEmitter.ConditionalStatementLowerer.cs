using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class CConditionalStatementLowerer(
        ImportedNameLowerer nameLowerer,
        CStatementLoweringPipeline statementLowerer)
    {
        public CIfStatement LowerIf(IfStatement ifStatement) =>
            new(
                nameLowerer.LowerExpression(ifStatement.Condition),
                statementLowerer.LowerBlock(ifStatement.ThenBody),
                LowerElse(ifStatement.ElseBranch));

        private CElseClause? LowerElse(StatementNode? elseBranch) => elseBranch switch
        {
            null => null,
            IfStatement elseIf => new CElseIfClause(LowerIf(elseIf)),
            ElseBlockStatement elseBlock => new CElseBlockClause(statementLowerer.LowerBlock(elseBlock.Body)),
            _ => throw UnsupportedElseBranch(elseBranch),
        };

        private static InvalidOperationException UnsupportedElseBranch(StatementNode elseBranch) =>
            new($"Internal C emission error: unsupported else branch '{elseBranch.GetType().Name}' at {elseBranch.Location} reached C statement lowering.");
    }
}
