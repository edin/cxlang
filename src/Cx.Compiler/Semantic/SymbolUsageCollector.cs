using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class SymbolUsageCollector
{
    public SymbolUsageReport Collect(ProgramNode program)
    {
        var builder = new SymbolUsageReportBuilder();
        CollectProgram(program, builder);
        return builder.Build();
    }

    private static void CollectProgram(ProgramNode program, SymbolUsageReportBuilder builder)
    {
        foreach (var typeAlias in program.TypeAliases)
        {
            builder.AddType(typeAlias.TargetTypeNode);
        }

        foreach (var global in program.GlobalVariables)
        {
            builder.AddType(global.TypeNode);
            if (global.Initializer is not null)
            {
                CollectExpression(global.Initializer, builder);
            }
        }

        foreach (var structNode in program.Structs)
        {
            builder.AddType(structNode.Name);
            foreach (var requirement in structNode.Requirements)
            {
                foreach (var argument in requirement.TypeArgumentNodes)
                {
                    builder.AddType(argument);
                }
            }

            foreach (var field in structNode.Fields)
            {
                builder.AddType(field.TypeNode);
            }

            foreach (var method in structNode.Methods)
            {
                CollectFunction(method, builder);
            }
        }

        foreach (var interfaceNode in program.Interfaces)
        {
            builder.AddType(interfaceNode.Name);
            foreach (var method in interfaceNode.Methods)
            {
                builder.AddType(method.ReturnTypeNode);
                foreach (var parameter in method.Parameters.Where(parameter => !parameter.IsVariadic))
                {
                    builder.AddType(parameter.TypeNode);
                }
            }
        }

        foreach (var taggedUnion in program.TaggedUnions)
        {
            builder.AddType(taggedUnion.Name);
            foreach (var variant in taggedUnion.Variants)
            {
                builder.AddType(variant.TypeNode);
            }

            foreach (var method in taggedUnion.Methods)
            {
                CollectFunction(method, builder);
            }
        }

        foreach (var function in program.Functions)
        {
            CollectFunction(function, builder);
        }
    }

    private static void CollectFunction(FunctionNode function, SymbolUsageReportBuilder builder)
    {
        builder.AddFunctionDefinition(GetFunctionKey(function));
        builder.AddType(function.ReturnTypeNode);
        if (function.OwnerTypeNode is not null)
        {
            builder.AddType(function.OwnerTypeNode);
        }

        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            builder.AddType(parameter.TypeNode);
        }

        CollectStatements(function.Body, builder);
    }

    private static void CollectStatements(
        IEnumerable<StatementNode> statements,
        SymbolUsageReportBuilder builder)
    {
        foreach (var statement in statements)
        {
            CollectSemantic(statement, builder);
            switch (statement)
            {
                case LetStatement let:
                    builder.AddType(let.TypeNode);
                    if (let.Initializer is not null)
                    {
                        CollectExpression(let.Initializer, builder);
                    }

                    break;
                case ReturnStatement ret:
                    CollectExpression(ret.Expression, builder);
                    break;
                case CStatement c:
                    CollectExpression(c.Expression, builder);
                    break;
                case IfStatement ifStatement:
                    CollectExpression(ifStatement.Condition, builder);
                    CollectStatements(ifStatement.ThenBody, builder);
                    if (ifStatement.ElseBranch is not null)
                    {
                        CollectStatements([ifStatement.ElseBranch], builder);
                    }

                    break;
                case ElseBlockStatement elseBlock:
                    CollectStatements(elseBlock.Body, builder);
                    break;
                case WhileStatement whileStatement:
                    CollectExpression(whileStatement.Condition, builder);
                    CollectStatements(whileStatement.Body, builder);
                    break;
                case ForStatement forStatement:
                    CollectForInitializer(forStatement.Initializer, builder);
                    CollectExpression(forStatement.Condition, builder);
                    CollectExpression(forStatement.Increment, builder);
                    CollectStatements(forStatement.Body, builder);
                    break;
                case ForeachStatement foreachStatement:
                    CollectForeachBinding(foreachStatement.IndexBinding, builder);
                    CollectForeachBinding(foreachStatement.KeyBinding, builder);
                    CollectForeachBinding(foreachStatement.ValueBinding, builder);
                    CollectExpression(foreachStatement.IterableExpression, builder);
                    CollectStatements(foreachStatement.Body, builder);
                    break;
                case SwitchStatement switchStatement:
                    CollectExpression(switchStatement.Expression, builder);
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        CollectExpression(switchCase.Pattern, builder);
                        CollectStatements(switchCase.Body, builder);
                    }

                    CollectStatements(switchStatement.DefaultBody, builder);
                    break;
                case MatchStatement matchStatement:
                    CollectExpression(matchStatement.Expression, builder);
                    foreach (var arm in matchStatement.Arms)
                    {
                        CollectStatements(arm.Body, builder);
                    }

                    break;
            }
        }
    }

    private static void CollectForInitializer(
        ForInitializerNode initializer,
        SymbolUsageReportBuilder builder)
    {
        CollectSemantic(initializer, builder);
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                builder.AddType(declaration.TypeNode);
                if (declaration.Initializer is not null)
                {
                    CollectExpression(declaration.Initializer, builder);
                }

                break;
            case ForExpressionInitializerNode expression:
                CollectExpression(expression.Expression, builder);
                break;
        }
    }

    private static void CollectForeachBinding(
        ForeachBinding? binding,
        SymbolUsageReportBuilder builder)
    {
        if (binding is null)
        {
            return;
        }

        CollectSemantic(binding, builder);
        builder.AddType(binding.TypeNode);
    }

    private static void CollectExpression(
        ExpressionNode expression,
        SymbolUsageReportBuilder builder)
    {
        CollectSemantic(expression, builder);
        switch (expression)
        {
            case ParenthesizedExpressionNode parenthesized:
                CollectExpression(parenthesized.Expression, builder);
                break;
            case CastExpressionNode cast:
                builder.AddType(cast.TargetTypeNode);
                CollectExpression(cast.Expression, builder);
                break;
            case UnaryExpressionNode unary:
                CollectExpression(unary.Operand, builder);
                break;
            case PostfixExpressionNode postfix:
                CollectExpression(postfix.Operand, builder);
                break;
            case SizeOfExpressionNode sizeOf:
                builder.AddType(sizeOf.TypeOperandNode);
                if (sizeOf.ExpressionOperand is not null)
                {
                    CollectExpression(sizeOf.ExpressionOperand, builder);
                }

                break;
            case BinaryExpressionNode binary:
                CollectExpression(binary.Left, builder);
                CollectExpression(binary.Right, builder);
                break;
            case ScalarRangeExpressionNode range:
                CollectExpression(range.Start, builder);
                CollectExpression(range.End, builder);
                break;
            case ConditionalExpressionNode conditional:
                CollectExpression(conditional.Condition, builder);
                CollectExpression(conditional.WhenTrue, builder);
                CollectExpression(conditional.WhenFalse, builder);
                break;
            case InitializerExpressionNode initializer:
                builder.AddType(initializer.TypeNameNode);
                foreach (var field in initializer.Fields)
                {
                    CollectExpression(field.Value, builder);
                }

                foreach (var value in initializer.Values)
                {
                    CollectExpression(value, builder);
                }

                break;
            case FunctionExpressionNode function:
                builder.AddType(function.ReturnTypeNode);
                foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
                {
                    builder.AddType(parameter.TypeNode);
                }

                if (function.ExpressionBody is not null)
                {
                    CollectExpression(function.ExpressionBody, builder);
                }

                if (function.BlockBody is not null)
                {
                    CollectStatements(function.BlockBody, builder);
                }

                break;
            case AssignmentExpressionNode assignment:
                CollectExpression(assignment.Target, builder);
                CollectExpression(assignment.Value, builder);
                break;
            case CallExpressionNode call:
                CollectExpression(call.Callee, builder);
                foreach (var argument in call.Arguments)
                {
                    CollectExpression(argument, builder);
                }

                break;
            case GenericCallExpressionNode call:
                CollectExpression(call.Callee, builder);
                foreach (var argument in call.TypeArgumentNodes)
                {
                    builder.AddType(argument);
                }

                foreach (var argument in call.Arguments)
                {
                    CollectExpression(argument, builder);
                }

                break;
            case MemberExpressionNode member:
                CollectExpression(member.Target, builder);
                break;
            case IndexExpressionNode index:
                CollectExpression(index.Target, builder);
                CollectExpression(index.Index, builder);
                break;
        }
    }

    private static void CollectSemantic(
        SyntaxNode node,
        SymbolUsageReportBuilder builder)
    {
        if (node.Semantic.Symbol is { } symbol)
        {
            builder.AddSymbol(symbol);
        }

        if (node.Semantic.ResolvedCall is { } call)
        {
            builder.AddFunctionCall(GetFunctionKey(call.Function));
            foreach (var argument in call.TypeArguments)
            {
                builder.AddType(argument);
            }
        }

        if (node.Semantic.Type is { } type)
        {
            builder.AddResolvedType(type);
        }
    }

    private static string GetFunctionKey(FunctionNode function) =>
        function.OwnerTypeNode is null ? function.Name : $"{function.OwnerTypeNode.TypeName}.{function.Name}";
}

