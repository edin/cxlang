using System.Text.RegularExpressions;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal sealed class GenericUseCollector(ProgramNode program)
{
    private readonly IReadOnlyList<FunctionNode> _genericFunctions = program.Functions
        .Where(function => function.TypeParameters.Count > 0)
        .ToList();
    private readonly IReadOnlyList<TypeAdapterNode> _typeAdapters = program.TypeAdapters;
    private readonly ExpressionTypeResolver _resolver = new(program);
    private readonly TypeRefParser _typeRefParser = new(program);

    public IReadOnlyList<RawGenericUseAuditEntry> RawGenericUseAuditEntries => [];
    public IEnumerable<GenericFunctionUse> Collect(ProgramNode program)
    {
        foreach (var expression in program.Functions
            .Where(function => function.TypeParameters.Count == 0)
            .SelectMany(function => EnumerateExpressions(function.Body)))
        {
            foreach (var use in CollectResolvedUse(expression))
            {
                yield return use;
            }
        }

        foreach (var function in program.Functions.Where(function => function.TypeParameters.Count == 0))
        {
            foreach (var use in Collect(function))
            {
                yield return use;
            }
        }

        foreach (var use in CollectGlobals(program))
        {
            yield return use;
        }
    }

    public IEnumerable<GenericFunctionUse> Collect(FunctionNode function)
    {
        var selfType = ResolveSelfType(function);
        var selfApiType = ResolveSelfApiType(function);
        var scopeSelfType = selfApiType ?? selfType;
        var variables = function.Parameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => (parameter.Name, Type: GenericTypeStringRewriter.SubstituteSelf(TypeText(parameter.TypeNode), scopeSelfType)))
            .Concat(CollectLocalVariables(function.Body)
                .Select(local => (local.Name, Type: GenericTypeStringRewriter.SubstituteSelf(local.Type, scopeSelfType))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal);

        var knownUses = new HashSet<GenericFunctionUseKey>();
        foreach (var expression in EnumerateExpressions(function.Body))
        {
            foreach (var use in CollectExpressionGenericUses(expression, variables, selfApiType))
            {
                if (TryRemember(use, knownUses))
                {
                    yield return use;
                }
            }
        }

        foreach (var use in FindForeachIteratorGenericUses(function.Body, variables))
        {
            if (TryRemember(use, knownUses))
            {
                yield return use;
            }
        }
    }

    private IEnumerable<GenericFunctionUse> CollectGlobals(ProgramNode program)
    {
        var variables = program.GlobalVariables
            .Select(global => (global.Name, Type: TypeText(global.TypeNode)))
            .Where(global => !string.IsNullOrWhiteSpace(global.Name) && !string.IsNullOrWhiteSpace(global.Type))
            .GroupBy(global => global.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal);

        var knownUses = new HashSet<GenericFunctionUseKey>();
        foreach (var global in program.GlobalVariables.Where(global => global.Initializer is not null))
        {
            foreach (var use in CollectExpressionGenericUses(global.Initializer!, variables))
            {
                if (TryRemember(use, knownUses))
                {
                    yield return use;
                }
            }

        }
    }

    public IEnumerable<GenericFunctionUse> CollectExpressionGenericUses(
        ExpressionNode expression,
        IReadOnlyDictionary<string, string> variables,
        string? selfApiType = null)
    {
        foreach (var use in CollectResolvedUse(expression))
        {
            yield return use;
        }

        foreach (var use in FindGenericFunctionUses(expression, variables, selfApiType))
        {
            yield return use;
        }
    }

    private static IEnumerable<GenericFunctionUse> CollectResolvedUse(ExpressionNode expression)
    {
        if (expression.Semantic.ResolvedCall is { Function.TypeParameters.Count: > 0 } resolved
            && resolved.TypeArguments.Count == resolved.Function.TypeParameters.Count)
        {
            yield return new GenericFunctionUse(resolved.Function, resolved.TypeArguments);
        }
    }

    private static bool TryRemember(GenericFunctionUse use, ISet<GenericFunctionUseKey> knownUses) =>
        knownUses.Add(GenericFunctionUseKey.Create(use.Function, use.TypeArguments));

    private IEnumerable<GenericFunctionUse> FindForeachIteratorGenericUses(
        IEnumerable<StatementNode> statements,
        IReadOnlyDictionary<string, string> variables)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case ForeachStatement foreachStatement:
                    if (foreachStatement.IterableExpression is NameExpressionNode name
                        && variables.TryGetValue(name.SourceText, out var iterableType)
                        && TypeSyntaxFacts.TryParseGenericUse(iterableType, out var ownerName, out var ownerArguments))
                    {
                        foreach (var iteratorFunction in _genericFunctions.Where(function =>
                            OwnerType(function) == ownerName
                            && function.Name == "iterator"
                            && function.TypeParameters.Count == ownerArguments.Count))
                        {
                            yield return new GenericFunctionUse(iteratorFunction, ownerArguments);

                            var substitutions = iteratorFunction.TypeParameters
                                .Zip(ownerArguments)
                                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                            var iteratorType = GenericTypeStringRewriter.Substitute(TypeText(iteratorFunction.ReturnTypeNode), substitutions);
                            if (TypeSyntaxFacts.TryParseGenericUse(iteratorType, out var iteratorOwner, out var iteratorArguments))
                            {
                                foreach (var iteratorMember in _genericFunctions.Where(function =>
                                    OwnerType(function) == iteratorOwner
                                    && function.TypeParameters.Count == iteratorArguments.Count
                                    && (function.Name == "next"
                                        || function.Name == "value"
                                        || function.Name == "key")))
                                {
                                    yield return new GenericFunctionUse(iteratorMember, iteratorArguments);
                                }
                            }
                        }
                    }

                    foreach (var nested in FindForeachIteratorGenericUses(foreachStatement.Body, variables))
                    {
                        yield return nested;
                    }
                    break;
                case IfStatement ifStatement:
                    foreach (var nested in FindForeachIteratorGenericUses(ifStatement.ThenBody, variables))
                    {
                        yield return nested;
                    }
                    if (ifStatement.ElseBranch is ElseBlockStatement nestedElseBlock)
                    {
                        foreach (var nested in FindForeachIteratorGenericUses(nestedElseBlock.Body, variables))
                        {
                            yield return nested;
                        }
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var nested in FindForeachIteratorGenericUses(elseBlock.Body, variables))
                    {
                        yield return nested;
                    }
                    break;
                case WhileStatement whileStatement:
                    foreach (var nested in FindForeachIteratorGenericUses(whileStatement.Body, variables))
                    {
                        yield return nested;
                    }
                    break;
                case ForStatement forStatement:
                    foreach (var nested in FindForeachIteratorGenericUses(forStatement.Body, variables))
                    {
                        yield return nested;
                    }
                    break;
                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var nested in FindForeachIteratorGenericUses(switchCase.Body, variables))
                        {
                            yield return nested;
                        }
                    }
                    foreach (var nested in FindForeachIteratorGenericUses(switchStatement.DefaultBody, variables))
                    {
                        yield return nested;
                    }
                    break;
                case MatchStatement matchStatement:
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var nested in FindForeachIteratorGenericUses(arm.Body, variables))
                        {
                            yield return nested;
                        }
                    }
                    break;
            }
        }
    }

    private IEnumerable<GenericFunctionUse> FindGenericFunctionUses(
        ExpressionNode expression,
        IReadOnlyDictionary<string, string> variables,
        string? selfApiType = null)
    {
        switch (expression)
        {
            case CallExpressionNode call:
                var resolvedInferredUses = FindResolvedGenericFunctionUses(call.Callee, [], call.Arguments, variables, selfApiType).ToList();
                if (resolvedInferredUses.Count > 0)
                {
                    foreach (var use in resolvedInferredUses)
                    {
                        yield return use;
                    }

                    yield break;
                }

                foreach (var use in FindInferredGenericFunctionUses(call, variables, selfApiType))
                {
                    yield return use;
                }
                break;
            case GenericCallExpressionNode call:
                var resolvedExplicitUses = FindResolvedGenericFunctionUses(call.Callee, TypeArguments(call.TypeArgumentNodes), call.Arguments, variables, selfApiType).ToList();
                if (resolvedExplicitUses.Count > 0)
                {
                    foreach (var use in resolvedExplicitUses)
                    {
                        yield return use;
                    }

                    yield break;
                }

                foreach (var use in FindExplicitGenericFunctionUses(call, variables, selfApiType))
                {
                    yield return use;
                }
                break;
        }
    }

    private IEnumerable<GenericFunctionUse> FindResolvedGenericFunctionUses(
        ExpressionNode callee,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables,
        string? selfApiType)
    {
        IReadOnlyDictionary<string, string> resolverVariables = variables;
        if (selfApiType is not null && variables.ContainsKey("self"))
        {
            var mappedVariables = variables
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            mappedVariables["self"] = selfApiType;
            resolverVariables = mappedVariables;
        }

        var resolved = new CallResolver(program, _resolver.Resolve)
            .Resolve(callee, typeArguments, arguments, resolverVariables);
        if (resolved?.Function is { TypeParameters.Count: > 0 } function
            && resolved.TypeArguments is { } resolvedTypeArguments
            && resolvedTypeArguments.Count == function.TypeParameters.Count)
        {
            yield return new GenericFunctionUse(function, resolvedTypeArguments);
        }
    }

    private IEnumerable<GenericFunctionUse> FindInferredGenericFunctionUses(
        CallExpressionNode call,
        IReadOnlyDictionary<string, string> variables,
        string? selfApiType = null)
    {
        if (call.Callee is NameExpressionNode name)
        {
            foreach (var function in _genericFunctions.Where(function =>
                OwnerType(function) is null
                && function.Name == name.SourceText))
            {
                if (_resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false) is { } arguments)
                {
                    yield return new GenericFunctionUse(function, arguments);
                }
            }

            yield break;
        }

        if (call.Callee is not MemberExpressionNode member)
        {
            yield break;
        }

        var targetName = GetQualifiedName(member.Target);
        if (targetName is null)
        {
            yield break;
        }

        if (!variables.TryGetValue(targetName, out var targetType))
        {
            foreach (var function in _genericFunctions.Where(function =>
                function.IsStatic
                && OwnerType(function) == targetName
                && function.Name == member.MemberName))
            {
                if (_resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false) is { } arguments)
                {
                    yield return new GenericFunctionUse(function, arguments);
                }
            }

            yield break;
        }

        var normalizedType = TypeSyntaxFacts.RemovePointer(targetType);
        var receiverArguments = TypeSyntaxFacts.TryParseGenericUse(normalizedType, out _, out var parsedReceiverArguments)
            ? parsedReceiverArguments
            : [];
        var ownerType = TypeSyntaxFacts.GetGenericBaseName(normalizedType) ?? normalizedType;

        foreach (var function in _genericFunctions.Where(function =>
            !function.IsStatic
            && OwnerType(function) == ownerType
            && function.Name == member.MemberName))
        {
            var arguments = receiverArguments.Count == function.TypeParameters.Count
                ? receiverArguments
                : _resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: true, receiverArguments);
            if (arguments is not null)
            {
                yield return new GenericFunctionUse(function, arguments);
            }
        }
    }

    private IEnumerable<GenericFunctionUse> FindExplicitGenericFunctionUses(
        GenericCallExpressionNode call,
        IReadOnlyDictionary<string, string> variables,
        string? selfApiType = null)
    {
        if (call.Callee is NameExpressionNode name)
        {
            var matchedFunction = _genericFunctions.FirstOrDefault(candidate =>
                OwnerType(candidate) is null
                && candidate.Name == name.SourceText
                && candidate.TypeParameters.Count == TypeArguments(call.TypeArgumentNodes).Count);
            if (matchedFunction is not null)
            {
                yield return new GenericFunctionUse(matchedFunction, TypeArguments(call.TypeArgumentNodes));
            }

            yield break;
        }

        if (call.Callee is not MemberExpressionNode member)
        {
            yield break;
        }

        var targetName = GetQualifiedName(member.Target);
        if (targetName is null)
        {
            yield break;
        }

        if (!variables.TryGetValue(targetName, out var targetType))
        {
            var staticFunction = _genericFunctions.FirstOrDefault(function =>
                function.IsStatic
                && OwnerType(function) == targetName
                && function.Name == member.MemberName
                && function.TypeParameters.Count == TypeArguments(call.TypeArgumentNodes).Count);
            if (staticFunction is not null)
            {
                yield return new GenericFunctionUse(staticFunction, TypeArguments(call.TypeArgumentNodes));
            }

            yield break;
        }

        var ownerType = TypeSyntaxFacts.GetGenericBaseName(TypeSyntaxFacts.RemovePointer(targetType));
        var matchedMethod = _genericFunctions.FirstOrDefault(candidate =>
            !candidate.IsStatic
            && OwnerType(candidate) == ownerType
            && candidate.Name == member.MemberName
            && candidate.TypeParameters.Count == TypeArguments(call.TypeArgumentNodes).Count);
        if (matchedMethod is not null)
        {
            yield return new GenericFunctionUse(matchedMethod, TypeArguments(call.TypeArgumentNodes));
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateExpressions(ProgramNode program)
    {
        foreach (var global in program.GlobalVariables.Where(global => global.Initializer is not null))
        {
            foreach (var expression in EnumerateExpressions(global.Initializer!))
            {
                yield return expression;
            }
        }

        foreach (var function in program.Functions)
        {
            foreach (var expression in EnumerateExpressions(function.Body))
            {
                yield return expression;
            }
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateExpressions(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement { Initializer: not null } let:
                    foreach (var expression in EnumerateExpressions(let.Initializer)) yield return expression;
                    break;
                case ReturnStatement { Expression: not null } ret:
                    foreach (var expression in EnumerateExpressions(ret.Expression)) yield return expression;
                    break;
                case CStatement c:
                    foreach (var expression in EnumerateExpressions(c.Expression)) yield return expression;
                    break;
                case IfStatement ifStatement:
                    foreach (var expression in EnumerateExpressions(ifStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(ifStatement.ThenBody)) yield return expression;
                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var expression in EnumerateExpressions([ifStatement.ElseBranch])) yield return expression;
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var expression in EnumerateExpressions(elseBlock.Body)) yield return expression;
                    break;
                case WhileStatement whileStatement:
                    foreach (var expression in EnumerateExpressions(whileStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(whileStatement.Body)) yield return expression;
                    break;
                case ForStatement forStatement:
                    foreach (var expression in EnumerateForInitializerExpressions(forStatement.Initializer)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Increment)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Body)) yield return expression;
                    break;
                case ForeachStatement foreachStatement:
                    foreach (var expression in EnumerateExpressions(foreachStatement.IterableExpression)) yield return expression;
                    foreach (var expression in EnumerateExpressions(foreachStatement.Body)) yield return expression;
                    break;
                case SwitchStatement switchStatement:
                    foreach (var expression in EnumerateExpressions(switchStatement.Expression)) yield return expression;
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var expression in EnumerateExpressions(switchCase.Pattern)) yield return expression;
                        foreach (var expression in EnumerateExpressions(switchCase.Body)) yield return expression;
                    }
                    foreach (var expression in EnumerateExpressions(switchStatement.DefaultBody)) yield return expression;
                    break;
                case MatchStatement matchStatement:
                    foreach (var expression in EnumerateExpressions(matchStatement.Expression)) yield return expression;
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var expression in EnumerateExpressions(arm.Body)) yield return expression;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateForInitializerExpressions(ForInitializerNode initializer) => initializer switch
    {
        ForDeclarationInitializerNode { Initializer: not null } declaration => EnumerateExpressions(declaration.Initializer),
        ForExpressionInitializerNode expression => EnumerateExpressions(expression.Expression),
        _ => [],
    };

    private static IEnumerable<ExpressionNode> EnumerateExpressions(ExpressionNode expression)
    {
        yield return expression;
        switch (expression)
        {
            case ParenthesizedExpressionNode parenthesized:
                foreach (var child in EnumerateExpressions(parenthesized.Expression)) yield return child;
                break;
            case CastExpressionNode cast:
                foreach (var child in EnumerateExpressions(cast.Expression)) yield return child;
                break;
            case UnaryExpressionNode unary:
                foreach (var child in EnumerateExpressions(unary.Operand)) yield return child;
                break;
            case PostfixExpressionNode postfix:
                foreach (var child in EnumerateExpressions(postfix.Operand)) yield return child;
                break;
            case SizeOfExpressionNode { ExpressionOperand: not null } sizeOf:
                foreach (var child in EnumerateExpressions(sizeOf.ExpressionOperand)) yield return child;
                break;
            case BinaryExpressionNode binary:
                foreach (var child in EnumerateExpressions(binary.Left)) yield return child;
                foreach (var child in EnumerateExpressions(binary.Right)) yield return child;
                break;
            case ScalarRangeExpressionNode range:
                foreach (var child in EnumerateExpressions(range.Start)) yield return child;
                foreach (var child in EnumerateExpressions(range.End)) yield return child;
                break;
            case ConditionalExpressionNode conditional:
                foreach (var child in EnumerateExpressions(conditional.Condition)) yield return child;
                foreach (var child in EnumerateExpressions(conditional.WhenTrue)) yield return child;
                foreach (var child in EnumerateExpressions(conditional.WhenFalse)) yield return child;
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    foreach (var child in EnumerateExpressions(field.Value)) yield return child;
                }
                foreach (var value in initializer.Values)
                {
                    foreach (var child in EnumerateExpressions(value)) yield return child;
                }
                break;
            case FunctionExpressionNode function:
                if (function.ExpressionBody is not null)
                {
                    foreach (var child in EnumerateExpressions(function.ExpressionBody)) yield return child;
                }
                if (function.BlockBody is not null)
                {
                    foreach (var child in EnumerateExpressions(function.BlockBody)) yield return child;
                }
                break;
            case AssignmentExpressionNode assignment:
                foreach (var child in EnumerateExpressions(assignment.Target)) yield return child;
                foreach (var child in EnumerateExpressions(assignment.Value)) yield return child;
                break;
            case CallExpressionNode call:
                foreach (var child in EnumerateExpressions(call.Callee)) yield return child;
                foreach (var argument in call.Arguments)
                {
                    foreach (var child in EnumerateExpressions(argument)) yield return child;
                }
                break;
            case GenericCallExpressionNode call:
                foreach (var child in EnumerateExpressions(call.Callee)) yield return child;
                foreach (var argument in call.Arguments)
                {
                    foreach (var child in EnumerateExpressions(argument)) yield return child;
                }
                break;
            case MemberExpressionNode member:
                foreach (var child in EnumerateExpressions(member.Target)) yield return child;
                break;
            case IndexExpressionNode index:
                foreach (var child in EnumerateExpressions(index.Target)) yield return child;
                foreach (var child in EnumerateExpressions(index.Index)) yield return child;
                break;
        }
    }

    private static IEnumerable<string> EnumerateExpressionTexts(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return let.TypeNode.ToTypeName();
                    if (let.Initializer is not null)
                    {
                        yield return let.Initializer.SourceText;
                    }
                    break;
                case ReturnStatement { Expression: not null } ret:
                    yield return ret.Expression.SourceText;
                    break;
                case CStatement c:
                    yield return c.Expression.SourceText;
                    break;
                case IfStatement ifStatement:
                    yield return ifStatement.Condition.SourceText;
                    foreach (var nested in EnumerateExpressionTexts(ifStatement.ThenBody))
                    {
                        yield return nested;
                    }
                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var nested in EnumerateExpressionTexts([ifStatement.ElseBranch]))
                        {
                            yield return nested;
                        }
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var nested in EnumerateExpressionTexts(elseBlock.Body))
                    {
                        yield return nested;
                    }
                    break;
                case WhileStatement whileStatement:
                    yield return whileStatement.Condition.SourceText;
                    foreach (var nested in EnumerateExpressionTexts(whileStatement.Body))
                    {
                        yield return nested;
                    }
                    break;
                case ForStatement forStatement:
                    foreach (var expression in EnumerateForInitializerTexts(forStatement.Initializer))
                    {
                        yield return expression;
                    }
                    yield return forStatement.Condition.SourceText;
                    yield return forStatement.Increment.SourceText;
                    foreach (var nested in EnumerateExpressionTexts(forStatement.Body))
                    {
                        yield return nested;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    yield return foreachStatement.IterableExpression.SourceText;
                    foreach (var nested in EnumerateExpressionTexts(foreachStatement.Body))
                    {
                        yield return nested;
                    }
                    break;
                case SwitchStatement switchStatement:
                    yield return switchStatement.Expression.SourceText;
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        yield return switchCase.Pattern.SourceText;
                        foreach (var nested in EnumerateExpressionTexts(switchCase.Body))
                        {
                            yield return nested;
                        }
                    }
                    foreach (var nested in EnumerateExpressionTexts(switchStatement.DefaultBody))
                    {
                        yield return nested;
                    }
                    break;
                case MatchStatement matchStatement:
                    yield return matchStatement.Expression.SourceText;
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var nested in EnumerateExpressionTexts(arm.Body))
                        {
                            yield return nested;
                        }
                    }
                    break;
            }
        }
    }

    private static IEnumerable<string> EnumerateForInitializerTexts(ForInitializerNode initializer) => initializer switch
    {
        ForDeclarationInitializerNode declaration when declaration.Initializer is not null =>
            [declaration.Initializer.SourceText],
        ForExpressionInitializerNode expression => [expression.Expression.SourceText],
        _ => [],
    };

    private IEnumerable<(string Name, string Type)> CollectLocalVariables(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return (let.Name, TypeText(let.TypeNode));
                    break;
                case IfStatement ifStatement:
                    foreach (var variable in CollectLocalVariables(ifStatement.ThenBody))
                    {
                        yield return variable;
                    }
                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var variable in CollectLocalVariables([ifStatement.ElseBranch]))
                        {
                            yield return variable;
                        }
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var variable in CollectLocalVariables(elseBlock.Body))
                    {
                        yield return variable;
                    }
                    break;
                case WhileStatement whileStatement:
                    foreach (var variable in CollectLocalVariables(whileStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForStatement forStatement:
                    if (forStatement.Initializer is ForDeclarationInitializerNode declaration)
                    {
                        yield return (declaration.Name, TypeText(declaration.TypeNode));
                    }
                    foreach (var variable in CollectLocalVariables(forStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    if (foreachStatement.IndexBinding is not null)
                    {
                        yield return (foreachStatement.IndexBinding.Name, TypeText(foreachStatement.IndexBinding.TypeNode));
                    }
                    if (foreachStatement.KeyBinding is not null)
                    {
                        yield return (foreachStatement.KeyBinding.Name, TypeText(foreachStatement.KeyBinding.TypeNode));
                    }
                    yield return (foreachStatement.ValueBinding.Name, TypeText(foreachStatement.ValueBinding.TypeNode));
                    foreach (var variable in CollectLocalVariables(foreachStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var variable in CollectLocalVariables(switchCase.Body))
                        {
                            yield return variable;
                        }
                    }
                    foreach (var variable in CollectLocalVariables(switchStatement.DefaultBody))
                    {
                        yield return variable;
                    }
                    break;
                case MatchStatement matchStatement:
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var variable in CollectLocalVariables(arm.Body))
                        {
                            yield return variable;
                        }
                    }
                    break;
            }
        }
    }

    private string? ResolveSelfType(FunctionNode function)
    {
        if (OwnerType(function) is null)
        {
            return null;
        }

        var functionTypeArguments = TypeArguments(function.TypeArgumentNodes);
        if (functionTypeArguments.Count > 0)
        {
            return ResolveAdapterStorageType($"{OwnerType(function)}<{string.Join(",", functionTypeArguments)}>");
        }

        var selfParameter = function.Parameters.FirstOrDefault(parameter => parameter.Name == "self");
        if (selfParameter is not null && !Regex.IsMatch(TypeText(selfParameter.TypeNode), @"\bSelf\b"))
        {
            return TypeSyntaxFacts.RemovePointer(TypeText(selfParameter.TypeNode));
        }

        return ResolveAdapterStorageType(OwnerType(function)!);
    }

    private string? ResolveSelfApiType(FunctionNode function)
    {
        if (OwnerType(function) is null)
        {
            return null;
        }

        var functionTypeArguments = TypeArguments(function.TypeArgumentNodes);
        return functionTypeArguments.Count > 0
            ? $"{OwnerType(function)}<{string.Join(",", functionTypeArguments)}>"
            : OwnerType(function);
    }

    private string ResolveAdapterStorageType(string type)
    {
        var prefix = "";
        if (type.StartsWith("const ", StringComparison.Ordinal))
        {
            prefix = "const ";
            type = type["const ".Length..].TrimStart();
        }

        var pointerSuffix = "";
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var adapterName = TypeSyntaxFacts.GetGenericBaseName(type) ?? type;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var adapter = _typeAdapters.LastOrDefault(adapter => adapter.Name == adapterName);
            if (adapter is null || !seen.Add(adapter.Name))
            {
                return prefix + type + pointerSuffix;
            }

            var receiverArguments = TypeSyntaxFacts.TryParseGenericUse(type, out _, out var parsedArguments)
                ? parsedArguments
                : [];
            type = SubstituteAdapterBaseType(adapter, receiverArguments);
            adapterName = TypeSyntaxFacts.GetGenericBaseName(type) ?? type;
        }
    }

    private string SubstituteAdapterBaseType(TypeAdapterNode adapter, IReadOnlyList<string> receiverArguments)
    {
        if (adapter.TypeParameters.Count == 0 || adapter.TypeParameters.Count != receiverArguments.Count)
        {
            return TypeText(adapter.BaseTypeNode);
        }

        var substitutions = adapter.TypeParameters
            .Zip(receiverArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        return GenericTypeStringRewriter.Substitute(TypeText(adapter.BaseTypeNode), substitutions);
    }

    private string? OwnerType(FunctionNode function)
    {
        var type = TypeText(function.OwnerTypeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }

    private IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode>? typeArgumentNodes) =>
        (typeArgumentNodes ?? []).Select(TypeText).ToList();

    private string TypeText(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        var type = typeNode.ToTypeRef(_typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

}

internal sealed record GenericFunctionUse(FunctionNode Function, IReadOnlyList<string> TypeArguments);

internal readonly record struct GenericFunctionUseKey(string FunctionName, string TypeArguments)
{
    public static GenericFunctionUseKey Create(FunctionNode function, IReadOnlyList<string> typeArguments) =>
        new(
            function.OwnerTypeNode is null ? function.Name : $"{function.OwnerTypeNode.ToTypeName()}.{function.Name}",
            string.Join(",", typeArguments));
}
