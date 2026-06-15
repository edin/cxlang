using Cx.Compiler.C;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class MatchLoweringPass
{
    public static ProgramNode Lower(ProgramNode program, DiagnosticBag diagnostics) =>
        AstTransformPipeline
            .Create()
            .Transform(new MatchTransform(program, new CAbiNameService(program.TypeAdapters)))
            .Run(program);

    private sealed class MatchTransform(ProgramNode program, CAbiNameService abiNames) : IAstNodeTransform<MatchStatement>
    {
        private readonly TypeRefParser _typeRefParser = new(program);

        public AstTransformResult Transform(MatchStatement node, AstTransformContext context)
        {
            var sourceType = ResolveSourceType(node.Expression, context);
            var matchedType = TypeRefFacts.StripPointersAndAliases(sourceType);
            var matchedTypeName = TypeRefFacts.GetBaseName(matchedType);
            if (matchedTypeName is null)
            {
                return AstTransformResult.Unchanged;
            }

            if (program.TaggedUnions.FirstOrDefault(union =>
                string.Equals(union.Name, matchedTypeName, StringComparison.Ordinal)) is { } taggedUnion)
            {
                return LowerTaggedUnionMatch(node, context, taggedUnion, sourceType);
            }

            if (program.Interfaces.FirstOrDefault(interfaceNode =>
                string.Equals(interfaceNode.Name, matchedTypeName, StringComparison.Ordinal)) is { } interfaceNode)
            {
                return LowerInterfaceMatch(node, context, interfaceNode, sourceType);
            }

            return AstTransformResult.Unchanged;
        }

        private static AstTransformResult LowerTaggedUnionMatch(
            MatchStatement node,
            AstTransformContext context,
            TaggedUnionNode taggedUnion,
            TypeRef sourceType)
        {
            var (prefix, source) = PrepareSource(node.Expression, context, sourceType);
            var cases = new List<SwitchCaseNode>();
            var defaultBody = new List<StatementNode>();
            foreach (var arm in node.Arms)
            {
                var body = BuildTaggedUnionArmBody(arm, taggedUnion, source);
                if (arm.Pattern == "_")
                {
                    defaultBody.AddRange(body);
                    continue;
                }

                cases.Add(new SwitchCaseNode(
                    arm.Location,
                    Name(arm.Location, $"{taggedUnion.Name}_Tag_{arm.Pattern}"),
                    body));
            }

            prefix.Add(new SwitchStatement(
                node.Location,
                Member(source, "tag"),
                cases,
                defaultBody));
            return AstTransformResult.ReplaceStatements(prefix);
        }

        private AstTransformResult LowerInterfaceMatch(
            MatchStatement node,
            AstTransformContext context,
            InterfaceNode interfaceNode,
            TypeRef sourceType)
        {
            var implementations = program.Structs
                .Where(structNode => structNode.Requirements.Any(requirement =>
                    string.Equals(requirement.Name, interfaceNode.Name, StringComparison.Ordinal)))
                .Select(structNode => structNode.Name)
                .ToHashSet(StringComparer.Ordinal);
            var (prefix, source) = PrepareSource(node.Expression, context, sourceType);
            var cases = new List<SwitchCaseNode>();
            var defaultBody = new List<StatementNode>();
            foreach (var arm in node.Arms)
            {
                var body = BuildInterfaceArmBody(arm, source, implementations);
                if (arm.Pattern == "_")
                {
                    defaultBody.AddRange(body);
                    continue;
                }

                cases.Add(new SwitchCaseNode(
                    arm.Location,
                    Name(arm.Location, abiNames.TypeIdName(arm.Pattern)),
                    body));
            }

            prefix.Add(new SwitchStatement(
                node.Location,
                Member(Member(source, "vtable"), "type_id"),
                cases,
                defaultBody));
            return AstTransformResult.ReplaceStatements(prefix);
        }

        private static List<StatementNode> BuildTaggedUnionArmBody(
            MatchArmNode arm,
            TaggedUnionNode taggedUnion,
            ExpressionNode source)
        {
            var body = new List<StatementNode>();
            if (arm.BindingName is not null
                && arm.Pattern != "_"
                && taggedUnion.Variants.FirstOrDefault(variant => variant.Name == arm.Pattern) is { } variant)
            {
                body.Add(new LetStatement(
                    arm.Location,
                    IsConst: false,
                    arm.BindingName,
                    Member(Member(source, "as"), arm.Pattern),
                    variant.TypeNode));
            }

            body.AddRange(arm.Body);
            AddBreakIfNeeded(body, arm.Location);
            return body;
        }

        private static List<StatementNode> BuildInterfaceArmBody(
            MatchArmNode arm,
            ExpressionNode source,
            IReadOnlySet<string> implementations)
        {
            var body = new List<StatementNode>();
            if (arm.BindingName is not null
                && arm.Pattern != "_"
                && implementations.Contains(arm.Pattern))
            {
                var targetType = TypeNode.CreateFromText(arm.Location, arm.Pattern + "*");
                body.Add(new LetStatement(
                    arm.Location,
                    IsConst: false,
                    arm.BindingName,
                    new CastExpressionNode(
                        arm.Location,
                        $"({arm.Pattern}*){Member(source, "state").SourceText}",
                        Member(source, "state"),
                        targetType),
                    targetType));
            }

            body.AddRange(arm.Body);
            AddBreakIfNeeded(body, arm.Location);
            return body;
        }

        private static (List<StatementNode> Prefix, ExpressionNode Source) PrepareSource(
            ExpressionNode source,
            AstTransformContext context,
            TypeRef sourceType)
        {
            if (source is NameExpressionNode)
            {
                return ([], source);
            }

            var sourceName = context.UniqueName("__cx_match_source");
            var typeNode = TypeNode.CreateFromText(source.Location, TypeRefFormatter.ToCxString(sourceType));
            return ([
                new LetStatement(
                    source.Location,
                    IsConst: true,
                    sourceName,
                    source,
                    typeNode),
            ], Name(source.Location, sourceName));
        }

        private TypeRef ResolveSourceType(ExpressionNode expression, AstTransformContext context)
        {
            if (expression.Semantic.Type is { } semanticType)
            {
                return semanticType;
            }

            if (expression is NameExpressionNode name && context.TryGetLocalType(name.SourceText, out var localType))
            {
                return _typeRefParser.Parse(localType);
            }

            return new TypeRef.Unknown();
        }

        private static void AddBreakIfNeeded(List<StatementNode> body, Location location)
        {
            if (body.LastOrDefault() is not ReturnStatement)
            {
                body.Add(new BreakStatement(location));
            }
        }

        private static NameExpressionNode Name(Location location, string name) =>
            new(location, name);

        private static MemberExpressionNode Member(ExpressionNode target, string memberName) =>
            new(
                target.Location,
                $"{target.SourceText}.{memberName}",
                target,
                memberName);
    }
}
