using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class CLocalDeclarationLowerer(ImportedNameLowerer nameLowerer)
    {
        public CLocalDeclarationStatement LowerLet(LetStatement let)
        {
            var type = LetStatementTypeText(let);
            var declaration = (let.IsConst ? "const " : "")
                + LowerDeclaration(let.TypeNode, type, let.Name, nameLowerer.SelfType);
            var initializer = let.Initializer is null
                ? null
                : nameLowerer.LowerInitializerExpression(let.TypeNode?.Semantic.Type, type, let.Initializer);
            return new CLocalDeclarationStatement(declaration, initializer);
        }

        public CLocalDeclarationStatement LowerForDeclaration(ForDeclarationInitializerNode declaration) =>
            new(
                DeclarationText(declaration),
                declaration.Initializer is null
                    ? null
                    : nameLowerer.LowerInitializerExpression(
                        declaration.TypeNode?.Semantic.Type,
                        ForDeclarationInitializerTypeText(declaration),
                        declaration.Initializer));

        public CForInitializerNode LowerForInitializer(ForInitializerNode initializer) => initializer switch
        {
            ForDeclarationInitializerNode declaration => new CDeclarationForInitializer(
                DeclarationText(declaration),
                declaration.Initializer is null
                    ? null
                    : nameLowerer.LowerInitializerExpression(
                        declaration.TypeNode?.Semantic.Type,
                        ForDeclarationInitializerTypeText(declaration),
                        declaration.Initializer)),
            ForExpressionInitializerNode expression => new CExpressionForInitializer(
                nameLowerer.LowerExpression(expression.Expression)),
            _ => new CEmptyForInitializer(),
        };

        private string DeclarationText(ForDeclarationInitializerNode declaration) =>
            $"{(declaration.IsConst ? "const " : "")}{LowerDeclaration(declaration.TypeNode, ForDeclarationInitializerTypeText(declaration), declaration.Name, nameLowerer.SelfType)}";
    }
}