internal sealed record SymbolUsageReport(
    IReadOnlySet<string> FunctionDefinitions,
    IReadOnlySet<string> FunctionCalls,
    IReadOnlySet<string> Symbols,
    IReadOnlySet<string> TypeReferences,
    IReadOnlySet<string> ResolvedTypeReferences);

internal sealed class SymbolUsageReportBuilder
{
    private readonly HashSet<string> _functionDefinitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _functionCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _symbols = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typeReferences = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvedTypeReferences = new(StringComparer.Ordinal);

    public void AddFunctionDefinition(string name) => Add(_functionDefinitions, name);

    public void AddFunctionCall(string name) => Add(_functionCalls, name);

    public void AddSymbol(Symbol symbol) =>
        Add(_symbols, $"{symbol.Kind}:{symbol.Name}");

    public void AddType(TypeNode? type) => Add(_typeReferences, type?.TypeName);

    public void AddType(string? type) => Add(_typeReferences, type);

    public void AddResolvedType(TypeRef type) =>
        Add(_resolvedTypeReferences, TypeRefFormatter.ToCxString(type));

    public SymbolUsageReport Build() =>
        new(
            _functionDefinitions.ToHashSet(StringComparer.Ordinal),
            _functionCalls.ToHashSet(StringComparer.Ordinal),
            _symbols.ToHashSet(StringComparer.Ordinal),
            _typeReferences.ToHashSet(StringComparer.Ordinal),
            _resolvedTypeReferences.ToHashSet(StringComparer.Ordinal));

    private static void Add(HashSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }

}
