using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class CLoopStatementLowerer(
        ImportedNameLowerer nameLowerer,
        CStatementLoweringPipeline statementLowerer,
        CLocalDeclarationLowerer localDeclarationLowerer)
    {
        public CStatementNode LowerFor(ForStatement forStatement)
        {
            var body = statementLowerer.LowerBlock(forStatement.Body);
            if (forStatement.CounterIncrement is not null)
            {
                body.Add(new CExpressionStatement(nameLowerer.LowerExpression(forStatement.CounterIncrement)));
            }

            var loop = new CForStatement(
                localDeclarationLowerer.LowerForInitializer(forStatement.Initializer),
                nameLowerer.LowerExpression(forStatement.Condition),
                nameLowerer.LowerExpression(forStatement.Increment),
                body);

            var prefix = new List<CStatementNode>();
            if (forStatement.CachedRangeEndInitializer is not null)
            {
                prefix.Add(localDeclarationLowerer.LowerForDeclaration(forStatement.CachedRangeEndInitializer));
            }

            if (forStatement.CounterInitializer is not null)
            {
                prefix.Add(localDeclarationLowerer.LowerForDeclaration(forStatement.CounterInitializer));
            }

            return prefix.Count == 0
                ? loop
                : new CBlockStatement(prefix.Concat([loop]).ToList());
        }

        public CWhileStatement LowerWhile(WhileStatement whileStatement) =>
            new(
                nameLowerer.LowerExpression(whileStatement.Condition),
                statementLowerer.LowerBlock(whileStatement.Body));
    }
}
