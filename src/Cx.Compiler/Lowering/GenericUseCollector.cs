using System.Text.RegularExpressions;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal sealed class GenericUseCollector(ProgramNode program)
{
    private readonly IReadOnlyList<FunctionNode> _genericFunctions = program.Functions
        .Where(function => function.TypeParameters.Count > 0)
        .ToList();
    private readonly IReadOnlyList<TypeAdapterNode> _typeAdapters = program.TypeAdapters;
    private readonly ExpressionTypeResolver _resolver = new(program);

    public IEnumerable<GenericFunctionUse> Collect(ProgramNode program)
    {
        foreach (var expression in EnumerateExpressions(program))
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
        foreach (var expression in EnumerateExpressions(function.Body))
        {
            foreach (var use in CollectResolvedUse(expression))
            {
                yield return use;
            }
        }

        var selfType = ResolveSelfType(function);
        var selfApiType = ResolveSelfApiType(function);
        var variables = function.Parameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => (parameter.Name, Type: SubstituteSelfType(parameter.Type, selfType)))
            .Concat(CollectLocalVariables(function.Body)
                .Select(local => (local.Name, Type: SubstituteSelfType(local.Type, selfType))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal);

        foreach (var expression in EnumerateExpressionTexts(function.Body))
        {
            foreach (var use in FindGenericFunctionUses(expression, variables))
            {
                yield return use;
            }
        }

        foreach (var expression in EnumerateExpressions(function.Body))
        {
            foreach (var use in FindGenericFunctionUses(expression, variables, selfApiType))
            {
                yield return use;
            }
        }

        foreach (var use in FindForeachIteratorGenericUses(function.Body, variables))
        {
            yield return use;
        }
    }

    private IEnumerable<GenericFunctionUse> CollectGlobals(ProgramNode program)
    {
        var variables = program.GlobalVariables
            .Where(global => !string.IsNullOrWhiteSpace(global.Name) && !string.IsNullOrWhiteSpace(global.Type))
            .GroupBy(global => global.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal);

        foreach (var global in program.GlobalVariables.Where(global => global.Initializer is not null))
        {
            foreach (var use in FindGenericFunctionUses(global.Initializer!.SourceText, variables))
            {
                yield return use;
            }

            foreach (var use in FindGenericFunctionUses(global.Initializer!, variables))
            {
                yield return use;
            }
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
                        && TryParseGenericUse(iterableType, out var ownerName, out var ownerArguments))
                    {
                        foreach (var iteratorFunction in _genericFunctions.Where(function =>
                            function.OwnerType == ownerName
                            && function.Name == "iterator"
                            && function.TypeParameters.Count == ownerArguments.Count))
                        {
                            yield return new GenericFunctionUse(iteratorFunction, ownerArguments);

                            var substitutions = iteratorFunction.TypeParameters
                                .Zip(ownerArguments)
                                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                            var iteratorType = SubstituteGenericType(iteratorFunction.ReturnType, substitutions);
                            if (TryParseGenericUse(iteratorType, out var iteratorOwner, out var iteratorArguments))
                            {
                                foreach (var iteratorMember in _genericFunctions.Where(function =>
                                    function.OwnerType == iteratorOwner
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
        string expression,
        IReadOnlyDictionary<string, string> variables)
    {
        foreach (var function in _genericFunctions)
        {
            var staticCallee = function.OwnerType is null
                ? function.Name
                : $"{function.OwnerType}.{function.Name}";
            foreach (var arguments in FindExplicitTypeArgumentCalls(expression, staticCallee))
            {
                if (arguments.Count == function.TypeParameters.Count)
                {
                    yield return new GenericFunctionUse(function, arguments);
                }
            }
        }

        foreach (var (variable, variableType) in variables)
        {
            var owner = GetGenericBaseName(variableType);
            if (owner is null)
            {
                continue;
            }

            foreach (var function in _genericFunctions.Where(function => function.OwnerType == owner && !function.IsStatic))
            {
                var inferredCallPattern = $@"\b{Regex.Escape(variable)}\s*\.\s*{Regex.Escape(function.Name)}\s*\(";
                if (function.TypeParameters.Count == (TryParseGenericUse(variableType, out _, out var receiverArguments) ? receiverArguments.Count : 0)
                    && Regex.IsMatch(expression, inferredCallPattern))
                {
                    yield return new GenericFunctionUse(function, receiverArguments);
                }

                foreach (var arguments in FindExplicitTypeArgumentCalls(expression, $"{variable}.{function.Name}"))
                {
                    if (arguments.Count == function.TypeParameters.Count)
                    {
                        yield return new GenericFunctionUse(function, arguments);
                    }
                }
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
                foreach (var use in FindInferredGenericFunctionUses(call, variables, selfApiType))
                {
                    yield return use;
                }
                break;
            case GenericCallExpressionNode call:
                foreach (var use in FindExplicitGenericFunctionUses(call, variables, selfApiType))
                {
                    yield return use;
                }
                break;
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
                function.OwnerType is null
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
            if (FindAdapterExpose(targetName, member.MemberName) is { Expose.IsStatic: true } staticExpose)
            {
                var resolvedExpose = ResolveAdapterExpose(staticExpose.Adapter, staticExpose.Expose, []);
                foreach (var function in _genericFunctions.Where(function =>
                    function.IsStatic
                    && function.OwnerType == resolvedExpose.BaseOwner
                    && function.Name == resolvedExpose.SourceName))
                {
                    var arguments = resolvedExpose.TypeArguments.Count == function.TypeParameters.Count
                        ? resolvedExpose.TypeArguments
                        : _resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false, resolvedExpose.TypeArguments);
                    if (arguments is not null)
                    {
                        yield return new GenericFunctionUse(function, arguments);
                    }
                }
            }

            foreach (var function in _genericFunctions.Where(function =>
                function.IsStatic
                && function.OwnerType == targetName
                && function.Name == member.MemberName))
            {
                if (_resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false) is { } arguments)
                {
                    yield return new GenericFunctionUse(function, arguments);
                }
            }

            yield break;
        }

        var normalizedType = RemovePointer(targetType);
        var receiverArguments = TryParseGenericUse(normalizedType, out _, out var parsedReceiverArguments)
            ? parsedReceiverArguments
            : [];
        var ownerType = GetGenericBaseName(normalizedType) ?? normalizedType;
        var apiType = targetName == "self" && selfApiType is not null
            ? selfApiType
            : normalizedType;
        var apiReceiverArguments = TryParseGenericUse(apiType, out _, out var parsedApiReceiverArguments)
            ? parsedApiReceiverArguments
            : [];
        var apiOwnerType = GetGenericBaseName(apiType) ?? apiType;
        if (FindAdapterExpose(apiOwnerType, member.MemberName) is { } apiExpose)
        {
            var resolvedExpose = ResolveAdapterExpose(apiExpose.Adapter, apiExpose.Expose, apiReceiverArguments);
            foreach (var function in _genericFunctions.Where(function =>
                !function.IsStatic
                && function.OwnerType == resolvedExpose.BaseOwner
                && function.Name == resolvedExpose.SourceName))
            {
                var arguments = resolvedExpose.TypeArguments.Count == function.TypeParameters.Count
                    ? resolvedExpose.TypeArguments
                    : _resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: true, resolvedExpose.TypeArguments);
                if (arguments is not null)
                {
                    yield return new GenericFunctionUse(function, arguments);
                }
            }
        }

        if (FindAdapterExpose(ownerType, member.MemberName) is { } expose)
        {
            var resolvedExpose = ResolveAdapterExpose(expose.Adapter, expose.Expose, receiverArguments);
            foreach (var function in _genericFunctions.Where(function =>
                !function.IsStatic
                && function.OwnerType == resolvedExpose.BaseOwner
                && function.Name == resolvedExpose.SourceName))
            {
                var arguments = resolvedExpose.TypeArguments.Count == function.TypeParameters.Count
                    ? resolvedExpose.TypeArguments
                    : _resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: true, resolvedExpose.TypeArguments);
                if (arguments is not null)
                {
                    yield return new GenericFunctionUse(function, arguments);
                }
            }
        }

        foreach (var function in _genericFunctions.Where(function =>
            !function.IsStatic
            && function.OwnerType == ownerType
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
                candidate.OwnerType is null
                && candidate.Name == name.SourceText
                && candidate.TypeParameters.Count == call.TypeArguments.Count);
            if (matchedFunction is not null)
            {
                yield return new GenericFunctionUse(matchedFunction, call.TypeArguments);
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
            if (FindAdapterExpose(targetName, member.MemberName) is { Expose.IsStatic: true } expose)
            {
                var resolvedExpose = ResolveAdapterExpose(expose.Adapter, expose.Expose, call.TypeArguments);
                var exposedStatic = _genericFunctions.FirstOrDefault(function =>
                    function.IsStatic
                    && function.OwnerType == resolvedExpose.BaseOwner
                    && function.Name == resolvedExpose.SourceName
                    && function.TypeParameters.Count == resolvedExpose.TypeArguments.Count);
                if (exposedStatic is not null)
                {
                    yield return new GenericFunctionUse(exposedStatic, resolvedExpose.TypeArguments);
                }
            }

            var staticFunction = _genericFunctions.FirstOrDefault(function =>
                function.IsStatic
                && function.OwnerType == targetName
                && function.Name == member.MemberName
                && function.TypeParameters.Count == call.TypeArguments.Count);
            if (staticFunction is not null)
            {
                yield return new GenericFunctionUse(staticFunction, call.TypeArguments);
            }

            yield break;
        }

        var ownerType = GetGenericBaseName(RemovePointer(targetType));
        var matchedMethod = _genericFunctions.FirstOrDefault(candidate =>
            !candidate.IsStatic
            && candidate.OwnerType == ownerType
            && candidate.Name == member.MemberName
            && candidate.TypeParameters.Count == call.TypeArguments.Count);
        if (matchedMethod is not null)
        {
            yield return new GenericFunctionUse(matchedMethod, call.TypeArguments);
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindExplicitTypeArgumentCalls(string expression, string callee)
    {
        var uses = new List<IReadOnlyList<string>>();
        var pattern = Regex.Escape(callee).Replace("\\.", @"\s*\.\s*") + @"\s*<";

        foreach (Match match in Regex.Matches(expression, pattern))
        {
            var openIndex = expression.IndexOf('<', match.Index + match.Length - 1);
            if (openIndex < 0)
            {
                continue;
            }

            var closeIndex = FindMatchingGenericClose(expression, openIndex);
            if (closeIndex < 0)
            {
                continue;
            }

            var after = expression[(closeIndex + 1)..];
            if (!Regex.IsMatch(after, @"^\s*\("))
            {
                continue;
            }

            uses.Add(SplitGenericArguments(expression[(openIndex + 1)..closeIndex]));
        }

        return uses;
    }

    private AdapterExposeMatch? FindAdapterExpose(string? adapterName, string exposedName)
    {
        if (adapterName is null)
        {
            return null;
        }

        foreach (var adapter in _typeAdapters.Where(adapter => adapter.Name == adapterName))
        {
            var expose = adapter.ExposedMethods.FirstOrDefault(expose => expose.ExposedName == exposedName);
            if (expose is not null)
            {
                return new AdapterExposeMatch(adapter, expose);
            }
        }

        return null;
    }

    private ResolvedAdapterExpose ResolveAdapterExpose(
        TypeAdapterNode adapter,
        ExposeMethodNode expose,
        IReadOnlyList<string> receiverArguments)
    {
        var currentAdapter = adapter;
        var currentExpose = expose;
        var currentArguments = receiverArguments;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var baseType = SubstituteAdapterBaseType(currentAdapter, currentArguments);
            var baseOwner = GetGenericBaseName(baseType) ?? baseType;
            var baseArguments = TryParseGenericUse(baseType, out _, out var parsedBaseArguments)
                ? parsedBaseArguments
                : [];
            var key = $"{currentAdapter.Name}.{currentExpose.ExposedName}";
            if (!seen.Add(key))
            {
                return new ResolvedAdapterExpose(baseType, baseOwner, currentExpose.SourceName, baseArguments);
            }

            var nextAdapter = _typeAdapters.FirstOrDefault(adapter => adapter.Name == baseOwner);
            var nextExpose = nextAdapter?.ExposedMethods.FirstOrDefault(expose =>
                expose.IsStatic == currentExpose.IsStatic
                && expose.ExposedName == currentExpose.SourceName);
            if (nextAdapter is null || nextExpose is null)
            {
                return new ResolvedAdapterExpose(baseType, baseOwner, currentExpose.SourceName, baseArguments);
            }

            currentAdapter = nextAdapter;
            currentExpose = nextExpose;
            currentArguments = baseArguments;
        }
    }

    private static string SubstituteAdapterBaseType(TypeAdapterNode adapter, IReadOnlyList<string> receiverArguments)
    {
        if (adapter.TypeParameters.Count == 0 || adapter.TypeParameters.Count != receiverArguments.Count)
        {
            return adapter.BaseType;
        }

        var substitutions = adapter.TypeParameters
            .Zip(receiverArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        return SubstituteGenericType(adapter.BaseType, substitutions);
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
                case ReturnStatement ret:
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
                    yield return let.Type;
                    if (let.Initializer is not null)
                    {
                        yield return let.Initializer.SourceText;
                    }
                    break;
                case ReturnStatement ret:
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

    private static IEnumerable<(string Name, string Type)> CollectLocalVariables(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return (let.Name, let.Type);
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
                        yield return (declaration.Name, declaration.Type);
                    }
                    foreach (var variable in CollectLocalVariables(forStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    if (foreachStatement.IndexBinding is not null)
                    {
                        yield return (foreachStatement.IndexBinding.Name, foreachStatement.IndexBinding.Type);
                    }
                    if (foreachStatement.KeyBinding is not null)
                    {
                        yield return (foreachStatement.KeyBinding.Name, foreachStatement.KeyBinding.Type);
                    }
                    yield return (foreachStatement.ValueBinding.Name, foreachStatement.ValueBinding.Type);
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
        if (function.OwnerType is null)
        {
            return null;
        }

        if (function.TypeArguments.Count > 0)
        {
            return ResolveAdapterStorageType($"{function.OwnerType}<{string.Join(",", function.TypeArguments)}>");
        }

        var selfParameter = function.Parameters.FirstOrDefault(parameter => parameter.Name == "self");
        if (selfParameter is not null && !Regex.IsMatch(selfParameter.Type, @"\bSelf\b"))
        {
            return RemovePointer(selfParameter.Type);
        }

        return ResolveAdapterStorageType(function.OwnerType);
    }

    private static string? ResolveSelfApiType(FunctionNode function)
    {
        if (function.OwnerType is null)
        {
            return null;
        }

        return function.TypeArguments.Count > 0
            ? $"{function.OwnerType}<{string.Join(",", function.TypeArguments)}>"
            : function.OwnerType;
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

        var adapterName = GetGenericBaseName(type) ?? type;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var adapter = _typeAdapters.LastOrDefault(adapter => adapter.Name == adapterName);
            if (adapter is null || !seen.Add(adapter.Name))
            {
                return prefix + type + pointerSuffix;
            }

            var receiverArguments = TryParseGenericUse(type, out _, out var parsedArguments)
                ? parsedArguments
                : [];
            type = SubstituteAdapterBaseType(adapter, receiverArguments);
            adapterName = GetGenericBaseName(type) ?? type;
        }
    }

    private static string SubstituteSelfType(string type, string? selfType) =>
        string.IsNullOrWhiteSpace(selfType)
            ? type
            : Regex.Replace(type, @"\bSelf\b", selfType);

    private static string SubstituteGenericType(string type, IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var (parameter, argument) in substitutions)
        {
            type = Regex.Replace(type, $@"\b{Regex.Escape(parameter)}\b", argument);
        }

        return type;
    }

    private static string RemovePointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private static string? GetGenericBaseName(string type)
    {
        type = type.TrimEnd('*').TrimEnd();
        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        return genericStart < 0 ? null : type[..genericStart].Trim();
    }

    private static bool TryParseGenericUse(string type, out string name, out IReadOnlyList<string> arguments)
    {
        name = string.Empty;
        arguments = [];
        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        var genericEnd = type.LastIndexOf('>');
        if (genericStart <= 0 || genericEnd < genericStart)
        {
            return false;
        }

        name = type[..genericStart].Trim();
        arguments = SplitGenericArguments(type[(genericStart + 1)..genericEnd]);
        return true;
    }

    private static int FindMatchingGenericClose(string type, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < type.Length; i++)
        {
            if (type[i] == '<')
            {
                depth++;
            }
            else if (type[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitGenericArguments(string argumentsText)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return [];
        }

        var arguments = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var i = 0; i < argumentsText.Length; i++)
        {
            switch (argumentsText[i])
            {
                case '<': angleDepth++; break;
                case '>': angleDepth--; break;
                case '(': parenDepth++; break;
                case ')': parenDepth--; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
            }

            if (argumentsText[i] != ',' || angleDepth != 0 || parenDepth != 0 || bracketDepth != 0)
            {
                continue;
            }

            arguments.Add(argumentsText[start..i].Trim());
            start = i + 1;
        }

        arguments.Add(argumentsText[start..].Trim());
        return arguments;
    }

    private sealed record AdapterExposeMatch(TypeAdapterNode Adapter, ExposeMethodNode Expose);

    private sealed record ResolvedAdapterExpose(
        string BaseType,
        string BaseOwner,
        string SourceName,
        IReadOnlyList<string> TypeArguments);
}

internal sealed record GenericFunctionUse(FunctionNode Function, IReadOnlyList<string> TypeArguments);
