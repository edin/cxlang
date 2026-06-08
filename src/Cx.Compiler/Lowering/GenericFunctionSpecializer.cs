using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class GenericFunctionSpecializer
{
    public static FunctionNode Specialize(FunctionNode function, IReadOnlyList<string> arguments)
    {
        var substitutions = function.TypeParameters
            .Zip(arguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var typeSubstitutions = GenericTypeSubstitutionBuilder.Build(substitutions);
        var selfType = function.OwnerType is not null && arguments.Count > 0
            ? $"{function.OwnerType}<{string.Join(",", arguments)}>"
            : function.OwnerType;
        var selfTypeRef = GenericTypeSubstitutionBuilder.ParseType(selfType);
        var specialized = function with
        {
            TypeParameters = [],
            TypeArgumentNodes = arguments
                .Select(argument => new TypeNode(function.Location, argument, TypeSyntaxParser.Parse(argument)))
                .ToList(),
            ReturnTypeNode = SubstituteTypeNode(function.ReturnTypeNode, substitutions, typeSubstitutions, selfType, selfTypeRef),
            Parameters = function.Parameters
                .Select(parameter => parameter with
                {
                    TypeNode = SubstituteTypeNode(parameter.TypeNode, substitutions, typeSubstitutions, selfType, selfTypeRef),
                })
                .ToList(),
            Body = function.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
        };
        specialized.Semantic.ModuleName = function.Semantic.ModuleName;
        EnsureFunctionSymbol(specialized);
        return specialized;
    }

    public static void EnsureFunctionSymbol(FunctionNode function)
    {
        if (function.Semantic.Symbol is { Kind: SymbolKind.Function })
        {
            return;
        }

        function.Semantic.Symbol = new Symbol(
            function.Name,
            SymbolKind.Function,
            function.ReturnType,
            function.Location,
            function);
    }

    private static StatementNode SubstituteStatement(
        StatementNode statement,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions)
    {
        return statement switch
        {
            LetStatement let => let with
            {
                TypeNode = SubstituteTypeNode(let.TypeNode, substitutions, typeSubstitutions),
                Initializer = SubstituteOptionalExpression(let.Initializer, substitutions, typeSubstitutions),
            },
            ReturnStatement ret => ret with { Expression = SubstituteExpression(ret.Expression, substitutions, typeSubstitutions) },
            CStatement c => c with { Expression = SubstituteExpression(c.Expression, substitutions, typeSubstitutions) },
            IfStatement ifStatement => ifStatement with
            {
                Condition = SubstituteExpression(ifStatement.Condition, substitutions, typeSubstitutions),
                ThenBody = ifStatement.ThenBody.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
                ElseBranch = ifStatement.ElseBranch is null ? null : SubstituteStatement(ifStatement.ElseBranch, substitutions, typeSubstitutions),
            },
            ElseBlockStatement elseBlock => elseBlock with
            {
                Body = elseBlock.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
            },
            WhileStatement whileStatement => whileStatement with
            {
                Condition = SubstituteExpression(whileStatement.Condition, substitutions, typeSubstitutions),
                Body = whileStatement.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
            },
            ForStatement forStatement => forStatement with
            {
                Initializer = SubstituteForInitializer(forStatement.Initializer, substitutions, typeSubstitutions),
                Condition = SubstituteExpression(forStatement.Condition, substitutions, typeSubstitutions),
                Increment = SubstituteExpression(forStatement.Increment, substitutions, typeSubstitutions),
                Body = forStatement.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
            },
            ForeachStatement foreachStatement => foreachStatement with
            {
                IndexBinding = SubstituteForeachBinding(foreachStatement.IndexBinding, substitutions, typeSubstitutions),
                KeyBinding = SubstituteForeachBinding(foreachStatement.KeyBinding, substitutions, typeSubstitutions),
                ValueBinding = SubstituteForeachBinding(foreachStatement.ValueBinding, substitutions, typeSubstitutions)!,
                IterableExpression = SubstituteExpression(foreachStatement.IterableExpression, substitutions, typeSubstitutions),
                Body = foreachStatement.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
            },
            SwitchStatement switchStatement => switchStatement with
            {
                Expression = SubstituteExpression(switchStatement.Expression, substitutions, typeSubstitutions),
                Cases = switchStatement.Cases.Select(switchCase => switchCase with
                {
                    Pattern = SubstituteExpression(switchCase.Pattern, substitutions, typeSubstitutions),
                    Body = switchCase.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
                }).ToList(),
                DefaultBody = switchStatement.DefaultBody.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
            },
            MatchStatement matchStatement => matchStatement with
            {
                Expression = SubstituteExpression(matchStatement.Expression, substitutions, typeSubstitutions),
                Arms = matchStatement.Arms.Select(arm => arm with
                {
                    Body = arm.Body.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
                }).ToList(),
            },
            _ => statement,
        };
    }

    private static ExpressionNode? SubstituteOptionalExpression(
        ExpressionNode? expression,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions) =>
        expression is null ? null : SubstituteExpression(expression, substitutions, typeSubstitutions);

    private static ForInitializerNode SubstituteForInitializer(
        ForInitializerNode initializer,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions) => initializer switch
    {
        ForDeclarationInitializerNode declaration => declaration with
        {
            TypeNode = SubstituteTypeNode(declaration.TypeNode, substitutions, typeSubstitutions),
            Initializer = SubstituteOptionalExpression(declaration.Initializer, substitutions, typeSubstitutions),
        },
        ForExpressionInitializerNode expression => expression with
        {
            Expression = SubstituteExpression(expression.Expression, substitutions, typeSubstitutions),
        },
        _ => initializer,
    };

    private static ForeachBinding? SubstituteForeachBinding(
        ForeachBinding? binding,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions) =>
        binding is null
            ? null
            : binding with
            {
                TypeNode = SubstituteTypeNode(binding.TypeNode, substitutions, typeSubstitutions),
            };

    private static ExpressionNode SubstituteExpression(
        ExpressionNode expression,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions)
    {
        var sourceText = GenericTypeStringRewriter.Substitute(expression.SourceText, substitutions);
        return expression switch
        {
            ParenthesizedExpressionNode parenthesized => parenthesized with
            {
                SourceText = sourceText,
                Expression = SubstituteExpression(parenthesized.Expression, substitutions, typeSubstitutions),
            },
            CastExpressionNode cast => cast with
            {
                SourceText = sourceText,
                TargetTypeNode = SubstituteTypeNode(cast.TargetTypeNode, substitutions, typeSubstitutions),
                Expression = SubstituteExpression(cast.Expression, substitutions, typeSubstitutions),
            },
            UnaryExpressionNode unary => unary with
            {
                SourceText = sourceText,
                Operand = SubstituteExpression(unary.Operand, substitutions, typeSubstitutions),
            },
            PostfixExpressionNode postfix => postfix with
            {
                SourceText = sourceText,
                Operand = SubstituteExpression(postfix.Operand, substitutions, typeSubstitutions),
            },
            SizeOfExpressionNode sizeOf => sizeOf with
            {
                SourceText = sourceText,
                TypeOperandNode = SubstituteTypeNode(sizeOf.TypeOperandNode, substitutions, typeSubstitutions),
                ExpressionOperand = SubstituteOptionalExpression(sizeOf.ExpressionOperand, substitutions, typeSubstitutions),
            },
            BinaryExpressionNode binary => binary with
            {
                SourceText = sourceText,
                Left = SubstituteExpression(binary.Left, substitutions, typeSubstitutions),
                Right = SubstituteExpression(binary.Right, substitutions, typeSubstitutions),
            },
            ScalarRangeExpressionNode range => range with
            {
                SourceText = sourceText,
                Start = SubstituteExpression(range.Start, substitutions, typeSubstitutions),
                End = SubstituteExpression(range.End, substitutions, typeSubstitutions),
            },
            ConditionalExpressionNode conditional => conditional with
            {
                SourceText = sourceText,
                Condition = SubstituteExpression(conditional.Condition, substitutions, typeSubstitutions),
                WhenTrue = SubstituteExpression(conditional.WhenTrue, substitutions, typeSubstitutions),
                WhenFalse = SubstituteExpression(conditional.WhenFalse, substitutions, typeSubstitutions),
            },
            InitializerExpressionNode initializer => initializer with
            {
                SourceText = sourceText,
                TypeNameNode = SubstituteTypeNode(initializer.TypeNameNode, substitutions, typeSubstitutions),
                Fields = initializer.Fields.Select(field => field with { Value = SubstituteExpression(field.Value, substitutions, typeSubstitutions) }).ToList(),
                Values = initializer.Values.Select(value => SubstituteExpression(value, substitutions, typeSubstitutions)).ToList(),
            },
            FunctionExpressionNode functionExpression => functionExpression with
            {
                SourceText = sourceText,
                Parameters = functionExpression.Parameters
                    .Select(parameter => parameter with
                    {
                        TypeNode = SubstituteTypeNode(parameter.TypeNode, substitutions, typeSubstitutions),
                    })
                    .ToList(),
                ReturnTypeNode = SubstituteTypeNode(functionExpression.ReturnTypeNode, substitutions, typeSubstitutions),
                ExpressionBody = SubstituteOptionalExpression(functionExpression.ExpressionBody, substitutions, typeSubstitutions),
                BlockBody = functionExpression.BlockBody?.Select(statement => SubstituteStatement(statement, substitutions, typeSubstitutions)).ToList(),
            },
            AssignmentExpressionNode assignment => assignment with
            {
                SourceText = sourceText,
                Target = SubstituteExpression(assignment.Target, substitutions, typeSubstitutions),
                Value = SubstituteExpression(assignment.Value, substitutions, typeSubstitutions),
            },
            CallExpressionNode call => call with
            {
                SourceText = sourceText,
                Callee = SubstituteExpression(call.Callee, substitutions, typeSubstitutions),
                Arguments = call.Arguments.Select(argument => SubstituteExpression(argument, substitutions, typeSubstitutions)).ToList(),
            },
            GenericCallExpressionNode call => call with
            {
                SourceText = sourceText,
                Callee = SubstituteExpression(call.Callee, substitutions, typeSubstitutions),
                TypeArgumentNodes = call.TypeArgumentNodes
                    .Select(typeNode => SubstituteTypeNode(typeNode, substitutions, typeSubstitutions)!)
                    .ToList(),
                Arguments = call.Arguments.Select(argument => SubstituteExpression(argument, substitutions, typeSubstitutions)).ToList(),
            },
            MemberExpressionNode member => member with
            {
                SourceText = sourceText,
                Target = SubstituteExpression(member.Target, substitutions, typeSubstitutions),
            },
            IndexExpressionNode index => index with
            {
                SourceText = sourceText,
                Target = SubstituteExpression(index.Target, substitutions, typeSubstitutions),
                Index = SubstituteExpression(index.Index, substitutions, typeSubstitutions),
            },
            _ => expression with { SourceText = sourceText },
        };
    }

    private static TypeNode? SubstituteTypeNode(
        TypeNode? typeNode,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions,
        string? selfType = null,
        TypeRef? selfTypeRef = null) =>
        TypeNodeRewriter.Rewrite(
            typeNode,
            typeName => GenericTypeStringRewriter.SubstituteAndSelf(typeName, substitutions, selfType),
            typeSubstitutions,
            selfTypeRef);

}
