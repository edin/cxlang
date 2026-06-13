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
    private ExpressionSemanticAnalyzer? _expressionAnalyzer;
    private SymbolSuggestionService? _symbolSuggestions;
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
        _symbolSuggestions = new SymbolSuggestionService(program, availablePrograms, OwnerType);
        _assignmentAnalyzer = CreateAssignmentAnalyzer(program);
        _returnAnalyzer = CreateReturnAnalyzer();
        _matchAnalyzer = CreateMatchAnalyzer(program);
        _foreachAnalyzer = CreateForeachAnalyzer();
        _expressionAnalyzer = CreateExpressionAnalyzer();
        _typeUsageAnalyzer = new TypeUsageAnalyzer(
            diagnostics,
            program,
            _requirementMatcher,
            TypeText,
            IsKnownTypeName,
            _symbolSuggestions.FindAliasSuggestionForType,
            _symbolSuggestions.FindPartialImportSuggestionForType,
            _symbolSuggestions.FindImportSuggestionForType);
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
        var typeRefParser = _typeRefParser ?? throw new InvalidOperationException("Semantic analyzer has no TypeRef parser.");
        var globalTypeEnvironment = TypeEnvironment.FromLegacyStrings(typeRefParser, globalVariables);
        var returnFlow = new ReturnFlowAnalyzer(program, _expressionTypeResolver);
        var definiteAssignment = new DefiniteAssignmentAnalyzer(diagnostics, program, _expressionTypeResolver, returnFlow);
        foreach (var global in program.GlobalVariables)
        {
            var globalType = TypeText(global.TypeNode);
            AnalyzeType(global.TypeNode, global.Location, program, []);
            AnalyzeExpression(global.Initializer, global.Location, globalVariables, globalTypeEnvironment, null);
            if (global.Initializer is not null && IsBareNull(global.Initializer) && !IsNullableType(ParseTypeRef(globalType)))
            {
                diagnostics.Report(global.Location, $"Cannot assign null to non-pointer global '{global.Name}' of type '{globalType}'.");
            }

            _assignmentAnalyzer?.CheckAssignmentCompatibility(
                global.Location,
                globalType,
                global.Initializer,
                globalTypeEnvironment,
                $"global '{global.Name}'");
        }

        foreach (var function in program.Functions)
        {
            var effectiveGenericConstraints = GetEffectiveGenericConstraints(program, function);
            requirementDeclarations.AnalyzeGenericConstraints(function.TypeParameters, effectiveGenericConstraints, function.Location);
            AnalyzeType(function.ReturnTypeNode, function.Location, program, function.TypeParameters);
            var variables = new Dictionary<string, string>(globalVariables, StringComparer.Ordinal);
            var typeEnvironment = globalTypeEnvironment.Clone();
            foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                SetVariableType(variables, typeEnvironment, parameter.Name, parameter.TypeNode.ToTypeRef(typeRefParser));
            }
            var locals = CollectLocalVariables(function.Body).ToList();
            foreach (var local in locals)
            {
                SetVariableType(variables, typeEnvironment, local.Name, ParseTypeRef(local.Type));
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
            _expressionAnalyzer = CreateExpressionAnalyzer();

            var functionReturnType = function.ReturnTypeNode?.Semantic.Type ?? ParseTypeRef(TypeText(function.ReturnTypeNode));
            AnalyzeStatements(function.Body, functionReturnType, variables, typeEnvironment, mutability, program, function.TypeParameters);

            _currentTypeParameters = previousTypeParameters;
            _currentGenericConstraints = previousGenericConstraints;
            _expressionTypeResolver = new ExpressionTypeResolver(program, _currentTypeParameters, _currentGenericConstraints);
            _assignmentAnalyzer = CreateAssignmentAnalyzer(program);
            _returnAnalyzer = CreateReturnAnalyzer();
            _matchAnalyzer = CreateMatchAnalyzer(program);
            _foreachAnalyzer = CreateForeachAnalyzer();
            _expressionAnalyzer = CreateExpressionAnalyzer();
            definiteAssignment.AnalyzeFunction(function, globalVariables);
            if (!IsVoidType(functionReturnType) && !returnFlow.StatementsAlwaysReturn(function.Body, typeEnvironment))
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
                _typeRefParser);

    private ReturnSemanticAnalyzer? CreateReturnAnalyzer() =>
        _assignmentAnalyzer is null
            ? null
            : new ReturnSemanticAnalyzer(diagnostics, _assignmentAnalyzer);

    private MatchSemanticAnalyzer? CreateMatchAnalyzer(ProgramNode program) =>
        _expressionTypeResolver is null || _typeRefParser is null
            ? null
            : new MatchSemanticAnalyzer(
                diagnostics,
                program,
                _expressionTypeResolver,
                _typeRefParser,
                IsKnownTypeName);

    private ForeachSemanticAnalyzer? CreateForeachAnalyzer() =>
        _typeSystem is null || _typeCompatibility is null || _expressionTypeResolver is null || _typeRefParser is null
            ? null
            : new ForeachSemanticAnalyzer(
                diagnostics,
                _typeSystem,
                _typeCompatibility,
                _expressionTypeResolver,
                _typeRefParser);

    private ExpressionSemanticAnalyzer? CreateExpressionAnalyzer() =>
        _program is null || _expressionTypeResolver is null || _typeCompatibility is null
            ? null
            : new ExpressionSemanticAnalyzer(
                diagnostics,
                _program,
                _assignmentAnalyzer,
                _expressionTypeResolver,
                _typeCompatibility,
                _symbolSuggestions,
                _currentTypeParameters,
                _currentGenericConstraints,
                TypeText,
                IsKnownTypeName,
                AnalyzeExpression);

    private void AnalyzeStatements(
        IReadOnlyList<StatementNode> statements,
        TypeRef returnType,
        Dictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        Dictionary<string, LocalMutability> mutability,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        foreach (var statement in statements)
        {
            AnalyzeStatement(statement, returnType, variables, typeEnvironment, mutability, program, inScopeTypeParameters);
        }
    }

    private void AnalyzeStatement(
        StatementNode statement,
        TypeRef returnType,
        Dictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        Dictionary<string, LocalMutability> mutability,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        switch (statement)
        {
            case LetStatement let:
                var letType = TypeText(let.TypeNode);
                AnalyzeType(let.TypeNode, let.Location, program, inScopeTypeParameters);
                AnalyzeExpression(let.Initializer, let.Location, variables, typeEnvironment, mutability);
                if (let.Initializer is not null && IsBareNull(let.Initializer) && !IsNullableType(ParseTypeRef(letType)))
                {
                    diagnostics.Report(let.Location, $"Cannot assign null to non-pointer type '{letType}'.");
                }

                _assignmentAnalyzer?.CheckAssignmentCompatibility(let.Location, letType, let.Initializer, typeEnvironment, $"local '{let.Name}'");
                SetVariableType(variables, typeEnvironment, let.Name, ParseTypeRef(letType));
                mutability[let.Name] = let.IsConst ? LocalMutability.Const : LocalMutability.Mutable;
                break;

            case ReturnStatement ret:
                _returnAnalyzer?.AnalyzeReturn(ret, returnType, variables, typeEnvironment, mutability, AnalyzeExpression);
                break;

            case CStatement c:
                AnalyzeExpression(c.Expression, c.Location, variables, typeEnvironment, mutability);
                break;

            case IfStatement ifStatement:
                AnalyzeExpression(ifStatement.Condition, ifStatement.Location, variables, typeEnvironment, mutability);
                AnalyzeStatements(
                    ifStatement.ThenBody,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    typeEnvironment.Clone(),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                if (ifStatement.ElseBranch is not null)
                {
                    AnalyzeStatement(
                        ifStatement.ElseBranch,
                        returnType,
                        new Dictionary<string, string>(variables, StringComparer.Ordinal),
                        typeEnvironment.Clone(),
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
                    typeEnvironment.Clone(),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                break;

            case WhileStatement whileStatement:
                AnalyzeExpression(whileStatement.Condition, whileStatement.Location, variables, typeEnvironment, mutability);
                AnalyzeStatements(
                    whileStatement.Body,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    typeEnvironment.Clone(),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                break;

            case ForStatement forStatement:
                var forVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                var forTypeEnvironment = typeEnvironment.Clone();
                var forMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
                AnalyzeForInitializer(forStatement.Initializer, forVariables, forTypeEnvironment, forMutability, program, inScopeTypeParameters);
                AnalyzeExpression(forStatement.Condition, forStatement.Location, forVariables, forTypeEnvironment, forMutability);
                AnalyzeExpression(forStatement.Increment, forStatement.Location, forVariables, forTypeEnvironment, forMutability);
                AnalyzeStatements(forStatement.Body, returnType, forVariables, forTypeEnvironment, forMutability, program, inScopeTypeParameters);
                break;

            case ForeachStatement foreachStatement:
                AnalyzeExpression(foreachStatement.IterableExpression, foreachStatement.Location, variables, typeEnvironment, mutability);
                var foreachScope = _foreachAnalyzer?.AnalyzeForeach(foreachStatement, typeEnvironment, mutability)
                    ?? new ForeachAnalysisResult(
                        new Dictionary<string, string>(variables, StringComparer.Ordinal),
                        TypeEnvironment.FromLegacyStrings(_typeRefParser ?? new TypeRefParser(program), variables),
                        new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal));
                AnalyzeStatements(
                    foreachStatement.Body,
                    returnType,
                    foreachScope.Variables,
                    foreachScope.TypeEnvironment,
                    foreachScope.Mutability,
                    program,
                    inScopeTypeParameters);
                break;

            case SwitchStatement switchStatement:
                AnalyzeExpression(switchStatement.Expression, switchStatement.Location, variables, typeEnvironment, mutability);
                foreach (var switchCase in switchStatement.Cases)
                {
                    AnalyzeExpression(switchCase.Pattern, switchCase.Location, variables, typeEnvironment, mutability);
                    AnalyzeStatements(
                        switchCase.Body,
                        returnType,
                        new Dictionary<string, string>(variables, StringComparer.Ordinal),
                        typeEnvironment.Clone(),
                        new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                        program,
                        inScopeTypeParameters);
                }

                AnalyzeStatements(
                    switchStatement.DefaultBody,
                    returnType,
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                    typeEnvironment.Clone(),
                    new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal),
                    program,
                    inScopeTypeParameters);
                break;

            case MatchStatement matchStatement:
                AnalyzeExpression(matchStatement.Expression, matchStatement.Location, variables, typeEnvironment, mutability);
                foreach (var armBinding in _matchAnalyzer?.AnalyzeMatch(matchStatement, typeEnvironment) ?? [])
                {
                    var arm = armBinding.Arm;
                    var armVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                    var armTypeEnvironment = typeEnvironment.Clone();
                    var armMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
                    if (arm.BindingName is not null && armBinding.Type is not null)
                    {
                        SetVariableType(armVariables, armTypeEnvironment, arm.BindingName, armBinding.Type);
                        armMutability[arm.BindingName] = LocalMutability.Mutable;
                    }

                    AnalyzeStatements(arm.Body, returnType, armVariables, armTypeEnvironment, armMutability, program, inScopeTypeParameters);
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



    private static bool IsVoidType(TypeRef? type) =>
        TypeRefFacts.IsNamed(type, "void");

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
        TypeEnvironment typeEnvironment,
        Dictionary<string, LocalMutability> mutability,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                var declarationType = TypeText(declaration.TypeNode);
                AnalyzeType(declaration.TypeNode, declaration.Location, program, inScopeTypeParameters);
                AnalyzeExpression(declaration.Initializer, declaration.Location, variables, typeEnvironment, mutability);
                if (declaration.Initializer is not null
                    && IsBareNull(declaration.Initializer)
                    && !IsNullableType(ParseTypeRef(declarationType)))
                {
                    diagnostics.Report(
                        declaration.Location,
                        $"Cannot assign null to non-pointer type '{declarationType}'.");
                }

                _assignmentAnalyzer?.CheckAssignmentCompatibility(
                    declaration.Location,
                    declarationType,
                    declaration.Initializer,
                    typeEnvironment,
                    $"for variable '{declaration.Name}'");
                SetVariableType(variables, typeEnvironment, declaration.Name, ParseTypeRef(declarationType));
                mutability[declaration.Name] = declaration.IsConst ? LocalMutability.Const : LocalMutability.Mutable;
                break;

            case ForExpressionInitializerNode expression:
                AnalyzeExpression(expression.Expression, expression.Location, variables, typeEnvironment, mutability);
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
        AnalyzeExpression(expression, location, variables, null, mutability);
    }

    private void AnalyzeExpression(
        ExpressionNode? expression,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, string>? variables,
        TypeEnvironment? typeEnvironment,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        _expressionAnalyzer?.Analyze(expression, location, variables, typeEnvironment, mutability);

        if (ContainsNullArithmetic(expression))
        {
            diagnostics.Report(location, "Cannot use null in arithmetic expressions.");
        }

        if (expression is not BinaryExpressionNode { Operator: "<=>" } binary
            || (variables is null && typeEnvironment is null)
            || _expressionTypeResolver is null)
        {
            return;
        }

        var leftType = typeEnvironment is null
            ? _expressionTypeResolver.ResolveTypeRef(binary.Left, variables!)
            : _expressionTypeResolver.ResolveTypeRef(binary.Left, typeEnvironment);
        var rightType = typeEnvironment is null
            ? _expressionTypeResolver.ResolveTypeRef(binary.Right, variables!)
            : _expressionTypeResolver.ResolveTypeRef(binary.Right, typeEnvironment);
        if (leftType is not null && rightType is not null)
        {
            AnalyzeSpaceshipTypes(leftType, rightType, location);
        }
    }

    private bool IsKnownTypeName(string name)
    {
        if (_program is null)
        {
            return false;
        }

        return BuiltinTypes.IsBuiltin(name)
            || _program.TypeAliases.Any(typeAlias => string.Equals(typeAlias.Name, name, StringComparison.Ordinal))
            || _program.Structs.Any(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal))
            || _program.Enums.Any(enumNode => string.Equals(enumNode.Name, name, StringComparison.Ordinal))
            || _program.Interfaces.Any(interfaceNode => string.Equals(interfaceNode.Name, name, StringComparison.Ordinal))
            || _program.TaggedUnions.Any(union => string.Equals(union.Name, name, StringComparison.Ordinal));
    }

    private static bool ContainsNullArithmetic(ExpressionNode? expression) =>
        expression switch
        {
            BinaryExpressionNode { Operator: "+" or "-" or "*" or "/" or "%", Left: var left, Right: var right }
                when IsNullLiteral(left) || IsNullLiteral(right) => true,
            BinaryExpressionNode binary => ContainsNullArithmetic(binary.Left) || ContainsNullArithmetic(binary.Right),
            ParenthesizedExpressionNode parenthesized => ContainsNullArithmetic(parenthesized.Expression),
            CastExpressionNode cast => ContainsNullArithmetic(cast.Expression),
            UnaryExpressionNode unary => ContainsNullArithmetic(unary.Operand),
            PostfixExpressionNode postfix => ContainsNullArithmetic(postfix.Operand),
            SizeOfExpressionNode sizeOf => ContainsNullArithmetic(sizeOf.ExpressionOperand),
            ScalarRangeExpressionNode range => ContainsNullArithmetic(range.Start) || ContainsNullArithmetic(range.End),
            ConditionalExpressionNode conditional =>
                ContainsNullArithmetic(conditional.Condition)
                || ContainsNullArithmetic(conditional.WhenTrue)
                || ContainsNullArithmetic(conditional.WhenFalse),
            InitializerExpressionNode initializer =>
                initializer.Fields.Any(field => ContainsNullArithmetic(field.Value))
                || initializer.Values.Any(ContainsNullArithmetic),
            FunctionExpressionNode function => ContainsNullArithmetic(function.ExpressionBody),
            AssignmentExpressionNode assignment =>
                ContainsNullArithmetic(assignment.Target) || ContainsNullArithmetic(assignment.Value),
            CallExpressionNode call =>
                ContainsNullArithmetic(call.Callee) || call.Arguments.Any(ContainsNullArithmetic),
            GenericCallExpressionNode call =>
                ContainsNullArithmetic(call.Callee) || call.Arguments.Any(ContainsNullArithmetic),
            MemberExpressionNode member => ContainsNullArithmetic(member.Target),
            IndexExpressionNode index => ContainsNullArithmetic(index.Target) || ContainsNullArithmetic(index.Index),
            _ => false,
        };

    private static bool IsNullLiteral(ExpressionNode expression) =>
        expression is LiteralExpressionNode { SourceText: "null" }
        || expression is ParenthesizedExpressionNode parenthesized && IsNullLiteral(parenthesized.Expression);

    private static bool IsNullableType(TypeRef? type) =>
        TypeRefFacts.IsPointer(type);

    private TypeRef ParseTypeRef(string type) =>
        _typeRefParser?.Parse(type) ?? new TypeRef.Unknown();

    private static void SetVariableType(
        Dictionary<string, string> variables,
        TypeEnvironment typeEnvironment,
        string name,
        TypeRef type)
    {
        variables[name] = TypeRefFormatter.ToCxString(type);
        typeEnvironment.Set(name, type);
    }

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

    private string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

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

}
