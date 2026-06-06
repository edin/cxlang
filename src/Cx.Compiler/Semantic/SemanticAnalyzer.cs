using System.Text.RegularExpressions;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

public sealed class SemanticAnalyzer(
    DiagnosticBag diagnostics,
    IReadOnlyList<ProgramNode>? availablePrograms = null)
{
    private RequirementMatcher? _requirementMatcher;
    private ExpressionTypeResolver? _expressionTypeResolver;
    private TypeCompatibility? _typeCompatibility;
    private TypeRefParser? _typeRefParser;
    private ProgramNode? _program;
    private IReadOnlyList<string> _currentTypeParameters = [];
    private IReadOnlyList<GenericConstraintNode> _currentGenericConstraints = [];

    private enum LocalMutability
    {
        Mutable,
        Const,
        ConstGlobal,
        ForeachIndex,
        ForeachKey,
        ForeachConstItem,
    }

    public void Analyze(ProgramNode program)
    {
        _program = program;
        _requirementMatcher = new RequirementMatcher(program);
        _expressionTypeResolver = new ExpressionTypeResolver(program);
        _typeRefParser = new TypeRefParser(program);
        _typeCompatibility = new TypeCompatibility(_typeRefParser);
        AnalyzeAttributes(program);

        foreach (var structNode in program.Structs)
        {
            AnalyzeGenericConstraints(structNode.TypeParameters, structNode.GenericConstraints, structNode.Location);
            foreach (var field in structNode.Fields)
            {
                AnalyzeType(field.Type, field.Location, program, structNode.TypeParameters);
            }

            AnalyzeStructRequirements(structNode);
        }

        var globalVariables = program.GlobalVariables
            .Select(global => (global.Name, global.Type))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal);
        var returnFlow = new ReturnFlowAnalyzer(program, _expressionTypeResolver);
        var definiteAssignment = new DefiniteAssignmentAnalyzer(diagnostics, program, _expressionTypeResolver, returnFlow);
        foreach (var global in program.GlobalVariables)
        {
            AnalyzeType(global.Type, global.Location, program, []);
            AnalyzeExpression(global.Initializer, global.Location, globalVariables);
            if (global.Initializer is not null && IsBareNull(global.Initializer) && !IsNullableType(ParseTypeRef(global.Type)))
            {
                diagnostics.Report(global.Location, $"Cannot assign null to non-pointer global '{global.Name}' of type '{global.Type}'.");
            }

            CheckAssignmentCompatibility(
                global.Location,
                global.Type,
                global.Initializer,
                globalVariables,
                $"global '{global.Name}'");
        }

        foreach (var function in program.Functions)
        {
            var effectiveGenericConstraints = GetEffectiveGenericConstraints(program, function);
            AnalyzeGenericConstraints(function.TypeParameters, effectiveGenericConstraints, function.Location);
            AnalyzeType(function.ReturnType, function.Location, program, function.TypeParameters);
            var variables = new Dictionary<string, string>(globalVariables, StringComparer.Ordinal);
            foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                variables[parameter.Name] = parameter.Type;
            }
            var locals = CollectLocalVariables(function.Body).ToList();
            foreach (var local in locals)
            {
                variables[local.Name] = local.Type;
            }
            foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                AnalyzeType(parameter.Type, parameter.Location, program, function.TypeParameters);
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

            var functionReturnType = function.ReturnTypeNode?.Semantic.Type ?? ParseTypeRef(function.ReturnType);
            AnalyzeStatements(function.Body, functionReturnType, variables, mutability, program, function.TypeParameters);

            _currentTypeParameters = previousTypeParameters;
            _currentGenericConstraints = previousGenericConstraints;
            _expressionTypeResolver = new ExpressionTypeResolver(program, _currentTypeParameters, _currentGenericConstraints);
            definiteAssignment.AnalyzeFunction(function, globalVariables);
            if (!IsVoidType(functionReturnType) && !returnFlow.StatementsAlwaysReturn(function.Body, variables))
            {
                diagnostics.Report(
                    function.Location,
                    $"Not all code paths return a value from function '{GetFunctionDisplayName(function)}' returning '{FormatTypeRef(functionReturnType)}'.");
            }
        }
    }

    private void AnalyzeAttributes(ProgramNode program)
    {
        var declarations = BuiltInAttributeDeclarations()
            .Concat(program.AttributeDeclarations)
            .GroupBy(attribute => attribute.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (var group in program.AttributeDeclarations.GroupBy(attribute => attribute.Name, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                diagnostics.Report(group.Last().Location, $"Attribute '{group.Key}' is declared more than once.");
            }
        }

        foreach (var declaration in program.AttributeDeclarations)
        {
            foreach (var field in declaration.Fields)
            {
                AnalyzeType(field.Type, field.Location, program, []);
            }
        }

        foreach (var typeAlias in program.TypeAliases)
        {
            AnalyzeAttributeApplications(typeAlias.Attributes, "type_alias", declarations);
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            AnalyzeAttributeApplications(externFunction.Attributes, "extern", declarations);
            foreach (var parameter in externFunction.Parameters)
            {
                AnalyzeAttributeApplications(parameter.Attributes, "parameter", declarations);
            }
        }

        foreach (var global in program.GlobalVariables)
        {
            AnalyzeAttributeApplications(global.Attributes, "global", declarations);
        }

        foreach (var enumNode in program.Enums)
        {
            AnalyzeAttributeApplications(enumNode.Attributes, "enum", declarations);
            foreach (var member in enumNode.Members)
            {
                AnalyzeAttributeApplications(member.Attributes, "variant", declarations);
            }
        }

        foreach (var structNode in program.Structs)
        {
            AnalyzeAttributeApplications(structNode.Attributes, "struct", declarations);
            foreach (var field in structNode.Fields)
            {
                AnalyzeAttributeApplications(field.Attributes, "field", declarations);
            }

        }

        foreach (var taggedUnion in program.TaggedUnions)
        {
            AnalyzeAttributeApplications(taggedUnion.Attributes, "union", declarations);
            foreach (var variant in taggedUnion.Variants)
            {
                AnalyzeAttributeApplications(variant.Attributes, "variant", declarations);
            }

        }

        foreach (var function in program.Functions)
        {
            AnalyzeFunctionAttributes(function, declarations);
        }
    }

    private void AnalyzeFunctionAttributes(
        FunctionNode function,
        IReadOnlyDictionary<string, AttributeDeclarationNode> declarations)
    {
        AnalyzeAttributeApplications(function.Attributes, "fn", declarations);
        foreach (var parameter in function.Parameters)
        {
            AnalyzeAttributeApplications(parameter.Attributes, "parameter", declarations);
        }
    }

    private void AnalyzeAttributeApplications(
        IReadOnlyList<AttributeApplicationNode> applications,
        string target,
        IReadOnlyDictionary<string, AttributeDeclarationNode> declarations)
    {
        foreach (var duplicate in applications
            .GroupBy(attribute => attribute.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            diagnostics.Report(duplicate.Last().Location, $"Attribute '{duplicate.Key}' cannot be applied more than once.");
        }

        foreach (var application in applications)
        {
            if (!declarations.TryGetValue(application.Name, out var declaration))
            {
                diagnostics.Report(application.Location, $"Unknown attribute '{application.Name}'.");
                continue;
            }

            if (!declaration.Targets.Contains(target, StringComparer.Ordinal))
            {
                diagnostics.Report(application.Location, $"Attribute '{application.Name}' cannot be applied to {target}.");
            }

            if (application.Name == "derive")
            {
                continue;
            }

            var namedArguments = application.Arguments.Where(argument => argument.Name is not null).ToList();
            foreach (var argument in namedArguments)
            {
                if (!declaration.Fields.Any(field => field.Name == argument.Name))
                {
                    diagnostics.Report(argument.Location, $"Attribute '{application.Name}' has no field named '{argument.Name}'.");
                }
            }

            if (namedArguments.Count > 0)
            {
                foreach (var field in declaration.Fields)
                {
                    if (!namedArguments.Any(argument => argument.Name == field.Name))
                    {
                        diagnostics.Report(application.Location, $"Attribute '{application.Name}' requires argument '{field.Name}'.");
                    }
                }

                continue;
            }

            if (application.Arguments.Count != declaration.Fields.Count)
            {
                diagnostics.Report(application.Location, $"Attribute '{application.Name}' expects {declaration.Fields.Count} argument(s).");
            }
        }
    }

    private static IReadOnlyList<GenericConstraintNode> GetEffectiveGenericConstraints(
        ProgramNode program,
        FunctionNode function)
    {
        var constraints = new List<GenericConstraintNode>();
        if (function.OwnerType is not null)
        {
            var owner = program.Structs.FirstOrDefault(structNode =>
                string.Equals(structNode.Name, function.OwnerType, StringComparison.Ordinal));
            if (owner is not null)
            {
                constraints.AddRange(owner.GenericConstraints);
            }
        }

        constraints.AddRange(function.GenericConstraints);
        return constraints;
    }

    private static IReadOnlyList<AttributeDeclarationNode> BuiltInAttributeDeclarations() =>
    [
        new AttributeDeclarationNode(
            new Cx.Compiler.Syntax.Location(new Cx.Compiler.Syntax.SourceFile("<built-in>", ""), 0, 1, 1),
            "derive",
            ["struct", "union", "enum"],
            [])
    ];

    private void AnalyzeGenericConstraints(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<GenericConstraintNode> constraints,
        Cx.Compiler.Syntax.Location location)
    {
        foreach (var constraint in constraints)
        {
            if (!typeParameters.Contains(constraint.TypeParameter, StringComparer.Ordinal))
            {
                diagnostics.Report(
                    constraint.Location,
                    $"Unknown generic type parameter '{constraint.TypeParameter}' in where clause.");
            }

            foreach (var requirement in constraint.Requirements)
            {
                AnalyzeRequirementReference(requirement, allowInferredTypeArguments: false);
            }
        }
    }

    private void AnalyzeStructRequirements(StructNode structNode)
    {
        foreach (var requirement in structNode.Requirements)
        {
            AnalyzeRequirementReference(requirement, allowInferredTypeArguments: true);
            var selfType = GetStructSelfType(structNode);
            var arguments = requirement.TypeArguments.Count > 0
                ? requirement.TypeArguments
                : structNode.TypeParameters;
            var match = _requirementMatcher?.Match(selfType, requirement.Name, arguments);
            if (match is null || match.Success)
            {
                continue;
            }

            diagnostics.Report(
                requirement.Location,
                $"Struct '{selfType}' declares '{FormatRequirementReference(requirement)}' but does not satisfy it: {FormatRequirementFailures(match.Failures)}");
        }
    }

    private void AnalyzeRequirementReference(
        StructRequirementNode reference,
        bool allowInferredTypeArguments)
    {
        if (_program is null)
        {
            return;
        }

        var requirement = _program.Requirements.FirstOrDefault(requirement =>
            string.Equals(requirement.Name, reference.Name, StringComparison.Ordinal));
        if (requirement is not null)
        {
            if (reference.TypeArguments.Count > 0
                && reference.TypeArguments.Count != requirement.TypeParameters.Count)
            {
                diagnostics.Report(
                    reference.Location,
                    $"Requirement '{reference.Name}' expects {requirement.TypeParameters.Count} type argument(s), but {reference.TypeArguments.Count} were provided.");
            }
            else if (!allowInferredTypeArguments
                && reference.TypeArguments.Count == 0
                && requirement.TypeParameters.Count > 0)
            {
                diagnostics.Report(
                    reference.Location,
                    $"Requirement '{reference.Name}' in a where clause needs explicit type arguments: {reference.Name}<{string.Join(", ", requirement.TypeParameters)}>.");
            }

            return;
        }

        var interfaceNode = _program.Interfaces.FirstOrDefault(interfaceNode =>
            string.Equals(interfaceNode.Name, reference.Name, StringComparison.Ordinal));
        if (interfaceNode is not null)
        {
            if (reference.TypeArguments.Count > 0)
            {
                diagnostics.Report(
                    reference.Location,
                    $"Interface '{reference.Name}' does not take type arguments.");
            }

            return;
        }

        diagnostics.Report(reference.Location, $"Unknown requirement '{reference.Name}'.");
    }

    private static string GetStructSelfType(StructNode structNode) =>
        structNode.TypeParameters.Count == 0
            ? structNode.Name
            : $"{structNode.Name}<{string.Join(",", structNode.TypeParameters)}>";

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
                AnalyzeType(let.Type, let.Location, program, inScopeTypeParameters);
                AnalyzeExpression(let.Initializer, let.Location, variables, mutability);
                if (let.Initializer is not null && IsBareNull(let.Initializer) && !IsNullableType(ParseTypeRef(let.Type)))
                {
                    diagnostics.Report(let.Location, $"Cannot assign null to non-pointer type '{let.Type}'.");
                }

                CheckAssignmentCompatibility(let.Location, let.Type, let.Initializer, variables, $"local '{let.Name}'");
                variables[let.Name] = let.Type;
                mutability[let.Name] = let.IsConst ? LocalMutability.Const : LocalMutability.Mutable;
                break;

            case ReturnStatement ret:
                if (IsVoidType(returnType))
                {
                    if (!IsEmptyExpression(ret.Expression))
                    {
                        AnalyzeExpression(ret.Expression, ret.Location, variables, mutability);
                        diagnostics.Report(ret.Location, "Cannot return a value from function returning void.");
                    }

                    break;
                }

                if (IsEmptyExpression(ret.Expression))
                {
                    diagnostics.Report(ret.Location, $"Function returning '{FormatTypeRef(returnType)}' must return a value.");
                    break;
                }

                AnalyzeExpression(ret.Expression, ret.Location, variables, mutability);
                if (IsBareNull(ret.Expression) && !IsNullableType(returnType))
                {
                    diagnostics.Report(ret.Location, $"Cannot return null from function returning non-pointer type '{FormatTypeRef(returnType)}'.");
                }

                CheckAssignmentCompatibility(ret.Location, returnType, ret.Expression, variables, "return value");
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
                var foreachVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                var foreachMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
                var iterableExpression = ExpressionText(foreachStatement.IterableExpression);
                if (foreachStatement.IterableExpression is ScalarRangeExpressionNode rangeExpression)
                {
                    if (foreachStatement.KeyBinding is not null)
                    {
                        diagnostics.Report(foreachStatement.Location, "Key/value foreach is not supported for scalar ranges.");
                    }

                    var rangeType = _expressionTypeResolver?.Resolve(rangeExpression, variables) ?? "int";
                    AddForeachScalarRangeBindings(foreachStatement, foreachVariables, foreachMutability, rangeType);
                }
                else if (!TryResolveVariableType(iterableExpression, variables, out var iterableType))
                {
                    diagnostics.Report(
                        foreachStatement.Location,
                        $"Cannot resolve foreach iterable '{iterableExpression}'. Use a visible local/global value, fixed array, scalar range like 0..10, or a type satisfying foreach requirements.");
                }
                else if (foreachStatement.KeyBinding is not null)
                {
                    if (TryResolveIteratorForeachTypes(
                        iterableType,
                        keyValue: true,
                        out var keyValueElementType,
                        out var keyValueKeyType))
                    {
                        if (keyValueKeyType is not null)
                        {
                            foreachVariables[foreachStatement.KeyBinding.Name] = string.IsNullOrWhiteSpace(foreachStatement.KeyBinding.Type)
                                ? keyValueKeyType
                                : foreachStatement.KeyBinding.Type;
                            foreachMutability[foreachStatement.KeyBinding.Name] = LocalMutability.ForeachKey;
                        }

                        AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, keyValueElementType);
                    }
                    else
                    {
                        ReportForeachRequirementFailure(
                            foreachStatement,
                            iterableType,
                            _requirementMatcher?.Match(iterableType, "Contiguous") ?? RequirementMatch.Failed(iterableType, "Contiguous", []));
                    }
                }
                else if (TryParseFixedArrayType(iterableType, out var arrayElementType, out _))
                {
                    AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, arrayElementType);
                }
                else if (TryResolveIteratorForeachTypes(
                    iterableType,
                    keyValue: false,
                    out var iteratorElementType,
                    out _))
                {
                    AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, iteratorElementType);
                }
                else if (_requirementMatcher?.Match(iterableType, "Contiguous") is { Success: true } contiguous
                    && contiguous.TypeBindings.TryGetValue("T", out var contiguousElementType))
                {
                    AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, contiguousElementType);
                }
                else if (_requirementMatcher?.Match(iterableType, "ContiguousRange") is { Success: true } range
                    && range.TypeBindings.TryGetValue("T", out var rangeElementType))
                {
                    AddForeachValueBindings(foreachStatement, foreachVariables, foreachMutability, rangeElementType);
                }
                else if (_requirementMatcher?.Match(iterableType, "Contiguous") is { } match && !match.Success)
                {
                    ReportForeachRequirementFailure(foreachStatement, iterableType, match);
                }

                AnalyzeStatements(foreachStatement.Body, returnType, foreachVariables, foreachMutability, program, inScopeTypeParameters);
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
                var matchExpressionType = _expressionTypeResolver?.Resolve(matchStatement.Expression, variables);
                TaggedUnionNode? matchedTaggedUnion = null;
                InterfaceNode? matchedInterface = null;
                if (matchExpressionType is not null
                    && _program?.TaggedUnions.FirstOrDefault(union => union.Name == StripPointer(ResolveAlias(matchExpressionType))) is { IsRaw: true })
                {
                    diagnostics.Report(matchStatement.Location, $"Cannot pattern match raw union type '{matchExpressionType}'.");
                }
                else if (ResolveMatchedTaggedUnion(matchStatement, variables) is { } taggedUnion)
                {
                    matchedTaggedUnion = taggedUnion;
                    AnalyzeMatchExhaustiveness(matchStatement, taggedUnion);
                }
                else if (ResolveMatchedInterface(matchStatement, variables) is { } interfaceNode)
                {
                    matchedInterface = interfaceNode;
                    AnalyzeInterfaceMatchArms(matchStatement, interfaceNode);
                }

                foreach (var arm in matchStatement.Arms)
                {
                    var armVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                    var armMutability = new Dictionary<string, LocalMutability>(mutability, StringComparer.Ordinal);
                    if (matchedTaggedUnion is not null
                        && arm.BindingName is not null
                        && arm.Pattern != "_"
                        && matchedTaggedUnion.Variants.FirstOrDefault(variant => variant.Name == arm.Pattern) is { } variant)
                    {
                        armVariables[arm.BindingName] = variant.Type;
                        armMutability[arm.BindingName] = LocalMutability.Mutable;
                    }
                    else if (matchedInterface is not null
                        && arm.BindingName is not null
                        && arm.Pattern != "_"
                        && InterfaceImplementationExists(arm.Pattern, matchedInterface.Name))
                    {
                        armVariables[arm.BindingName] = arm.Pattern + "*";
                        armMutability[arm.BindingName] = LocalMutability.Mutable;
                    }

                    AnalyzeStatements(arm.Body, returnType, armVariables, armMutability, program, inScopeTypeParameters);
                }

                break;
        }
    }

    private void AnalyzeMatchExhaustiveness(MatchStatement matchStatement, TaggedUnionNode taggedUnion)
    {
        var variantNames = taggedUnion.Variants
            .Select(variant => variant.Name)
            .ToHashSet(StringComparer.Ordinal);
        var seenPatterns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var arm in matchStatement.Arms)
        {
            if (arm.Pattern == "_")
            {
                continue;
            }

            if (!variantNames.Contains(arm.Pattern))
            {
                diagnostics.Report(
                    arm.Location,
                    $"Unknown match arm '{arm.Pattern}' for union '{taggedUnion.Name}'.");
                continue;
            }

            if (!seenPatterns.Add(arm.Pattern))
            {
                diagnostics.Report(
                    arm.Location,
                    $"Duplicate match arm '{arm.Pattern}' for union '{taggedUnion.Name}'.");
            }
        }

        if (IsMatchExhaustive(matchStatement, taggedUnion))
        {
            return;
        }

        var covered = matchStatement.Arms
            .Select(arm => arm.Pattern)
            .ToHashSet(StringComparer.Ordinal);
        var missing = taggedUnion.Variants
            .Select(variant => variant.Name)
            .Where(variantName => !covered.Contains(variantName))
            .ToList();
        diagnostics.Report(
            matchStatement.Location,
            $"Match on union '{taggedUnion.Name}' is not exhaustive. Missing variants: {string.Join(", ", missing)}.");
    }

    private static bool IsMatchExhaustive(MatchStatement matchStatement, TaggedUnionNode? taggedUnion)
    {
        if (matchStatement.Arms.Any(arm => arm.Pattern == "_"))
        {
            return true;
        }

        if (taggedUnion is null)
        {
            return false;
        }

        var covered = matchStatement.Arms
            .Select(arm => arm.Pattern)
            .ToHashSet(StringComparer.Ordinal);
        return taggedUnion.Variants.All(variant => covered.Contains(variant.Name));
    }

    private TaggedUnionNode? ResolveMatchedTaggedUnion(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = _expressionTypeResolver?.Resolve(matchStatement.Expression, variables);
        if (matchExpressionType is null || _program is null)
        {
            return null;
        }

        var normalizedType = StripPointer(ResolveAlias(matchExpressionType));
        return _program.TaggedUnions.FirstOrDefault(union =>
            string.Equals(union.Name, normalizedType, StringComparison.Ordinal));
    }

    private InterfaceNode? ResolveMatchedInterface(
        MatchStatement matchStatement,
        IReadOnlyDictionary<string, string> variables)
    {
        var matchExpressionType = _expressionTypeResolver?.Resolve(matchStatement.Expression, variables);
        if (matchExpressionType is null || _program is null)
        {
            return null;
        }

        var normalizedType = StripPointer(ResolveAlias(matchExpressionType));
        return _program.Interfaces.FirstOrDefault(interfaceNode =>
            string.Equals(interfaceNode.Name, normalizedType, StringComparison.Ordinal));
    }

    private void AnalyzeInterfaceMatchArms(MatchStatement matchStatement, InterfaceNode interfaceNode)
    {
        var seenPatterns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arm in matchStatement.Arms)
        {
            if (arm.Pattern == "_")
            {
                continue;
            }

            if (!InterfaceImplementationExists(arm.Pattern, interfaceNode.Name))
            {
                var message = IsKnownTypeName(arm.Pattern)
                    ? $"Type '{arm.Pattern}' does not implement interface '{interfaceNode.Name}'."
                    : $"Unknown match arm '{arm.Pattern}' for interface '{interfaceNode.Name}'.";
                diagnostics.Report(
                    arm.Location,
                    message);
                continue;
            }

            if (!seenPatterns.Add(arm.Pattern))
            {
                diagnostics.Report(
                    arm.Location,
                    $"Duplicate match arm '{arm.Pattern}' for interface '{interfaceNode.Name}'.");
            }
        }
    }

    private bool InterfaceImplementationExists(string structName, string interfaceName) =>
        _program?.Structs.Any(structNode =>
            string.Equals(structNode.Name, structName, StringComparison.Ordinal)
            && structNode.Requirements.Any(requirement =>
                string.Equals(requirement.Name, interfaceName, StringComparison.Ordinal))) == true;

    private void AddForeachValueBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        string elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            var indexType = string.IsNullOrWhiteSpace(indexBinding.Type)
                ? "usize"
                : indexBinding.Type;
            variables[indexBinding.Name] = indexType;
            mutability[indexBinding.Name] = LocalMutability.ForeachIndex;
        }

        var declaredElementType = string.IsNullOrWhiteSpace(foreachStatement.ValueBinding.Type)
            ? elementType
            : foreachStatement.ValueBinding.Type;
        if (!string.IsNullOrWhiteSpace(foreachStatement.ValueBinding.Type)
            && _typeCompatibility is not null
            && !_typeCompatibility.CanAssign(foreachStatement.ValueBinding.Type, elementType, out var reason))
        {
            diagnostics.Report(
                foreachStatement.ValueBinding.Location,
                $"Type mismatch for foreach value '{foreachStatement.ValueBinding.Name}': {reason}.");
        }

        variables[foreachStatement.ValueBinding.Name] = declaredElementType;
        mutability[foreachStatement.ValueBinding.Name] = foreachStatement.ValueBinding.IsConst
            ? LocalMutability.ForeachConstItem
            : LocalMutability.Mutable;
    }

    private void AddForeachScalarRangeBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        Dictionary<string, LocalMutability> mutability,
        string elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            variables[indexBinding.Name] = string.IsNullOrWhiteSpace(indexBinding.Type)
                ? "usize"
                : indexBinding.Type;
            mutability[indexBinding.Name] = LocalMutability.ForeachIndex;
        }

        var declaredElementType = string.IsNullOrWhiteSpace(foreachStatement.ValueBinding.Type)
            ? elementType
            : foreachStatement.ValueBinding.Type;
        variables[foreachStatement.ValueBinding.Name] = declaredElementType;
        mutability[foreachStatement.ValueBinding.Name] = foreachStatement.ValueBinding.IsConst
            ? LocalMutability.ForeachConstItem
            : LocalMutability.Mutable;
    }

    private bool TryResolveIteratorForeachTypes(
        string iterableType,
        bool keyValue,
        out string valueType,
        out string? keyType)
    {
        valueType = string.Empty;
        keyType = null;
        if (_requirementMatcher is null)
        {
            return false;
        }

        var iterableRequirement = keyValue ? "KeyValueIterable" : "Iterable";
        var iterableMatch = _requirementMatcher.Match(iterableType, iterableRequirement);
        if (!iterableMatch.Success || !iterableMatch.TypeBindings.ContainsKey("I"))
        {
            return false;
        }

        if (keyValue)
        {
            if (!iterableMatch.TypeBindings.TryGetValue("K", out var matchedKeyType)
                || !iterableMatch.TypeBindings.TryGetValue("V", out var matchedValueType))
            {
                return false;
            }

            keyType = matchedKeyType;
            valueType = matchedValueType;
            return true;
        }

        if (!iterableMatch.TypeBindings.TryGetValue("T", out var matchedItemType))
        {
            return false;
        }

        valueType = matchedItemType;
        return true;
    }

    private void ReportForeachRequirementFailure(
        ForeachStatement foreachStatement,
        string iterableType,
        RequirementMatch contiguousMatch)
    {
        if (_requirementMatcher is null)
        {
            diagnostics.Report(
                foreachStatement.Location,
                $"Type '{iterableType}' cannot be used in foreach.");
            return;
        }

        var keyValue = foreachStatement.KeyBinding is not null;
        var iterableRequirementName = keyValue ? "KeyValueIterable" : "Iterable";
        var iteratorRequirementName = keyValue ? "KeyValueIterator" : "Iterator";
        var iterableRequirementDisplay = keyValue ? "KeyValueIterable<K, V, I>" : "Iterable<T, I>";
        var rangeMatch = _requirementMatcher.Match(iterableType, "ContiguousRange");
        var iteratorMatch = _requirementMatcher.Match(iterableType, iterableRequirementName);
        var parts = new List<string>
        {
            $"Type '{iterableType}' cannot be used in foreach.",
            keyValue
                ? "Expected key/value foreach source: KeyValueIterable<K, V, I> where I: KeyValueIterator<K, V>."
                : "Expected foreach source: fixed array, scalar range, Iterable<T, I> where I: Iterator<T>, Contiguous<T>, or ContiguousRange<T>.",
        };

        if (!iteratorMatch.Success)
        {
            parts.Add($"{iterableRequirementDisplay}: " + FormatRequirementFailures(iteratorMatch.Failures));
        }
        else if (!iteratorMatch.TypeBindings.TryGetValue("I", out var iteratorType))
        {
            parts.Add($"{iterableRequirementDisplay}: could not infer iterator type 'I' from iterator().");
        }
        else
        {
            var concreteIteratorMatch = _requirementMatcher.Match(iteratorType, iteratorRequirementName);
            if (!concreteIteratorMatch.Success)
            {
                parts.Add($"{iteratorType} must satisfy {iteratorRequirementName}: {FormatRequirementFailures(concreteIteratorMatch.Failures)}");
            }
        }

        if (contiguousMatch.Failures.Count > 0)
        {
            parts.Add("Contiguous<T>: " + FormatRequirementFailures(contiguousMatch.Failures));
        }

        if (rangeMatch.Failures.Count > 0)
        {
            parts.Add("ContiguousRange<T>: " + FormatRequirementFailures(rangeMatch.Failures));
        }

        parts.Add(keyValue
            ? "Add iterator(self: Self*) plus next/key/value methods, or use 'foreach item in source' for value iteration."
            : "Add iterator(self: Self*) with next/value methods, data/length fields, or start/end fields.");

        diagnostics.Report(foreachStatement.Location, string.Join(" ", parts));
    }

    private static string FormatRequirementFailures(IReadOnlyList<string> failures) =>
        failures.Count == 0
            ? "no details available."
            : string.Join(" ", failures.Select(failure => failure.Trim()));

    private static string FormatRequirementReference(StructRequirementNode requirement) =>
        requirement.TypeArguments.Count == 0
            ? requirement.Name
            : $"{requirement.Name}<{string.Join(", ", requirement.TypeArguments)}>";

    private static string GetFunctionDisplayName(FunctionNode function) =>
        function.OwnerType is null
            ? function.Name
            : $"{function.OwnerType}.{function.Name}";

    private static bool IsVoidType(string type) =>
        string.Equals(type.Trim(), "void", StringComparison.Ordinal);

    private static bool IsVoidType(TypeRef? type) =>
        UnwrapAlias(type) is TypeRef.Named { Name: "void", Arguments.Count: 0 };

    private static bool IsEmptyExpression(ExpressionNode expression) =>
        string.IsNullOrWhiteSpace(expression.SourceText);

    private void AnalyzeType(
        string type,
        Cx.Compiler.Syntax.Location location,
        ProgramNode program,
        IReadOnlyList<string> inScopeTypeParameters)
    {
        foreach (var typeName in FindTypeNames(type)
            .Where(typeName => !inScopeTypeParameters.Contains(typeName, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal))
        {
            if (IsKnownTypeName(typeName))
            {
                continue;
            }

            if (FindAliasSuggestionForType(typeName) is { } aliasSuggestion)
            {
                diagnostics.Report(location, $"Unknown type '{typeName}'. Did you mean '{aliasSuggestion}'?");
            }
            else if (FindPartialImportSuggestionForType(typeName) is { } partialSuggestion)
            {
                diagnostics.Report(location, $"Unknown type '{typeName}'. Did you mean '{partialSuggestion}'?");
            }
            else if (FindImportSuggestionForType(typeName) is { } suggestion)
            {
                diagnostics.Report(location, $"Unknown type '{typeName}'. Did you mean to import {suggestion}?");
            }
        }

        foreach (var use in FindGenericStructUses(type))
        {
            var definition = program.Structs.FirstOrDefault(structNode =>
                structNode.Name == use.Name
                && structNode.TypeParameters.Count == use.Arguments.Count);
            if (definition is null || definition.GenericConstraints.Count == 0)
            {
                continue;
            }

            var substitutions = definition.TypeParameters
                .Zip(use.Arguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var constraint in definition.GenericConstraints)
            {
                if (!substitutions.TryGetValue(constraint.TypeParameter, out var concreteType))
                {
                    continue;
                }

                if (inScopeTypeParameters.Contains(concreteType, StringComparer.Ordinal))
                {
                    continue;
                }

                foreach (var requirement in constraint.Requirements)
                {
                    var arguments = requirement.TypeArguments
                        .Select(argument => GenericTypeStringRewriter.Substitute(argument, substitutions))
                        .ToList();
                    var match = _requirementMatcher?.Match(concreteType, requirement.Name, arguments);
                    if (match is null || match.Success)
                    {
                        continue;
                    }

                    diagnostics.Report(
                        location,
                        $"Type '{concreteType}' used for '{definition.Name}.{constraint.TypeParameter}' does not satisfy requirement '{requirement.Name}': {string.Join(" ", match.Failures)}");
                }
            }
        }
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

        var leftType = _expressionTypeResolver.Resolve(new RawExpressionNode(location, left), variables);
        var rightType = _expressionTypeResolver.Resolve(new RawExpressionNode(location, right), variables);
        if (leftType is null || rightType is null)
        {
            return;
        }

        AnalyzeSpaceshipTypes(leftType, rightType, location);
    }

    private void AnalyzeSpaceshipTypes(
        string leftType,
        string rightType,
        Cx.Compiler.Syntax.Location location)
    {
        if (!SameTypeName(leftType, rightType))
        {
            diagnostics.Report(location, $"Cannot compare '{leftType}' and '{rightType}' with '<=>'.");
            return;
        }

        var match = _requirementMatcher?.Match(leftType, "Compare", [leftType]);
        if (match is { Success: false })
        {
            diagnostics.Report(
                location,
                $"Type '{leftType}' does not satisfy requirement 'Compare': {string.Join(" ", match.Failures)}");
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
                AnalyzeType(declaration.Type, declaration.Location, program, inScopeTypeParameters);
                AnalyzeExpression(declaration.Initializer, declaration.Location, variables, mutability);
                if (declaration.Initializer is not null
                    && IsBareNull(declaration.Initializer)
                    && !IsNullableType(declaration.Type))
                {
                    diagnostics.Report(
                        declaration.Location,
                        $"Cannot assign null to non-pointer type '{declaration.Type}'.");
                }

                CheckAssignmentCompatibility(
                    declaration.Location,
                    declaration.Type,
                    declaration.Initializer,
                    variables,
                    $"for variable '{declaration.Name}'");
                variables[declaration.Name] = declaration.Type;
                mutability[declaration.Name] = declaration.IsConst ? LocalMutability.Const : LocalMutability.Mutable;
                break;

            case ForExpressionInitializerNode expression:
                AnalyzeExpression(expression.Expression, expression.Location, variables, mutability);
                break;
        }
    }

    private static bool SameTypeName(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

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

    private static bool TryResolveVariableType(
        string expression,
        IReadOnlyDictionary<string, string> variables,
        out string type)
    {
        return variables.TryGetValue(expression.Trim(), out type!);
    }

    private static bool TryParseFixedArrayType(string type, out string elementType, out string length)
    {
        elementType = string.Empty;
        length = string.Empty;
        type = type.Trim();
        if (!type.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var openBracket = type.LastIndexOf('[');
        if (openBracket < 0)
        {
            return false;
        }

        elementType = type[..openBracket].Trim();
        length = type[(openBracket + 1)..^1].Trim();
        return !string.IsNullOrWhiteSpace(elementType) && !string.IsNullOrWhiteSpace(length);
    }

    private string ResolveAlias(string type)
    {
        if (_program is null)
        {
            return type;
        }

        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var aliases = _program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().TargetType, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(type, out var targetType) && seen.Add(type))
        {
            type = targetType;
        }

        return type + pointerSuffix;
    }

    private static string StripPointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    private static string GetGenericBaseName(string type)
    {
        type = type.Trim();
        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        return genericStart < 0
            ? type
            : type[..genericStart].Trim();
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

    private static bool MatchesGenericArguments(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<string> typeArguments) =>
        typeParameters.Count == typeArguments.Count;

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private static bool TryParseFunctionType(
        string? type,
        out IReadOnlyList<string> parameterTypes,
        out string returnType,
        out bool isVariadic)
    {
        parameterTypes = [];
        returnType = string.Empty;
        isVariadic = false;
        type = type?.Trim() ?? string.Empty;
        if (!type.StartsWith("fn(", StringComparison.Ordinal))
        {
            return false;
        }

        var close = FindMatchingParen(type, 2);
        if (close < 0 || close + 2 >= type.Length || type[close + 1] != '-' || type[close + 2] != '>')
        {
            return false;
        }

        var parsedParameters = SplitGenericArguments(type[3..close]);
        isVariadic = parsedParameters.LastOrDefault() == "...";
        parameterTypes = parsedParameters
            .Where(parameter => parameter != "...")
            .ToList();
        returnType = type[(close + 3)..].Trim();
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static int FindMatchingParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
                continue;
            }

            if (text[i] != ')')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private void AnalyzeAssignmentExpression(
        AssignmentExpressionNode assignment,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        if (_expressionTypeResolver is null || _typeCompatibility is null)
        {
            return;
        }

        AnalyzeAssignmentMutability(assignment, mutability);

        var targetTypeRef = _expressionTypeResolver.ResolveTypeRef(assignment.Target, variables);
        if (targetTypeRef is null)
        {
            return;
        }

        var targetType = FormatTypeRef(targetTypeRef)!;

        if (assignment.Operator == "=")
        {
            if (IsBareNull(assignment.Value) && !IsNullableType(targetTypeRef))
            {
                diagnostics.Report(assignment.Location, $"Cannot assign null to non-pointer type '{targetType}'.");
                return;
            }

            CheckAssignmentCompatibility(assignment.Location, targetTypeRef, assignment.Value, variables, "assignment");
            return;
        }

        CheckCompoundAssignmentCompatibility(assignment.Location, targetTypeRef, assignment.Operator, assignment.Value, variables);
    }

    private void AnalyzeAssignmentMutability(
        AssignmentExpressionNode assignment,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        AnalyzeMutationTarget(assignment.Target, assignment.Location, mutability);
    }

    private void AnalyzeMutationTarget(
        ExpressionNode target,
        Cx.Compiler.Syntax.Location location,
        IReadOnlyDictionary<string, LocalMutability>? mutability)
    {
        if (mutability is null || GetAssignmentRootName(target) is not { } name)
        {
            return;
        }

        if (!mutability.TryGetValue(name, out var localMutability))
        {
            return;
        }

        var message = localMutability switch
        {
            LocalMutability.Const => $"Cannot assign to const local '{name}'.",
            LocalMutability.ConstGlobal => $"Cannot assign to const global '{name}'.",
            LocalMutability.ForeachIndex => $"Cannot assign to foreach index '{name}'.",
            LocalMutability.ForeachKey => $"Cannot assign to foreach key '{name}'.",
            LocalMutability.ForeachConstItem => $"Cannot assign to const foreach item '{name}'.",
            _ => null,
        };

        if (message is not null)
        {
            diagnostics.Report(location, message);
        }
    }

    private static string? GetAssignmentRootName(ExpressionNode target) => target switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetAssignmentRootName(parenthesized.Expression),
        MemberExpressionNode member => GetAssignmentRootName(member.Target),
        IndexExpressionNode index => GetAssignmentRootName(index.Target),
        UnaryExpressionNode { Operator: "*" } unary => GetAssignmentRootName(unary.Operand),
        _ => null,
    };

    private void CheckAssignmentCompatibility(
        Cx.Compiler.Syntax.Location location,
        string targetType,
        ExpressionNode? sourceExpression,
        IReadOnlyDictionary<string, string> variables,
        string subject) =>
        CheckAssignmentCompatibility(location, ParseTypeRef(targetType), sourceExpression, variables, subject);

    private void CheckAssignmentCompatibility(
        Cx.Compiler.Syntax.Location location,
        TypeRef? targetType,
        ExpressionNode? sourceExpression,
        IReadOnlyDictionary<string, string> variables,
        string subject)
    {
        if (targetType is null || sourceExpression is null || _expressionTypeResolver is null || _typeCompatibility is null)
        {
            return;
        }

        if (sourceExpression is InitializerExpressionNode { TypeName: null })
        {
            return;
        }

        var sourceType = _expressionTypeResolver.ResolveTypeRef(sourceExpression, variables);
        var targetTypeText = FormatTypeRef(targetType);
        var sourceTypeText = FormatTypeRef(sourceType);
        if (targetTypeText is not null && IsTaggedUnionVariantAssignment(targetTypeText, sourceTypeText))
        {
            return;
        }

        if (targetTypeText is not null && IsInterfaceBindingAssignment(targetTypeText, sourceTypeText))
        {
            return;
        }

        if (targetTypeText is not null && IsSelfPointerAssignment(targetTypeText, sourceTypeText))
        {
            return;
        }

        if (!_typeCompatibility.CanAssign(targetType, sourceType, out var reason))
        {
            diagnostics.Report(location, $"Type mismatch for {subject}: {reason}.");
        }
    }

    private static bool IsSelfPointerAssignment(string targetType, string? sourceType) =>
        string.Equals(sourceType?.Trim(), "Self*", StringComparison.Ordinal)
        && targetType.TrimEnd().EndsWith("*", StringComparison.Ordinal);

    private bool IsInterfaceBindingAssignment(string targetType, string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || _program is null)
        {
            return false;
        }

        var interfaceNode = _program.Interfaces.FirstOrDefault(interfaceNode =>
            string.Equals(interfaceNode.Name, targetType, StringComparison.Ordinal));
        if (interfaceNode is null)
        {
            return false;
        }

        var sourceBaseType = GetGenericBaseName(StripPointer(ResolveAlias(sourceType)));
        var structNode = _program.Structs.FirstOrDefault(structNode =>
            string.Equals(structNode.Name, sourceBaseType, StringComparison.Ordinal));
        if (structNode?.Requirements.Any(requirement =>
            string.Equals(requirement.Name, interfaceNode.Name, StringComparison.Ordinal)) == true)
        {
            return true;
        }

        return _requirementMatcher?.Match(sourceType, interfaceNode.Name) is { Success: true };
    }

    private void CheckCompoundAssignmentCompatibility(
        Cx.Compiler.Syntax.Location location,
        TypeRef targetType,
        string assignmentOperator,
        ExpressionNode value,
        IReadOnlyDictionary<string, string> variables)
    {
        if (_expressionTypeResolver is null || _typeCompatibility is null)
        {
            return;
        }

        var valueType = _expressionTypeResolver.ResolveTypeRef(value, variables);
        if (valueType is null)
        {
            return;
        }

        if (IsPointerType(targetType)
            && assignmentOperator is "+=" or "-="
            && IsNumericLikeType(valueType))
        {
            return;
        }

        if (IsNumericLikeType(targetType)
            && IsNumericLikeType(valueType))
        {
            return;
        }

        if (assignmentOperator == "+="
            && _typeCompatibility.CanAssign(targetType, valueType, out _))
        {
            return;
        }

        diagnostics.Report(
            location,
            $"Type mismatch for compound assignment: cannot apply '{assignmentOperator}' to '{FormatTypeRef(targetType)}' and '{FormatTypeRef(valueType)}'.");
    }

    private bool IsTaggedUnionVariantAssignment(string targetType, string? sourceType)
    {
        if (_program is null || sourceType is null)
        {
            return false;
        }

        targetType = StripPointer(ResolveAlias(targetType));
        sourceType = ResolveAlias(sourceType);
        var taggedUnion = _program.TaggedUnions.FirstOrDefault(union =>
            !union.IsRaw
            && string.Equals(union.Name, targetType, StringComparison.Ordinal));
        return taggedUnion is not null
            && taggedUnion.Variants.Any(variant => SameTypeName(ResolveAlias(variant.Type), sourceType));
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

        var leftType = _expressionTypeResolver.Resolve(binary.Left, variables);
        var rightType = _expressionTypeResolver.Resolve(binary.Right, variables);
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
                    AnalyzeMutationTarget(postfix.Operand, postfix.Location, mutability);
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
                    AnalyzeAssignmentExpression(assignment, variables, mutability);
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

    private static bool DefinesFunction(ProgramNode program, string name) =>
        program.Functions.Any(function =>
            function.OwnerType is null
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

        if (ResolveCallSignature(call.Callee, call.TypeArguments, call.Arguments, variables) is { } signature)
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

        if (callee is MemberExpressionNode member)
        {
            return ResolveMemberCallSignature(member, typeArguments, arguments, variables);
        }

        var calleeType = _expressionTypeResolver.Resolve(callee, variables);
        if (TryParseFunctionType(calleeType, out var parameterTypes, out _, out var isVariadic))
        {
            return new CallSignature(callee.SourceText, parameterTypes.Select(ParseTypeRef).ToList(), isVariadic);
        }

        var name = GetQualifiedName(callee);
        if (name is null)
        {
            return null;
        }

        var function = _program.Functions.FirstOrDefault(function =>
            function.OwnerType is null
            && function.Name == name
            && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && _expressionTypeResolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) is not null)));
        if (function is not null)
        {
            var inferredArguments = typeArguments.Count > 0
                ? typeArguments
                : _expressionTypeResolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) ?? [];
            return BuildSignature(function.Name, function.TypeParameters, function.Parameters, inferredArguments, skipSelf: false);
        }

        var externFunction = _program.ExternFunctions.FirstOrDefault(function =>
            function.Name == name
            && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && _expressionTypeResolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) is not null)));
        return externFunction is null
            ? null
            : BuildSignature(
                externFunction.Name,
                externFunction.TypeParameters,
                externFunction.Parameters,
                typeArguments.Count > 0
                    ? typeArguments
                    : _expressionTypeResolver.InferFunctionTypeArguments(externFunction.TypeParameters, externFunction.Parameters, arguments, variables, skipSelf: false) ?? [],
                skipSelf: false);
    }

    private CallSignature? ResolveMemberCallSignature(
        MemberExpressionNode member,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        if (_program is null)
        {
            return null;
        }

        var targetName = GetQualifiedName(member.Target);
        if (targetName is null)
        {
            return null;
        }

        if (!variables.TryGetValue(targetName, out var targetType))
        {
            if (TryResolveStaticRequirementCall(targetName, member.MemberName) is { } requirementSignature)
            {
                return requirementSignature;
            }

            var staticFunction = _program.Functions.FirstOrDefault(function =>
                function.IsStatic
                && function.OwnerType is not null
                && targetName == function.OwnerType
                && function.Name == member.MemberName
                && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                    || (typeArguments.Count == 0
                        && function.TypeParameters.Count > 0
                        && _expressionTypeResolver?.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) is not null)));
            return staticFunction is null
                ? null
                : BuildSignature(
                    $"{targetName}.{member.MemberName}",
                    staticFunction.TypeParameters,
                    staticFunction.Parameters,
                    typeArguments.Count > 0
                        ? typeArguments
                        : _expressionTypeResolver?.InferFunctionTypeArguments(staticFunction.TypeParameters, staticFunction.Parameters, arguments, variables, skipSelf: false) ?? [],
                    skipSelf: false);
        }

        var normalizedType = GetGenericBaseName(StripPointer(ResolveAlias(targetType)));
        var receiverArguments = TryParseGenericUse(StripPointer(ResolveAlias(targetType)), out _, out var parsedReceiverArguments)
            ? parsedReceiverArguments
            : [];
        if (FindAdapterExpose(normalizedType, member.MemberName) is { } adapterExpose)
        {
            var baseType = SubstituteAdapterBaseType(adapterExpose.Adapter, receiverArguments);
            var baseArguments = TryParseGenericUse(baseType, out _, out var parsedBaseArguments)
                ? parsedBaseArguments
                : [];
            var baseOwner = GetGenericBaseName(baseType);
            var exposedFunction = _program.Functions.FirstOrDefault(function =>
                function.OwnerType is not null
                && !function.IsStatic
                && function.Name == adapterExpose.Expose.SourceName
                && string.Equals(function.OwnerType, baseOwner, StringComparison.Ordinal)
                && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                    || (typeArguments.Count == 0 && function.TypeParameters.Count == baseArguments.Count)
                    || (typeArguments.Count == 0
                        && function.TypeParameters.Count > 0
                        && _expressionTypeResolver?.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: true, baseArguments) is not null)));
            if (exposedFunction is not null)
            {
                var exposedGenericArguments = typeArguments.Count > 0
                    ? typeArguments
                    : baseArguments.Count == exposedFunction.TypeParameters.Count
                        ? baseArguments
                        : _expressionTypeResolver?.InferFunctionTypeArguments(exposedFunction.TypeParameters, exposedFunction.Parameters, arguments, variables, skipSelf: true, baseArguments) ?? [];
                return BuildSignature($"{normalizedType}.{member.MemberName}", exposedFunction.TypeParameters, exposedFunction.Parameters, exposedGenericArguments, skipSelf: true);
            }
        }

        var function = _program.Functions.FirstOrDefault(function =>
            function.OwnerType is not null
            && !function.IsStatic
            && function.Name == member.MemberName
            && string.Equals(function.OwnerType, normalizedType, StringComparison.Ordinal)
            && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                || (typeArguments.Count == 0 && function.TypeParameters.Count == receiverArguments.Count)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && _expressionTypeResolver?.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: true, receiverArguments) is not null)));
        if (function is null)
        {
            return null;
        }

        var genericArguments = typeArguments.Count > 0
            ? typeArguments
            : receiverArguments.Count == function.TypeParameters.Count
                ? receiverArguments
                : _expressionTypeResolver?.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: true, receiverArguments) ?? [];
        return BuildSignature($"{normalizedType}.{member.MemberName}", function.TypeParameters, function.Parameters, genericArguments, skipSelf: true);
    }

    private CallSignature? TryResolveStaticRequirementCall(string targetName, string memberName)
    {
        if (_program is null || !_currentTypeParameters.Contains(targetName, StringComparer.Ordinal))
        {
            return null;
        }

        foreach (var constraint in _currentGenericConstraints.Where(constraint =>
            string.Equals(constraint.TypeParameter, targetName, StringComparison.Ordinal)))
        {
            foreach (var reference in constraint.Requirements)
            {
                var requirement = _program.Requirements.FirstOrDefault(requirement =>
                    string.Equals(requirement.Name, reference.Name, StringComparison.Ordinal));
                if (requirement is null)
                {
                    continue;
                }

                var function = requirement.Members
                    .OfType<RequirementFunctionNode>()
                    .FirstOrDefault(function => function.IsStatic && function.Name == memberName);
                if (function is null)
                {
                    continue;
                }

                var arguments = reference.TypeArguments.Count == requirement.TypeParameters.Count
                    ? reference.TypeArguments
                    : requirement.TypeParameters.Count == 1
                        ? [targetName]
                        : [];
                var substitutions = requirement.TypeParameters.Count == arguments.Count
                    ? requirement.TypeParameters
                        .Zip(arguments)
                        .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                var parameterTypes = function.Parameters
                    .Where(parameter => !parameter.IsVariadic)
                    .Select(parameter => GenericTypeStringRewriter.Substitute(parameter.Type, substitutions))
                    .ToList();
                return new CallSignature($"{targetName}.{memberName}", parameterTypes.Select(ParseTypeRef).ToList(), IsVariadic: false);
            }
        }

        return null;
    }

    private sealed record AdapterExposeMatch(TypeAdapterNode Adapter, ExposeMethodNode Expose);

    private AdapterExposeMatch? FindAdapterExpose(string? adapterName, string exposedName)
    {
        if (_program is null || adapterName is null)
        {
            return null;
        }

        foreach (var adapter in _program.TypeAdapters.Where(adapter => adapter.Name == adapterName))
        {
            var expose = adapter.ExposedMethods.FirstOrDefault(expose => expose.ExposedName == exposedName);
            if (expose is not null)
            {
                return new AdapterExposeMatch(adapter, expose);
            }
        }

        return null;
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
        return GenericTypeStringRewriter.Substitute(adapter.BaseType, substitutions);
    }

    private CallSignature BuildSignature(
        string name,
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<ParameterNode> parameters,
        IReadOnlyList<string> typeArguments,
        bool skipSelf)
    {
        var substitutions = typeParameters.Count == typeArguments.Count
            ? typeParameters.Zip(typeArguments).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var filteredParameters = parameters
            .Skip(skipSelf ? 1 : 0)
            .ToList();
        var isVariadic = filteredParameters.Any(parameter => parameter.IsVariadic);
        var parameterTypes = filteredParameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => GenericTypeStringRewriter.Substitute(parameter.Type, substitutions))
            .Select(ParseTypeRef)
            .ToList();
        return new CallSignature(name, parameterTypes, isVariadic);
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

    private static bool IsPointerType(string type) =>
        type.TrimEnd().EndsWith("*", StringComparison.Ordinal);

    private static bool IsPointerType(TypeRef type) =>
        UnwrapAlias(type) is TypeRef.Pointer;

    private bool IsNumericLikeType(string type)
    {
        type = StripConst(StripPointerSuffix(ResolveAlias(type)).Trim());
        return type is
            "char" or
            "signed char" or
            "unsigned char" or
            "short" or
            "unsigned short" or
            "int" or
            "unsigned int" or
            "long" or
            "unsigned long" or
            "long long" or
            "unsigned long long" or
            "int8_t" or
            "uint8_t" or
            "int16_t" or
            "uint16_t" or
            "int32_t" or
            "uint32_t" or
            "int64_t" or
            "uint64_t" or
            "float" or
            "double" or
            "long double" or
            "size_t" or
            "usize" or
            "u8" or
            "u16" or
            "u32" or
            "u64" or
            "clock_t" or
            "bool";
    }

    private bool IsNumericLikeType(TypeRef type)
    {
        var unwrapped = UnwrapAlias(type);
        return unwrapped is TypeRef.Named named
            && IsNumericLikeType(named.Name);
    }

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

    private static string StripConst(string type) =>
        type.StartsWith("const ", StringComparison.Ordinal)
            ? type["const ".Length..].TrimStart()
            : type;

    private static string StripPointerSuffix(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

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

    private static IReadOnlyList<GenericStructUse> FindGenericStructUses(string type)
    {
        var uses = new List<GenericStructUse>();

        for (var i = 0; i < type.Length; i++)
        {
            if (!IsIdentifierStart(type[i]))
            {
                continue;
            }

            var nameStart = i;
            while (i < type.Length && IsIdentifierPart(type[i]))
            {
                i++;
            }

            if (i >= type.Length || type[i] != '<')
            {
                continue;
            }

            var close = FindMatchingGenericClose(type, i);
            if (close < 0)
            {
                continue;
            }

            var name = type[nameStart..i];
            var arguments = SplitGenericArguments(type[(i + 1)..close]);
            uses.Add(new GenericStructUse(name, arguments));
            foreach (var argument in arguments)
            {
                uses.AddRange(FindGenericStructUses(argument));
            }

            i = close;
        }

        return uses;
    }

    private static IReadOnlyList<string> FindTypeNames(string type)
    {
        type = StripArraySuffix(StripPointer(type.Trim()));
        if (type.Length == 0)
        {
            return [];
        }

        if (TryParseFunctionType(type, out var parameterTypes, out var returnType, out _))
        {
            return parameterTypes
                .SelectMany(FindTypeNames)
                .Concat(FindTypeNames(returnType))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (TryParseGenericUse(type, out var genericName, out var arguments))
        {
            return new[] { NormalizeTypeName(genericName) }
                .Concat(arguments.SelectMany(FindTypeNames))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var normalized = NormalizeTypeName(type);
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : [normalized];
    }

    private static string NormalizeTypeName(string type)
    {
        type = StripArraySuffix(StripPointer(type.Trim()));
        if (type.StartsWith("const ", StringComparison.Ordinal))
        {
            type = type["const ".Length..].TrimStart();
        }

        return IsBuiltInTypeName(type) ? string.Empty : type;
    }

    private static string StripArraySuffix(string type)
    {
        while (type.EndsWith("]", StringComparison.Ordinal))
        {
            var open = type.LastIndexOf('[');
            if (open < 0)
            {
                break;
            }

            type = type[..open].TrimEnd();
        }

        return type;
    }

    private static int FindMatchingGenericClose(string type, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < type.Length; i++)
        {
            if (type[i] == '<')
            {
                depth++;
                continue;
            }

            if (type[i] != '>')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return i;
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

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private sealed record CallSignature(
        string Name,
        IReadOnlyList<TypeRef> ParameterTypes,
        bool IsVariadic);

    private sealed record GenericStructUse(string Name, IReadOnlyList<string> Arguments);
}
