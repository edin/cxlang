using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class TypeNodeRewriter
{
    public static TypeNode? Rewrite(
        TypeNode? typeNode,
        Func<string, string> rewriteTypeName,
        IReadOnlyDictionary<string, TypeRef> substitutions,
        TypeRef? selfType = null)
    {
        if (typeNode is null)
        {
            return null;
        }

        var rewritten = TypeNode.CreateFromText(typeNode.Location, rewriteTypeName(typeNode.TypeName));
        SyntaxNode.CloneSemantic(typeNode, rewritten);
        if (typeNode.Semantic.Type is null)
        {
            return rewritten;
        }

        var rewrittenType = TypeRefRewriter.Substitute(typeNode.Semantic.Type, substitutions);
        if (selfType is not null)
        {
            rewrittenType = TypeRefRewriter.SubstituteSelf(rewrittenType, selfType);
        }

        if (string.Equals(TypeRefFormatter.ToCxString(rewrittenType), rewritten.TypeName, StringComparison.Ordinal))
        {
            rewritten.Semantic.Type = rewrittenType;
        }
        else
        {
            rewritten.Semantic.Type = null;
        }

        return rewritten;
    }
}
