using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class TypeResolutionPass(DiagnosticBag diagnostics)
{
    private TypeRefParser? _parser;
    private TypeSyntaxTypeRefConverter? _typeSyntaxConverter;

    public void Resolve(ProgramNode program)
    {
        _parser = new TypeRefParser(program);
        _typeSyntaxConverter = new TypeSyntaxTypeRefConverter(program);

        foreach (var typeAlias in program.TypeAliases)
        {
            ResolveType(typeAlias, typeAlias.TargetTypeNode);
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            ResolveFunctionSignature(externFunction, externFunction.ReturnTypeNode, externFunction.Parameters);
        }

        foreach (var global in program.GlobalVariables)
        {
            ResolveType(global, global.TypeNode);
        }

        foreach (var attribute in program.AttributeDeclarations)
        {
            foreach (var field in attribute.Fields)
            {
                ResolveType(field, field.TypeNode);
            }
        }

        foreach (var requirement in program.Requirements)
        {
            ResolveGenericConstraints(requirement.GenericConstraints);
            foreach (var member in requirement.Members)
            {
                if (member is RequirementFunctionNode function)
                {
                    ResolveFunctionSignature(function, function.ReturnTypeNode, function.Parameters);
                }
                else if (member is RequirementFieldNode field)
                {
                    ResolveType(field, field.TypeNode);
                }
            }
        }

        foreach (var interfaceNode in program.Interfaces)
        {
            foreach (var method in interfaceNode.Methods)
            {
                ResolveInterfaceMethod(method);
            }
        }

        foreach (var structNode in program.Structs)
        {
            ResolveGenericConstraints(structNode.GenericConstraints);
            ResolveStructRequirements(structNode.Requirements);
            foreach (var field in structNode.Fields)
            {
                ResolveType(field, field.TypeNode);
            }

            foreach (var method in structNode.Methods)
            {
                ResolveFunction(method);
            }
        }

        foreach (var adapter in program.TypeAdapters)
        {
            ResolveType(adapter, adapter.BaseTypeNode);
            foreach (var expose in adapter.ExposedMethods)
            {
                if (expose.ReturnTypeNode is not null)
                {
                    ResolveType(expose, expose.ReturnTypeNode);
                }
            }

            foreach (var method in adapter.Methods)
            {
                ResolveFunction(method);
            }
        }

        foreach (var union in program.TaggedUnions)
        {
            foreach (var variant in union.Variants)
            {
                ResolveType(variant, variant.TypeNode);
            }

            foreach (var method in union.Methods)
            {
                ResolveFunction(method);
            }
        }

        foreach (var function in program.Functions)
        {
            ResolveFunction(function);
        }
    }

    private void ResolveFunction(FunctionNode function)
    {
        ResolveGenericConstraints(function.GenericConstraints);
        ResolveFunctionSignature(function, function.ReturnTypeNode, function.Parameters);
        ResolveStatements(function.Body);
    }

    private void ResolveGenericConstraints(IReadOnlyList<GenericConstraintNode> constraints)
    {
        foreach (var constraint in constraints)
        {
            ResolveStructRequirements(constraint.Requirements);
        }
    }

    private void ResolveStructRequirements(IReadOnlyList<StructRequirementNode> requirements)
    {
        foreach (var requirement in requirements)
        {
            ResolveTypeArgumentNodes(requirement.TypeArgumentNodes);
        }
    }

    private void ResolveInterfaceMethod(InterfaceMethodNode method)
    {
        ResolveFunctionSignature(method, method.ReturnTypeNode, method.Parameters);
    }

    private void ResolveFunctionSignature(
        SyntaxNode node,
        TypeNode? typeNode,
        IReadOnlyList<ParameterNode> parameters)
    {
        ResolveType(node, typeNode);
        foreach (var parameter in parameters.Where(parameter => !parameter.IsVariadic))
        {
            ResolveType(parameter, parameter.TypeNode);
        }
    }

    private void ResolveStatements(IReadOnlyList<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            ResolveStatement(statement);
        }
    }

    private void ResolveStatement(StatementNode statement)
    {
        switch (statement)
        {
            case LetStatement let:
                ResolveType(let, let.TypeNode);
                ResolveExpression(let.Initializer);
                break;
            case ReturnStatement { Expression: not null } ret:
                ResolveExpression(ret.Expression);
                break;
            case CStatement c:
                ResolveExpression(c.Expression);
                break;
            case IfStatement ifStatement:
                ResolveExpression(ifStatement.Condition);
                ResolveStatements(ifStatement.ThenBody);
                if (ifStatement.ElseBranch is not null)
                {
                    ResolveStatement(ifStatement.ElseBranch);
                }

                break;
            case ElseBlockStatement elseBlock:
                ResolveStatements(elseBlock.Body);
                break;
            case WhileStatement whileStatement:
                ResolveExpression(whileStatement.Condition);
                ResolveStatements(whileStatement.Body);
                break;
            case ForStatement forStatement:
                ResolveForInitializer(forStatement.Initializer);
                ResolveExpression(forStatement.Condition);
                ResolveExpression(forStatement.Increment);
                ResolveStatements(forStatement.Body);
                break;
            case ForeachStatement foreachStatement:
                ResolveForeachBinding(foreachStatement.IndexBinding);
                ResolveForeachBinding(foreachStatement.KeyBinding);
                ResolveForeachBinding(foreachStatement.ValueBinding);
                ResolveExpression(foreachStatement.IterableExpression);
                ResolveStatements(foreachStatement.Body);
                break;
            case SwitchStatement switchStatement:
                ResolveExpression(switchStatement.Expression);
                foreach (var switchCase in switchStatement.Cases)
                {
                    ResolveExpression(switchCase.Pattern);
                    ResolveStatements(switchCase.Body);
                }

                ResolveStatements(switchStatement.DefaultBody);
                break;
            case MatchStatement matchStatement:
                ResolveExpression(matchStatement.Expression);
                foreach (var arm in matchStatement.Arms)
                {
                    ResolveStatements(arm.Body);
                }

                break;
        }
    }

    private void ResolveForInitializer(ForInitializerNode initializer)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                ResolveType(declaration, declaration.TypeNode);
                ResolveExpression(declaration.Initializer);
                break;
            case ForExpressionInitializerNode expression:
                ResolveExpression(expression.Expression);
                break;
        }
    }

    private void ResolveForeachBinding(ForeachBinding? binding)
    {
        if (binding is not null)
        {
            ResolveType(binding, binding.TypeNode);
        }
    }

    private void ResolveExpression(ExpressionNode? expression)
    {
        switch (expression)
        {
            case null:
                return;
            case ParenthesizedExpressionNode parenthesized:
                ResolveExpression(parenthesized.Expression);
                break;
            case CastExpressionNode cast:
                ResolveType(cast, cast.TargetTypeNode);
                ResolveExpression(cast.Expression);
                break;
            case UnaryExpressionNode unary:
                ResolveExpression(unary.Operand);
                break;
            case PostfixExpressionNode postfix:
                ResolveExpression(postfix.Operand);
                break;
            case SizeOfExpressionNode sizeOf:
                ResolveSizeOfExpression(sizeOf);
                break;
            case BinaryExpressionNode binary:
                ResolveExpression(binary.Left);
                ResolveExpression(binary.Right);
                break;
            case ConditionalExpressionNode conditional:
                ResolveExpression(conditional.Condition);
                ResolveExpression(conditional.WhenTrue);
                ResolveExpression(conditional.WhenFalse);
                break;
            case ScalarRangeExpressionNode range:
                ResolveExpression(range.Start);
                ResolveExpression(range.End);
                break;
            case InitializerExpressionNode initializer:
                if (initializer.TypeNameNode is not null)
                {
                    ResolveType(initializer, initializer.TypeNameNode);
                }

                foreach (var field in initializer.Fields)
                {
                    ResolveExpression(field.Value);
                }

                foreach (var value in initializer.Values)
                {
                    ResolveExpression(value);
                }

                break;
            case FunctionExpressionNode function:
                if (function.ReturnTypeNode is not null)
                {
                    ResolveType(function, function.ReturnTypeNode);
                }

                foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
                {
                    ResolveType(parameter, parameter.TypeNode);
                }

                ResolveExpression(function.ExpressionBody);
                if (function.BlockBody is not null)
                {
                    ResolveStatements(function.BlockBody);
                }

                break;
            case AssignmentExpressionNode assignment:
                ResolveExpression(assignment.Target);
                ResolveExpression(assignment.Value);
                break;
            case CallExpressionNode call:
                ResolveExpression(call.Callee);
                foreach (var argument in call.Arguments)
                {
                    ResolveExpression(argument);
                }

                break;
            case GenericCallExpressionNode call:
                ResolveExpression(call.Callee);
                ResolveTypeArgumentNodes(call.TypeArgumentNodes);

                foreach (var argument in call.Arguments)
                {
                    ResolveExpression(argument);
                }

                break;
            case MemberExpressionNode member:
                ResolveExpression(member.Target);
                break;
            case IndexExpressionNode index:
                ResolveExpression(index.Target);
                ResolveExpression(index.Index);
                break;
        }
    }

    private void ResolveSizeOfExpression(SizeOfExpressionNode sizeOf)
    {
        if (sizeOf.OperandNode is SizeOfUnresolvedOperandNode unresolved
            && unresolved.ExpressionCandidate is NameExpressionNode name
            && name.Semantic.Symbol is null or { Kind: SymbolKind.Type })
        {
            var typeNode = TypeNode.CreateFromText(unresolved.Location, unresolved.SourceText);
            sizeOf.TypeOperandNode = typeNode;
            sizeOf.ExpressionOperand = null;
            sizeOf.OperandNode = new SizeOfTypeOperandNode(typeNode.Location, typeNode.TypeName, typeNode);
        }

        if (sizeOf.TypeOperandNode is not null)
        {
            ResolveType(sizeOf, sizeOf.TypeOperandNode);
            return;
        }

        ResolveExpression(sizeOf.ExpressionOperand);
    }

    private void ResolveType(SyntaxNode node, TypeNode? typeNode)
    {
        var resolvedType = typeNode is null
            ? ResolveType((string?)null)
            : ResolveType(typeNode);
        node.Semantic.Type = resolvedType;
        if (typeNode is not null)
        {
            typeNode.Semantic.Type = resolvedType;
        }
    }

    private void ResolveTypeArgumentNodes(IReadOnlyList<TypeNode> typeNodes)
    {
        foreach (var typeNode in typeNodes)
        {
            ResolveType(typeNode, typeNode);
        }
    }

    private TypeRef ResolveType(TypeNode typeNode)
    {
        if (_typeSyntaxConverter is null)
        {
            diagnostics.Report(Location.Synthetic("<type-resolution>"), "Type resolution was not initialized.");
            return new TypeRef.Unknown();
        }

        return typeNode.Syntax is null
            ? ResolveType(typeNode.TypeName)
            : _typeSyntaxConverter.Convert(typeNode);
    }

    private TypeRef ResolveType(string? type)
    {
        if (_parser is null)
        {
            diagnostics.Report(Location.Synthetic("<type-resolution>"), "Type resolution was not initialized.");
            return new TypeRef.Unknown();
        }

        return _parser.Parse(type);
    }
}
