using System.Text.RegularExpressions;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

public sealed class SemanticAnalyzer(
    DiagnosticBag diagnostics,
    IReadOnlyList<ProgramNode>? availablePrograms = null)
{
    private RequirementMatcher? _requirementMatcher;
    private TypeSystem? _typeSystem;
    private ExpressionTypeResolver? _expressionTypeResolver;
    private TypeCompatibility? _typeCompatibility;
    private TypeRefParser? _typeRefParser;
    private TypeUsageAnalyzer? _typeUsageAnalyzer;
    private AssignmentSemanticAnalyzer? _assignmentAnalyzer;
    private ReturnSemanticAnalyzer? _returnAnalyzer;
    private MatchSemanticAnalyzer? _matchAnalyzer;
    private ForeachSemanticAnalyzer? _foreachAnalyzer;
    private ProgramNode? _program;
    private IReadOnlyList<string> _currentTypeParameters = [];
    private IReadOnlyList<GenericConstraintNode> _currentGenericConstraints = [];

    public void Analyze(ProgramNode program)
    {
        _program = program;
        _requirementMatcher = new RequirementMatcher(program);
        _typeSystem = new TypeSystem(program);
        _expressionTypeResolver = new ExpressionTypeResolver(program);
        _typeRefParser = new TypeRefParser(program);
        _typeCompatibility = new TypeCompatibility(_typeRefParser);
        _assignmentAnalyzer = CreateAssignmentAnalyzer(program);
        _returnAnalyzer = CreateReturnAnalyzer();
        _matchAnalyzer = CreateMatchAnalyzer(program);
        _foreachAnalyzer = CreateForeachAnalyzer();
        _typeUsageAnalyzer = new TypeUsageAnalyzer(
            diagnostics,
            program,
            _requirementMatcher,
            TypeText,
            IsKnownTypeName,
            FindAliasSuggestionForType,
            FindPartialImportSuggestionForType,
            FindImportSuggestionForType);
        var requirementDeclarations = new RequirementDeclarationAnalyzer(
            diagnostics,
            program,
            _requirementMatcher,
            TypeText);
        new AttributeSemanticAnalyzer(diagnostics).Analyze(
            program,
            (typeNode, location, inScopeTypeParameters) => AnalyzeType(typeNode, location, program, inScopeTypeParameters));

        foreach (var structNode in program.Structs)
        {
            requirementDeclarations.AnalyzeGenericConstraints(structNode.TypeParameters, structNode.GenericConstraints, structNode.Location);
            foreach (var field in structNode.Fields)
            {
                AnalyzeType(field.TypeNode, field.Location, program, structNode.TypeParameters);
            }

            requirementDeclarations.AnalyzeStructRequirements(structNode);
        }

        var globalVariables = program.GlobalVariables
            .Select(global => (global.Name, Type: TypeText(global.TypeNode)))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal);
        var returnFlow = new ReturnFlowAnalyzer(program, _expressionTypeResolver);
        var definiteAssignment = new DefiniteAssignmentAnalyzer(diagnostics, program, _expressionTypeResolver, returnFlow);
        foreach (var global in program.GlobalVariables)
        {
            var globalType = TypeText(global.TypeNode);
            AnalyzeType(global.TypeNode, global.Location, program, []);
            AnalyzeExpression(global.Initializer, global.Location, globalVariables);
            if (global.Initializer is not null && IsBareNull(global.Initializer) && !IsNullableType(ParseTypeRef(globalType)))
            {
                diagnostics.Report(global.Location, $"Cannot assign null to non-pointer global '{global.Name}' of type '{globalType}'.");
            }

            _assignmentAnalyzer?.CheckAssignmentCompatibility(
                global.Location,
                globalType,
                global.Initializer,
                globalVariables,
                $"global '{global.Name}'");
        }

        foreach (var function in program.Functions)
        {
            var effectiveGenericConstraints = GetEffectiveGenericConstraints(program, function);
            requirementDeclarations.AnalyzeGenericConstraints(function.TypeParameters, effectiveGenericConstraints, function.Location);
            AnalyzeType(function.ReturnTypeNode, function.Location, program, function.TypeParameters);
            var variables = new Dictionary<string, string>(globalVariables, StringComparer.Ordinal);
            foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                variables[parameter.Name] = TypeText(parameter.TypeNode);
            }
            var locals = CollectLocalVariables(function.Body).ToList();
            foreach (var local in locals)
            {
                variables[local.Name] = local.Type;
            }
            foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                AnalyzeType(parameter.TypeNode, parameter.Location, program, function.TypeParameters);
            }

            var mutability = variables.Keys.ToDictionary(name => name, _ => LocalMutability.Mutable, StringComparer.Ordinal);
            foreach (var global in program.GlobalVariables)
            {
                mutability[global.Name] = global.IsConst ? LocalMutability.ConstGlobal : LocalMutability.Mutable;
            }

            foreach (var local in CollectLocalMutability(function.Body))
            {
                mutability[local.Name] = local.Mutability;
            }

            var previousTypeParameters = _currentTypeParameters;
            var previousGenericConstraints = _currentGenericConstraints;
            _currentTypeParameters = function.TypeParameters;
            _currentGenericConstraints = effectiveGenericConstraints;
            _expressionTypeResolver = new ExpressionTypeResolver(program, _currentTypeParameters, _currentGenericConstraints);
            _assignmentAnalyzer = CreateAssignmentAnalyzer(program);
            _returnAnalyzer = CreateReturnAnalyzer();
            _matchAnalyzer = CreateMatchAnalyzer(program);
            _foreachAnalyzer = CreateForeachAnalyzer();

            var functionReturnType = function.ReturnTypeNode?.Semantic.Type ?? ParseTypeRef(TypeText(function.ReturnTypeNode));
            AnalyzeStatements(function.Body, functionReturnType, variables, mutability, program, function.TypeParameters);

            _currentTypeParameters = previousTypeParameters;
            _currentGenericConstraints = previousGenericConstraints;
            _expressionTypeResolver = new ExpressionTypeResolver(program, _currentTypeParameters, _currentGenericConstraints);
            _assignmentAnalyzer = CreateAssignmentAnalyzer(program);
            _returnAnalyzer = CreateReturnAnalyzer();
            _matchAnalyzer = CreateMatchAnalyzer(program);
            _foreachAnalyzer = CreateForeachAnalyzer();
            definiteAssignment.AnalyzeFunction(function, globalVariables);
            if (!IsVoidType(functionReturnType) && !returnFlow.StatementsAlwaysReturn(function.Body, variables))
            {
                diagnostics.Report(
                    function.Location,
                    $"Not all code paths return a value from function '{GetFunctionDisplayName(function)}' returning '{FormatTypeRef(functionReturnType)}'.");
            }
        }
    }

    private IReadOnlyList<GenericConstraintNode> GetEffectiveGenericConstraints(
        ProgramNode program,
        FunctionNode function)
    {
        var constraints = new List<GenericConstraintNode>();
        var ownerType = OwnerType(function);
        if (ownerType is not null)
        {
            var owner = program.Structs.FirstOrDefault(structNode =>
                string.Equals(structNode.Name, ownerType, StringComparison.Ordinal));
            if (owner is not null)
            {
                constraints.AddRange(owner.GenericConstraints);
            }
        }

        constraints.AddRange(function.GenericConstraints);
        return constraints;
    }

    private AssignmentSemanticAnalyzer? CreateAssignmentAnalyzer(ProgramNode program) =>
        _expressionTypeResolver is null || _typeCompatibility is null || _typeSystem is null || _typeRefParser is null
            ? null
            : new AssignmentSemanticAnalyzer(
                diagnostics,
                program,
                _expressionTypeResolver,
                _typeCompatibility,
                _typeSystem,
                _typeRefParser,
                TypeText);

    private ReturnSemanticAnalyzer? CreateReturnAnalyzer() =>
        _assignmentAnalyzer is null
            ? null
            : new ReturnSemanticAnalyzer(diagnostics, _assignmentAnalyzer);

    private MatchSemanticAnalyzer? CreateMatchAnalyzer(ProgramNode program) =>
        _expressionTypeResolver is null
            ? null
            : new MatchSemanticAnalyzer(
                diagnostics,
                program,
                _expressionTypeResolver,
                TypeText,
                IsKnownTypeName);

    private ForeachSemanticAnalyzer? CreateForeachAnalyzer() =>
        _typeSystem is null || _typeCompatibility is null || _expressionTypeResolver is null
            ? null
            : new ForeachSemanticAnalyzer(
                diagnostics,
                _typeSystem,
                _typeCompatibility,
                _expressionTypeResolver,
                TypeTextOrNull);

    private void AnalyzeStatements(
        IReadOnlyList<StatementNode> statements,
        TypeRef returnType,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        foreach (var statement in statements)
        {
            AnalyzeStatement(statement, returnType, variables, mutability, program, inScopeTypeParameters);
        }
    }

    private void AnalyzeStatement(
        StatementNode statement,
        TypeRef returnType,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        switch (statement)
        {
            case LetStatement let:
                var letType = TypeText(let.TypeNode);
                AnalyzeType(let.TypeNode, let.Location, program, inScopeTypeParameters);
                AnalyzeExpression(let.Initializer, let.Location, variables, mutability);
                if (let.Initializer is not null && IsBareNull(let.Initializer) && !IsNullableType(ParseTypeRef(letType)))
                {
                    diagnostics.Report(let.Location, $"Cannot assign null to non-pointer type '{letType}'.");
                }

                _assignmentAnalyzer?.CheckAssignmentCompatibility(let.Location, letType, let.Initializer, variables, $"local '{let.Name}'");
                variables[let.Name] = letType;
                mutability[let.Name] = let.IsConst ? LocalMutability.Const : LocalMutability.Mutable;
                break;

            case ReturnStatement ret:
                _returnAnalyzer?.AnalyzeReturn(ret, returnType, variables, mutability, AnalyzeExpression);
                break;

            case CStatement c:
                AnalyzeExpression(c.Expression, c.Location, variables, mutability);
                break;

            case IfStatement ifStatement:
                AnalyzeExpression(ifStatement.Condition, ifStatement.Location, variables, mutability);
                AnalyzeStatements(
                    ifStatement.ThenBody,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                if (ifStatement.ElseBranch is not null)
                {
                    AnalyzeStatement(
                        ifStatement.ElseBranch,
                        returnType,
                        new Dictionary<string, string>(variables, StringComparer.Ordinal),
                        new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                        program,
                        inScopeTypeParameters);
                }

                break;

            case ElseBlockStatement elseBlock:
                AnalyzeStatements(
                    elseBlock.Body,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                break;

            case WhileStatement whileStatement:
                AnalyzeExpression(whileStatement.Condition, whileStatement.Location, variables, mutability);
                AnalyzeStatements(
                    whileStatement.Body,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                break;

            case ForStatement forStatement:
                var forVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                var forMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
                AnalyzeForInitializer(forStatement.Initializer, forVariables, forMutability, program, inScopeTypeParameters);
                AnalyzeExpression(forStatement.Condition, forStatement.Location, forVariables, forMutability);
                AnalyzeExpression(forStatement.Increment, forStatement.Location, forVariables, forMutability);
                AnalyzeStatements(forStatement.Body, returnType, forVariables, forMutability, program, inScopeTypeParameters);
                break;

            case ForeachStatement foreachStatement:
                AnalyzeExpression(foreachStatement.IterableExpression, foreachStatement.Location, variables, mutability);
                var foreachScope = _foreachAnalyzer?.AnalyzeForeach(foreachStatement, variables, mutability)
                    ?? new ForeachAnalysisResult(
                        new Dictionary<string, string>(variables, StringComparer.Ordinal),
                        new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal));
                AnalyzeStatements(foreachStatement.Body, returnType, foreachScope.Variables, foreachScope.Mutability, program, inScopeTypeParameters);
                break;

            case SwitchStatement switchStatement:
                AnalyzeExpression(switchStatement.Expression, switchStatement.Location, variables, mutability);
                foreach (var switchCase in switchStatement.Cases)
                {
                    AnalyzeExpression(switchCase.Pattern, switchCase.Location, variables, mutability);
                    AnalyzeStatements(
                        switchCase.Body,
                        returnType,
                        new Dictionary<string, string>(variables, StringComparer.Ordinal),
                        new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                        program,
                        inScopeTypeParameters);
                }

                AnalyzeStatements(
                    switchStatement.DefaultBody,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                break;

            case MatchStatement matchStatement:
                AnalyzeExpression(matchStatement.Expression, matchStatement.Location, variables, mutability);
                foreach (var armBinding in _matchAnalyzer?.AnalyzeMatch(matchStatement, variables) ?? [])
                {
                    var arm = armBinding.Arm;
                    var armVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                    var armMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
                    if (arm.BindingName is not null && armBinding.Type is not null)
                    {
                        armVariables[arm.BindingName] = armBinding.Type;
                        armMutability[arm.BindingName] = LocalMutability.Mutable;
                    }

                    AnalyzeStatements(arm.Body, returnType, armVariables, armMutability, program, inScopeTypeParameters);
                }

                break;
        }
    }

    private RequirementMatch SatisfiesRequirement(
        string concreteType,
        string requirementName,
        IReadOnlyList<string>? requirementArguments = null) =>
        _typeSystem?.SatisfiesRequirement(concreteType, requirementName, requirementArguments)
        ?? RequirementMatch.Failed(concreteType, requirementName, []);

    private string GetFunctionDisplayName(FunctionNode function) =>
        OwnerType(function) is null
            ? function.Name
            : $"{OwnerType(function)}.{function.Name}";

    private static bool IsVoidType(string type) =>
        string.Equals(type.Trim(), "void", StringComparison.Ordinal);

    private static bool IsVoidType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Named { Name: "void", Arguments.Count: 0 };

    private void AnalyzeType(
        TypeNode? typeNode,
        Cx.Compiler.Syntax.Location location,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters) =>
        AnalyzeType(TypeText(typeNode), location, program, inScopeTypeParameters);

    private void AnalyzeType(
        string type,
        Cx.Compiler.Syntax.Location location,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        _ = program;
        _typeUsageAnalyzer?.Analyze(type, location, inScopeTypeParameters);
    }

    private void AnalyzeExpression(
        string? expression,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, LocalMutability>? mutability = null)
    {
        if (ContainsNullArithmetic(expression))
        {
            diagnostics.Report(location, "Cannot use null in arithmetic expressions.");
        }

        if (expression is null || !TrySplitTopLevelSpaceship(expression, out var left, out var right))
        {
            return;
        }

        if (variables is null || _expressionTypeResolver is null)
        {
            return;
        }

        var leftType = _expressionTypeResolver.ResolveTypeRef(new RawExpressionNode(location, left), variables);
        var rightType = _expressionTypeResolver.ResolveTypeRef(new RawExpressionNode(location, right), variables);
        if (leftType is null || rightType is null)
        {
            return;
        }

        AnalyzeSpaceshipTypes(leftType, rightType, location);
    }

    private void AnalyzeSpaceshipTypes(
        TypeRef leftType,
        TypeRef rightType,
        Cx.Compiler.Syntax.Location location)
    {
        var leftTypeText = FormatTypeRef(leftType)!;
        var rightTypeText = FormatTypeRef(rightType)!;
        if (_typeCompatibility is not null
            && (!_typeCompatibility.CanAssign(leftType, rightType, out _)
                || !_typeCompatibility.CanAssign(rightType, leftType, out _)))
        {
            diagnostics.Report(location, $"Cannot compare '{leftTypeText}' and '{rightTypeText}' with '<=>'.");
            return;
        }

        var match = SatisfiesRequirement(leftTypeText, "Compare", [leftTypeText]);
        if (match is { Success: false })
        {
            diagnostics.Report(
                location,
                $"Type '{leftTypeText}' does not satisfy requirement 'Compare': {string.Join(" ", match.Failures)}");
        }
    }

    private void AnalyzeForInitializer(
        ForInitializerNode initializer,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                var declarationType = TypeText(declaration.TypeNode);
                AnalyzeType(declaration.TypeNode, declaration.Location, program, inScopeTypeParameters);
                AnalyzeExpression(declaration.Initializer, declaration.Location, variables, mutability);
                if (declaration.Initializer is not null
                    && IsBareNull(declaration.Initializer)
                    && !IsNullableType(declarationType))
                {
                    diagnostics.Report(
                        declaration.Location,
                        $"Cannot assign null to non-pointer type '{declarationType}'.");
                }

                _assignmentAnalyzer?.CheckAssignmentCompatibility(
                    declaration.Location,
                    declarationType,
                    declaration.Initializer,
                    variables,
                    $"for variable '{declaration.Name}'");
                variables[declaration.Name] = declarationType;
                mutability[declaration.Name] = declaration.IsConst ? LocalMutability.Const : LocalMutability.Mutable;
                break;

            case ForExpressionInitializerNode expression:
                AnalyzeExpression(expression.Expression, expression.Location, variables, mutability);
                break;
        }
    }

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

    private static IEnumerable<(string Name, LocalMutability Mutability)> CollectLocalMutability(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return (let.Name, let.IsConst ? LocalMutability.Const : LocalMutability.Mutable);
                    break;
                case IfStatement ifStatement:
                    foreach (var variable in CollectLocalMutability(ifStatement.ThenBody))
                    {
                        yield return variable;
                    }

                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var variable in CollectLocalMutability([ifStatement.ElseBranch]))
                        {
                            yield return variable;
                        }
                    }

                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var variable in CollectLocalMutability(elseBlock.Body))
                    {
                        yield return variable;
                    }
                    break;
                case WhileStatement whileStatement:
                    foreach (var variable in CollectLocalMutability(whileStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForStatement forStatement:
                    if (forStatement.Initializer is ForDeclarationInitializerNode declaration)
                    {
                        yield return (declaration.Name, declaration.IsConst ? LocalMutability.Const : LocalMutability.Mutable);
                    }

                    foreach (var variable in CollectLocalMutability(forStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    if (foreachStatement.IndexBinding is not null)
                    {
                        yield return (foreachStatement.IndexBinding.Name, LocalMutability.ForeachIndex);
                    }

                    if (foreachStatement.KeyBinding is not null)
                    {
                        yield return (foreachStatement.KeyBinding.Name, LocalMutability.ForeachKey);
                    }

                    yield return (foreachStatement.ValueBinding.Name, foreachStatement.ValueBinding.IsConst
                        ? LocalMutability.ForeachConstItem
                        : LocalMutability.Mutable);

                    foreach (var variable in CollectLocalMutability(foreachStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var variable in CollectLocalMutability(switchCase.Body))
                        {
                            yield return variable;
                        }
                    }

                    foreach (var variable in CollectLocalMutability(switchStatement.DefaultBody))
                    {
                        yield return variable;
                    }
                    break;
                case MatchStatement matchStatement:
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var variable in CollectLocalMutability(arm.Body))
                        {
                            yield return variable;
                        }
                    }
                    break;
            }
        }
    }

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private static bool IsBareNull(string expression) =>
        string.Equals(expression.Trim(), "null", StringComparison.Ordinal);

    private static bool IsBareNull(ExpressionNode expression) =>
        IsBareNull(ExpressionText(expression));

    private static string ExpressionText(ExpressionNode expression) => expression.SourceText;

    private void AnalyzeExpression(
        ExpressionNode? expression,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, LocalMutability>? mutability = null)
    {
        AnalyzeExpressionTree(expression, location, variables, mutability);

        if (expression is not BinaryExpressionNode { Operator: "<=>" } binary
            || variables is null
            || _expressionTypeResolver is null)
        {
            AnalyzeExpression(expression?.SourceText, location, variables, mutability);
            return;
        }

        if (ContainsNullArithmetic(expression.SourceText))
        {
            diagnostics.Report(location, "Cannot use null in arithmetic expressions.");
        }

        var leftType = _expressionTypeResolver.ResolveTypeRef(binary.Left, variables);
        var rightType = _expressionTypeResolver.ResolveTypeRef(binary.Right, variables);
        if (leftType is not null && rightType is not null)
        {
            AnalyzeSpaceshipTypes(leftType, rightType, location);
        }
    }

    private void AnalyzeExpressionTree(
        ExpressionNode? expression,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        if (expression is null)
        {
            return;
        }

        switch (expression)
        {
            case NameExpressionNode name:
                AnalyzeNameExpression(name, location, variables);
                break;
            case ParenthesizedExpressionNode parenthesized:
                AnalyzeExpressionTree(parenthesized.Expression, location, variables, mutability);
                break;
            case CastExpressionNode cast:
                AnalyzeExpressionTree(cast.Expression, location, variables, mutability);
                break;
            case UnaryExpressionNode unary:
                AnalyzeExpressionTree(unary.Operand, location, variables, mutability);
                break;
            case PostfixExpressionNode postfix:
                if (postfix.Operator is "++" or "--")
                {
                    _assignmentAnalyzer?.AnalyzeMutationTarget(postfix.Operand, postfix.Location, mutability);
                }

                AnalyzeExpressionTree(postfix.Operand, location, variables, mutability);
                break;
            case SizeOfExpressionNode sizeOf:
                AnalyzeExpressionTree(sizeOf.ExpressionOperand, location, variables, mutability);
                break;
            case BinaryExpressionNode binary:
                AnalyzeExpressionTree(binary.Left, location, variables, mutability);
                AnalyzeExpressionTree(binary.Right, location, variables, mutability);
                break;
            case ScalarRangeExpressionNode range:
                AnalyzeExpressionTree(range.Start, location, variables, mutability);
                AnalyzeExpressionTree(range.End, location, variables, mutability);
                break;
            case ConditionalExpressionNode conditional:
                AnalyzeExpressionTree(conditional.Condition, location, variables, mutability);
                AnalyzeExpressionTree(conditional.WhenTrue, location, variables, mutability);
                AnalyzeExpressionTree(conditional.WhenFalse, location, variables, mutability);
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    AnalyzeExpressionTree(field.Value, location, variables, mutability);
                }

                foreach (var value in initializer.Values)
                {
                    AnalyzeExpressionTree(value, location, variables, mutability);
                }

                break;
            case FunctionExpressionNode functionExpression:
                AnalyzeExpressionTree(functionExpression.ExpressionBody, location, variables, mutability);
                break;
            case AssignmentExpressionNode assignment:
                AnalyzeExpressionTree(assignment.Target, location, variables, mutability);
                AnalyzeExpressionTree(assignment.Value, location, variables, mutability);
                if (variables is not null)
                {
                    _assignmentAnalyzer?.AnalyzeAssignmentExpression(
                        assignment,
                        variables,
                        mutability,
                        AnalyzeExpression);
                }

                break;
            case CallExpressionNode call:
                AnalyzeCallExpression(call, location, variables);
                AnalyzeExpressionTree(call.Callee, location, variables, mutability);
                foreach (var argument in call.Arguments)
                {
                    AnalyzeExpressionTree(argument, location, variables, mutability);
                }

                break;
            case GenericCallExpressionNode call:
                AnalyzeGenericCallExpression(call, location, variables);
                AnalyzeExpressionTree(call.Callee, location, variables, mutability);
                foreach (var argument in call.Arguments)
                {
                    AnalyzeExpressionTree(argument, location, variables, mutability);
                }

                break;
            case MemberExpressionNode member:
                AnalyzeExpressionTree(member.Target, location, variables, mutability);
                break;
            case IndexExpressionNode index:
                AnalyzeExpressionTree(index.Target, location, variables, mutability);
                AnalyzeExpressionTree(index.Index, location, variables, mutability);
                break;
        }
    }

    private void AnalyzeNameExpression(
        NameExpressionNode name,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null || _expressionTypeResolver is null)
        {
            return;
        }

        if (_expressionTypeResolver.Resolve(name, variables) is not null
            || IsKnownTypeName(name.SourceText)
            || _currentTypeParameters.Contains(name.SourceText, StringComparer.Ordinal)
            || IsKnownConstructorOrVariantCall(name.SourceText))
        {
            return;
        }

        if (FindAliasSuggestionForValue(name.SourceText) is { } aliasSuggestion)
        {
            diagnostics.Report(location, $"Unknown symbol '{name.SourceText}'. Did you mean '{aliasSuggestion}'?");
        }
        else if (FindPartialImportSuggestionForValue(name.SourceText) is { } partialSuggestion)
        {
            diagnostics.Report(location, $"Unknown symbol '{name.SourceText}'. Did you mean '{partialSuggestion}'?");
        }
        else if (FindImportSuggestionForValue(name.SourceText) is { } suggestion)
        {
            diagnostics.Report(location, $"Unknown symbol '{name.SourceText}'. Did you mean to import {suggestion}?");
        }
    }

    private void AnalyzeCallExpression(
        CallExpressionNode call,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null || _expressionTypeResolver is null || _typeCompatibility is null)
        {
            return;
        }

        if (ResolveCallSignature(call.Callee, [], call.Arguments, variables) is { } signature)
        {
            CheckCallArguments(location, signature, call.Arguments, variables);
            return;
        }

        ReportUnknownCall(call.Callee, location, variables);
    }

    private void ReportUnknownCall(
        ExpressionNode callee,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string> variables)
    {
        if (_expressionTypeResolver?.Resolve(callee, variables) is not null)
        {
            return;
        }

        if (callee is not NameExpressionNode)
        {
            return;
        }

        if (GetQualifiedName(callee) is { } name)
        {
            if (IsKnownConstructorOrVariantCall(name))
            {
                return;
            }

            if (IsKnownTypeName(name))
            {
                return;
            }

            var aliasSuggestion = FindAliasSuggestionForFunction(name);
            var partialSuggestion = aliasSuggestion is null ? FindPartialImportSuggestionForFunction(name) : null;
            var suggestion = aliasSuggestion is null && partialSuggestion is null ? FindImportSuggestionForFunction(name) : null;
            diagnostics.Report(
                location,
                aliasSuggestion is not null
                    ? $"Unknown function '{name}'. Did you mean '{aliasSuggestion}'?"
                    : partialSuggestion is not null
                    ? $"Unknown function '{name}'. Did you mean '{partialSuggestion}'?"
                    : suggestion is null
                    ? $"Unknown function '{name}'."
                    : $"Unknown function '{name}'. Did you mean to import {suggestion}?");
        }
    }

    private string? FindAliasSuggestionForFunction(string name) =>
        FindAliasSuggestion(name, DefinesFunction);

    private string? FindAliasSuggestionForType(string name) =>
        FindAliasSuggestion(name, DefinesType);

    private string? FindAliasSuggestionForValue(string name) =>
        FindAliasSuggestion(name, DefinesValue);

    private string? FindPartialImportSuggestionForFunction(string name) =>
        FindPartialImportSuggestion(name, DefinesFunction);

    private string? FindPartialImportSuggestionForType(string name) =>
        FindPartialImportSuggestion(name, DefinesType);

    private string? FindPartialImportSuggestionForValue(string name) =>
        FindPartialImportSuggestion(name, DefinesValue);

    private string? FindPartialImportSuggestion(
        string name,
        Func<ProgramNode, string, bool> definesSymbol)
    {
        if (_program is null || availablePrograms is null)
        {
            return null;
        }

        foreach (var import in _program.SymbolImports)
        {
            if (import.Symbols.Any(symbol =>
                    string.Equals(symbol.Name, name, StringComparison.Ordinal)
                    || string.Equals(symbol.Alias, name, StringComparison.Ordinal)))
            {
                continue;
            }

            if (availablePrograms.Any(program =>
                    string.Equals(program.Module?.Name, import.ModuleName, StringComparison.Ordinal)
                    && definesSymbol(program, name)))
            {
                return $"from {import.ModuleName} import {name}";
            }
        }

        return null;
    }

    private string? FindAliasSuggestion(
        string name,
        Func<ProgramNode, string, bool> definesSymbol)
    {
        if (_program is null || availablePrograms is null)
        {
            return null;
        }

        foreach (var import in _program.Imports.Where(import => import.Alias is not null))
        {
            if (availablePrograms.Any(program =>
                    string.Equals(program.Module?.Name, import.ModuleName, StringComparison.Ordinal)
                    && definesSymbol(program, name)))
            {
                return import.Alias + "." + name;
            }
        }

        return null;
    }

    private string? FindImportSuggestionForFunction(string name)
    {
        if (availablePrograms is null)
        {
            return null;
        }

        var visibleModules = _program is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : _program.Imports.Select(import => import.ModuleName)
                .Concat(_program.SymbolImports.Select(import => import.ModuleName))
                .Append(_program.Module?.Name ?? string.Empty)
                .Append("std.core")
                .ToHashSet(StringComparer.Ordinal);

        return availablePrograms
            .Select(program => new
            {
                ModuleName = program.Module?.Name ?? string.Empty,
                Program = program,
            })
            .Where(item => item.ModuleName.Length > 0)
            .Where(item => !visibleModules.Contains(item.ModuleName))
            .Where(item => DefinesFunction(item.Program, name))
            .Select(item => item.ModuleName)
            .OrderBy(moduleName => moduleName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private string? FindImportSuggestionForType(string name)
    {
        if (availablePrograms is null)
        {
            return null;
        }

        return FindImportSuggestion(name, DefinesType);
    }

    private string? FindImportSuggestionForValue(string name)
    {
        if (availablePrograms is null)
        {
            return null;
        }

        return FindImportSuggestion(name, DefinesValue);
    }

    private string? FindImportSuggestion(
        string name,
        Func<ProgramNode, string, bool> definesSymbol)
    {
        if (availablePrograms is null)
        {
            return null;
        }

        var visibleModules = _program is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : _program.Imports.Select(import => import.ModuleName)
                .Concat(_program.SymbolImports.Select(import => import.ModuleName))
                .Append(_program.Module?.Name ?? string.Empty)
                .Append("std.core")
                .ToHashSet(StringComparer.Ordinal);

        return availablePrograms
            .Select(program => new
            {
                ModuleName = program.Module?.Name ?? string.Empty,
                Program = program,
            })
            .Where(item => item.ModuleName.Length > 0)
            .Where(item => !visibleModules.Contains(item.ModuleName))
            .Where(item => definesSymbol(item.Program, name))
            .Select(item => item.ModuleName)
            .OrderBy(moduleName => moduleName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private bool DefinesFunction(ProgramNode program, string name) =>
        program.Functions.Any(function =>
            OwnerType(function) is null
            && string.Equals(function.Name, name, StringComparison.Ordinal))
        || program.ExternFunctions.Any(function =>
            string.Equals(function.Name, name, StringComparison.Ordinal))
        || program.CDeclarations.Any(declaration =>
            declaration.Functions.Any(function =>
                string.Equals(function.Name, name, StringComparison.Ordinal)));

    private static bool DefinesType(ProgramNode program, string name) =>
        program.TypeAliases.Any(typeAlias => string.Equals(typeAlias.Name, name, StringComparison.Ordinal))
        || program.Structs.Any(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal))
        || program.Enums.Any(enumNode => string.Equals(enumNode.Name, name, StringComparison.Ordinal))
        || program.Interfaces.Any(interfaceNode => string.Equals(interfaceNode.Name, name, StringComparison.Ordinal))
        || program.TaggedUnions.Any(union => string.Equals(union.Name, name, StringComparison.Ordinal))
        || program.CDeclarations.Any(declaration =>
            declaration.TypeAliases.Any(typeAlias => string.Equals(typeAlias.Name, name, StringComparison.Ordinal))
            || declaration.Structs.Any(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal))
            || declaration.Enums.Any(enumNode => string.Equals(enumNode.Name, name, StringComparison.Ordinal))
            || declaration.Unions.Any(union => string.Equals(union.Name, name, StringComparison.Ordinal)));

    private static bool DefinesValue(ProgramNode program, string name) =>
        program.GlobalVariables.Any(global => string.Equals(global.Name, name, StringComparison.Ordinal))
        || program.Enums.Any(enumNode => enumNode.Members.Any(member => string.Equals(member.Name, name, StringComparison.Ordinal)))
        || program.CDeclarations.Any(declaration =>
            declaration.Constants.Any(constant => string.Equals(constant.Name, name, StringComparison.Ordinal))
            || declaration.Enums.Any(enumNode => enumNode.Members.Any(member => string.Equals(member.Name, name, StringComparison.Ordinal))));

    private bool IsKnownConstructorOrVariantCall(string name)
    {
        if (_program is null)
        {
            return false;
        }

        if (_program.Structs.Any(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal)))
        {
            return true;
        }

        return _program.TaggedUnions
            .Where(union => !union.IsRaw)
            .Any(union => union.Variants.Any(variant =>
                string.Equals($"{union.Name}.{variant.Name}", name, StringComparison.Ordinal)
                || string.Equals(variant.Name, name, StringComparison.Ordinal)));
    }

    private bool IsKnownTypeName(string name)
    {
        if (_program is null)
        {
            return false;
        }

        return IsBuiltInTypeName(name)
            || _program.TypeAliases.Any(typeAlias => string.Equals(typeAlias.Name, name, StringComparison.Ordinal))
            || _program.Structs.Any(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal))
            || _program.Enums.Any(enumNode => string.Equals(enumNode.Name, name, StringComparison.Ordinal))
            || _program.Interfaces.Any(interfaceNode => string.Equals(interfaceNode.Name, name, StringComparison.Ordinal))
            || _program.TaggedUnions.Any(union => string.Equals(union.Name, name, StringComparison.Ordinal));
    }

    private static bool IsBuiltInTypeName(string name) =>
        name is
            "void" or
            "char" or
            "short" or
            "int" or
            "long" or
            "long long" or
            "float" or
            "double" or
            "bool";

    private void AnalyzeGenericCallExpression(
        GenericCallExpressionNode call,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null || _expressionTypeResolver is null || _typeCompatibility is null)
        {
            return;
        }

        if (ResolveCallSignature(call.Callee, TypeArguments(call.TypeArgumentNodes), call.Arguments, variables) is { } signature)
        {
            CheckCallArguments(location, signature, call.Arguments, variables);
            return;
        }

        ReportUnknownCall(call.Callee, location, variables);
    }

    private CallSignature? ResolveCallSignature(
        ExpressionNode callee,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        if (_program is null || _expressionTypeResolver is null)
        {
            return null;
        }

        var resolvedCall = new CallResolver(
            _program,
            _expressionTypeResolver.Resolve,
            _currentTypeParameters,
            _currentGenericConstraints).Resolve(callee, typeArguments, arguments, variables);
        return resolvedCall is null
            ? null
            : new CallSignature(resolvedCall.Name, resolvedCall.ParameterTypes, resolvedCall.IsVariadic);
    }

    private void CheckCallArguments(
        Cx.Compiler.Syntax.Location location,
        CallSignature signature,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        if (_expressionTypeResolver is null || _typeCompatibility is null)
        {
            return;
        }

        if (arguments.Count < signature.ParameterTypes.Count)
        {
            diagnostics.Report(
                location,
                $"Call to '{signature.Name}' expects at least {signature.ParameterTypes.Count} argument(s), got {arguments.Count}.");
            return;
        }

        if (!signature.IsVariadic && arguments.Count > signature.ParameterTypes.Count)
        {
            diagnostics.Report(
                location,
                $"Call to '{signature.Name}' expects {signature.ParameterTypes.Count} argument(s), got {arguments.Count}.");
            return;
        }

        for (var i = 0; i < signature.ParameterTypes.Count && i < arguments.Count; i++)
        {
            var parameterType = signature.ParameterTypes[i];
            if (IsAnyType(parameterType))
            {
                continue;
            }

            var argumentType = _expressionTypeResolver.ResolveTypeRef(arguments[i], variables);
            if (!_typeCompatibility.CanAssign(parameterType, argumentType, out var reason))
            {
                diagnostics.Report(
                    location,
                    $"Argument {i + 1} for call to '{signature.Name}' has incompatible type: {reason}.");
            }
        }
    }

    private static bool ContainsNullArithmetic(string? expression) =>
        expression is not null
        && Regex.IsMatch(expression, @"(\bnull\b\s*[\+\-\*/%])|([\+\-\*/%]\s*\bnull\b)");

    private static bool IsNullableType(string type) =>
        type.TrimEnd().EndsWith("*", StringComparison.Ordinal);

    private static bool IsNullableType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Pointer;

    private static bool IsAnyType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Named { Name: "any", Arguments.Count: 0 };

    private TypeRef ParseTypeRef(string type) =>
        _typeRefParser?.Parse(type) ?? new TypeRef.Unknown();

    private static TypeRef? UnwrapAlias(TypeRef? type)
    {
        while (type is TypeRef.Alias alias)
        {
            type = alias.Target;
        }

        return type;
    }

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

    private static bool TrySplitTopLevelSpaceship(string expression, out string left, out string right)
    {
        var depth = 0;
        for (var i = 0; i <= expression.Length - 3; i++)
        {
            depth += expression[i] switch
            {
                '(' or '[' or '{' => 1,
                ')' or ']' or '}' => -1,
                _ => 0,
            };

            if (depth != 0 || !expression.AsSpan(i, 3).SequenceEqual("<=>"))
            {
                continue;
            }

            left = expression[..i].Trim();
            right = expression[(i + 3)..].Trim();
            return left.Length > 0 && right.Length > 0;
        }

        left = string.Empty;
        right = string.Empty;
        return false;
    }

    private string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

    private IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> nodes) =>
        nodes.Select(TypeText).ToList();

    private string TypeText(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        if (_typeRefParser is null)
        {
            throw new InvalidOperationException("Semantic analyzer has no TypeRef parser.");
        }

        var type = typeNode.ToTypeRef(_typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private string? TypeTextOrNull(TypeNode? typeNode)
    {
        var type = TypeText(typeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }

    private sealed record CallSignature(
        string Name,
        IReadOnlyList<TypeRef> ParameterTypes,
        bool IsVariadic);

}
