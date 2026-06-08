using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

public sealed class RequirementMatcher
{
    private readonly ProgramNode _program;
    private readonly TypeRefParser _typeRefParser;
    private readonly TypeResolver _typeResolver;
    private readonly ResolvedTypeMemberResolver _memberResolver;
    private readonly IReadOnlyDictionary<string, StructNode> _concreteStructs;
    private readonly IReadOnlyDictionary<string, string> _typeAliases;

    public RequirementMatcher(ProgramNode program, IReadOnlyList<StructNode>? concreteStructs = null)
    {
        _program = program;
        _typeRefParser = new TypeRefParser(program);
        _typeResolver = new TypeResolver(program);
        _memberResolver = new ResolvedTypeMemberResolver(program);
        _concreteStructs = (concreteStructs ?? [])
            .Concat(program.Structs.Where(structNode => structNode.TypeParameters.Count == 0))
            .GroupBy(structNode => structNode.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _typeAliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TypeText(group.First().TargetTypeNode), StringComparer.Ordinal);
    }

    public RequirementMatch Match(
        string concreteType,
        string requirementName,
        IReadOnlyList<string>? requirementArguments = null)
        => Match(concreteType, requirementName, requirementArguments, new HashSet<string>(StringComparer.Ordinal));

    private RequirementMatch Match(
        string concreteType,
        string requirementName,
        IReadOnlyList<string>? requirementArguments,
        HashSet<string> activeMatches)
    {
        var requirement = _program.Requirements.FirstOrDefault(requirement => requirement.Name == requirementName);
        if (requirement is null)
        {
            var interfaceNode = _program.Interfaces.FirstOrDefault(interfaceNode => interfaceNode.Name == requirementName);
            if (interfaceNode is not null)
            {
                return MatchInterface(concreteType, interfaceNode, requirementArguments);
            }

            return RequirementMatch.Failed(concreteType, requirementName, [$"Unknown requirement '{requirementName}'."]);
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Self"] = NormalizeSelfType(concreteType),
        };
        var matchKey = $"{bindings["Self"]}:{requirementName}<{string.Join(",", requirementArguments ?? [])}>";
        if (!activeMatches.Add(matchKey))
        {
            return RequirementMatch.Succeeded(concreteType, requirementName, bindings);
        }

        requirementArguments ??= [];
        for (var i = 0; i < requirementArguments.Count && i < requirement.TypeParameters.Count; i++)
        {
            var argument = ResolveAlias(requirementArguments[i]);
            if (string.Equals(argument, requirement.TypeParameters[i], StringComparison.Ordinal))
            {
                continue;
            }

            bindings[requirement.TypeParameters[i]] = argument;
        }

        var hasStructMembers = requirement.Members.Any(member => member is RequirementFieldNode);
        var hasStructType = TryResolveStructMembers(concreteType, out var fields);
        if (hasStructMembers && !hasStructType)
        {
            activeMatches.Remove(matchKey);
            return RequirementMatch.Failed(
                concreteType,
                requirementName,
                [$"Type '{concreteType}' is not a known struct type."]);
        }

        var failures = new List<string>();
        foreach (var member in requirement.Members)
        {
            switch (member)
            {
                case RequirementFieldNode field:
                    if (!hasStructType)
                    {
                        failures.Add($"Missing field '{field.Name}: {Substitute(TypeText(field.TypeNode), bindings)}'.");
                        break;
                    }

                    MatchField(field, fields, bindings, failures);
                    break;
                case RequirementFunctionNode function:
                    MatchFunction(function, bindings, failures);
                    break;
            }
        }

        if (failures.Count == 0)
        {
            MatchRequirementConstraints(requirement, bindings, failures, activeMatches);
        }

        activeMatches.Remove(matchKey);

        return failures.Count == 0
            ? RequirementMatch.Succeeded(concreteType, requirementName, bindings)
            : RequirementMatch.Failed(concreteType, requirementName, failures, bindings);
    }

    private RequirementMatch MatchInterface(
        string concreteType,
        InterfaceNode interfaceNode,
        IReadOnlyList<string>? requirementArguments)
    {
        if (requirementArguments is { Count: > 0 })
        {
            return RequirementMatch.Failed(
                concreteType,
                interfaceNode.Name,
                [$"Interface '{interfaceNode.Name}' does not take type arguments."]);
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Self"] = NormalizeSelfType(concreteType),
        };
        var failures = new List<string>();

        foreach (var method in interfaceNode.Methods)
        {
            MatchInterfaceMethod(method, bindings, failures);
        }

        return failures.Count == 0
            ? RequirementMatch.Succeeded(concreteType, interfaceNode.Name, bindings)
            : RequirementMatch.Failed(concreteType, interfaceNode.Name, failures, bindings);
    }

    private void MatchInterfaceMethod(
        InterfaceMethodNode interfaceMethod,
        Dictionary<string, string> bindings,
        List<string> failures)
    {
        var ownerType = bindings["Self"];
        var expectedParameterCount = interfaceMethod.Parameters.Count + 1;
        var method = ResolveMethods(ownerType)
            .FirstOrDefault(candidate =>
                !candidate.Declaration.IsStatic
                && candidate.Name == interfaceMethod.Name
                && candidate.ParameterTypes.Count == expectedParameterCount);

        if (method is null)
        {
            failures.Add($"Missing method '{interfaceMethod.Name}' with receiver 'Self*'.");
            return;
        }

        var candidateBindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
        var receiver = method.ParameterTypes[0];
        if (!Unify("Self*", TypeRefFormatter.ToCxString(receiver), candidateBindings))
        {
            failures.Add(
                $"Method '{interfaceMethod.Name}' receiver has type '{TypeRefFormatter.ToCxString(receiver)}', expected '{Substitute("Self*", bindings)}'.");
        }

        for (var i = 0; i < interfaceMethod.Parameters.Count; i++)
        {
            var expected = interfaceMethod.Parameters[i];
            var actual = method.ParameterTypes[i + 1];
            var expectedType = TypeText(expected.TypeNode);
            if (!Unify(expectedType, TypeRefFormatter.ToCxString(actual), candidateBindings))
            {
                failures.Add(
                    $"Method '{interfaceMethod.Name}' parameter {i + 1} has type '{TypeRefFormatter.ToCxString(actual)}', expected '{Substitute(expectedType, bindings)}'.");
            }
        }

        var expectedReturnType = TypeText(interfaceMethod.ReturnTypeNode);
        if (!Unify(expectedReturnType, TypeRefFormatter.ToCxString(method.ReturnType), candidateBindings))
        {
            failures.Add(
                $"Method '{interfaceMethod.Name}' returns '{TypeRefFormatter.ToCxString(method.ReturnType)}', expected '{Substitute(expectedReturnType, bindings)}'.");
        }
    }

    private void MatchRequirementConstraints(
        RequirementNode requirement,
        Dictionary<string, string> bindings,
        List<string> failures,
        HashSet<string> activeMatches)
    {
        foreach (var constraint in requirement.GenericConstraints)
        {
            if (!bindings.TryGetValue(constraint.TypeParameter, out var constrainedType))
            {
                failures.Add($"Could not infer type parameter '{constraint.TypeParameter}' required by where clause.");
                continue;
            }

            foreach (var required in constraint.Requirements)
            {
                var arguments = TypeArguments(required.TypeArgumentNodes)
                    .Select(argument => Substitute(argument, bindings))
                    .ToList();
                var match = Match(constrainedType, required.Name, arguments, activeMatches);
                if (match.Success)
                {
                    MergeBindings(bindings, match.TypeBindings);
                    continue;
                }

                failures.Add(
                    $"Where clause requires '{constraint.TypeParameter}: {required.Name}{FormatTypeArguments(arguments)}' but '{constrainedType}' does not satisfy it: {string.Join(" ", match.Failures)}");
            }
        }
    }

    private static string FormatTypeArguments(IReadOnlyList<string> arguments) =>
        arguments.Count == 0
            ? string.Empty
            : "<" + string.Join(", ", arguments) + ">";

    public string ResolveAlias(string type)
    {
        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (_typeAliases.TryGetValue(type, out var targetType) && seen.Add(type))
        {
            type = targetType;
        }

        return type + pointerSuffix;
    }

    private void MatchField(
        RequirementFieldNode field,
        IReadOnlyList<ResolvedField> fields,
        Dictionary<string, string> bindings,
        List<string> failures)
    {
        var actualField = fields.FirstOrDefault(candidate => candidate.Name == field.Name);
        var expectedFieldType = TypeText(field.TypeNode);
        if (actualField is null)
        {
            failures.Add($"Missing field '{field.Name}: {Substitute(expectedFieldType, bindings)}'.");
            return;
        }

        if (!Unify(expectedFieldType, TypeRefFormatter.ToCxString(actualField.Type), bindings))
        {
            failures.Add(
                $"Field '{field.Name}' has type '{TypeRefFormatter.ToCxString(actualField.Type)}', expected '{Substitute(expectedFieldType, bindings)}'.");
        }
    }

    private void MatchFunction(
        RequirementFunctionNode function,
        Dictionary<string, string> bindings,
        List<string> failures)
    {
        if (function.IsStatic)
        {
            MatchStaticFunction(function, bindings, failures);
            return;
        }

        var ownerType = bindings["Self"];
        var method = ResolveMethods(ownerType)
            .FirstOrDefault(candidate =>
                !candidate.Declaration.IsStatic
                && candidate.Name == function.Name
                && candidate.ParameterTypes.Count == function.Parameters.Count);

        if (method is null)
        {
            var freeFunction = _program.Functions.FirstOrDefault(candidate =>
                OwnerType(candidate) is null
                && !candidate.IsStatic
                && candidate.Name == function.Name
                && candidate.Parameters.Count == function.Parameters.Count
                && FunctionMatches(candidate, function, bindings));

            if (freeFunction is null)
            {
                failures.Add($"Missing function '{function.Name}'.");
            }

            return;
        }

        var candidateBindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
        var failureStart = failures.Count;
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var actualType = TypeRefFormatter.ToCxString(method.ParameterTypes[i]);
            var expectedType = TypeText(function.Parameters[i].TypeNode);
            if (!Unify(expectedType, actualType, candidateBindings))
            {
                failures.Add(
                    $"Method '{function.Name}' parameter {i + 1} has type '{actualType}', expected '{Substitute(expectedType, bindings)}'.");
            }
        }

        var actualReturnType = TypeRefFormatter.ToCxString(method.ReturnType);
        var expectedReturnType = TypeText(function.ReturnTypeNode);
        if (!Unify(expectedReturnType, actualReturnType, candidateBindings))
        {
            failures.Add(
                $"Method '{function.Name}' returns '{actualReturnType}', expected '{Substitute(expectedReturnType, bindings)}'.");
        }

        if (failures.Count == failureStart)
        {
            MergeBindings(bindings, candidateBindings);
        }
    }

    private void MatchStaticFunction(
        RequirementFunctionNode function,
        Dictionary<string, string> bindings,
        List<string> failures)
    {
        var ownerType = bindings["Self"];
        var method = ResolveMethods(ownerType)
            .FirstOrDefault(candidate =>
                candidate.Declaration.IsStatic
                && candidate.Name == function.Name
                && candidate.ParameterTypes.Count == function.Parameters.Count);

        if (method is null)
        {
            failures.Add($"Missing static function '{function.Name}'.");
            return;
        }

        var candidateBindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
        var failureStart = failures.Count;
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var actualType = TypeRefFormatter.ToCxString(method.ParameterTypes[i]);
            var expectedType = TypeText(function.Parameters[i].TypeNode);
            if (!Unify(expectedType, actualType, candidateBindings))
            {
                failures.Add(
                    $"Static method '{function.Name}' parameter {i + 1} has type '{actualType}', expected '{Substitute(expectedType, bindings)}'.");
            }
        }

        var actualReturnType = TypeRefFormatter.ToCxString(method.ReturnType);
        var expectedReturnType = TypeText(function.ReturnTypeNode);
        if (!Unify(expectedReturnType, actualReturnType, candidateBindings))
        {
            failures.Add(
                $"Static method '{function.Name}' returns '{actualReturnType}', expected '{Substitute(expectedReturnType, bindings)}'.");
        }

        if (failures.Count == failureStart)
        {
            MergeBindings(bindings, candidateBindings);
        }
    }

    private static void MergeBindings(
        Dictionary<string, string> target,
        IReadOnlyDictionary<string, string> source)
    {
        foreach (var (name, value) in source)
        {
            target[name] = value;
        }
    }

    private Dictionary<string, string> BuildCandidateBindings(
        FunctionNode candidate,
        string ownerType,
        IReadOnlyDictionary<string, string> currentBindings)
    {
        var bindings = new Dictionary<string, string>(currentBindings, StringComparer.Ordinal);
        var candidateOwnerType = OwnerType(candidate);
        if (candidateOwnerType is null)
        {
            return bindings;
        }

        var normalizedOwnerType = StripPointer(ResolveAlias(ownerType));
        if (!TryParseGenericUse(normalizedOwnerType, out var ownerName, out var ownerArguments)
            || !string.Equals(ownerName, candidateOwnerType, StringComparison.Ordinal))
        {
            return bindings;
        }

        var ownerDefinition = _program.Structs.FirstOrDefault(structNode =>
            string.Equals(structNode.Name, candidateOwnerType, StringComparison.Ordinal)
            && structNode.TypeParameters.Count == ownerArguments.Count);
        if (ownerDefinition is null)
        {
            return bindings;
        }

        foreach (var (parameter, argument) in ownerDefinition.TypeParameters.Zip(ownerArguments))
        {
            bindings[parameter] = Substitute(ResolveAlias(argument), bindings);
        }

        return bindings;
    }

    private bool FunctionMatches(
        FunctionNode candidate,
        RequirementFunctionNode requirement,
        IReadOnlyDictionary<string, string> currentBindings)
    {
        var bindings = new Dictionary<string, string>(currentBindings, StringComparer.Ordinal);
        foreach (var parameter in candidate.TypeParameters)
        {
            bindings.Remove(parameter);
        }

        for (var i = 0; i < requirement.Parameters.Count; i++)
        {
            if (!Unify(TypeText(requirement.Parameters[i].TypeNode), TypeText(candidate.Parameters[i].TypeNode), bindings))
            {
                return false;
            }
        }

        return Unify(TypeText(requirement.ReturnTypeNode), TypeText(candidate.ReturnTypeNode), bindings);
    }

    private bool TryResolveStruct(string type, out StructNode structNode)
    {
        var resolvedType = StripPointer(ResolveAlias(type));
        var loweredType = LowerType(resolvedType);
        if (_concreteStructs.TryGetValue(loweredType, out structNode!))
        {
            return true;
        }

        if (!TryParseGenericUse(resolvedType, out var genericName, out var arguments))
        {
            structNode = null!;
            return false;
        }

        var definition = _program.Structs.FirstOrDefault(structNode =>
            !structNode.IsHeaderDeclaration
            &&
            structNode.Name == genericName
            && structNode.TypeParameters.Count == arguments.Count);
        if (definition is null)
        {
            structNode = null!;
            return false;
        }

        var substitutions = definition.TypeParameters
            .Zip(arguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var fields = definition.Fields
            .Select(field =>
            {
                var substitutedType = Substitute(TypeText(field.TypeNode), substitutions);
                return new StructFieldNode(
                    field.Location,
                    field.Name,
                    field.Attributes,
                    new TypeNode(field.Location, substitutedType, TypeSyntaxParser.Parse(substitutedType)));
            })
            .ToList();

        structNode = new StructNode(definition.Location, LowerType(resolvedType), [], [], [], fields, [], definition.Attributes);
        return true;
    }

    private bool TryResolveStructMembers(string type, out IReadOnlyList<ResolvedField> fields)
    {
        var resolvedType = ResolveConcreteDefinition(type);
        if (resolvedType.Symbol is TypeSymbol.Struct)
        {
            fields = _memberResolver.GetFields(resolvedType);
            return true;
        }

        if (TryResolveStruct(type, out var structNode))
        {
            fields = structNode.Fields
                .Select(field => new ResolvedField(field.Name, field.TypeNode?.Semantic.Type ?? _typeRefParser.Parse(TypeText(field.TypeNode)), field))
                .ToList();
            return true;
        }

        fields = [];
        return false;
    }

    private ResolvedType ResolveConcreteDefinition(string type)
    {
        var pointerDepth = 0;
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerDepth++;
            type = type[..^1].TrimEnd();
        }

        var resolved = _typeResolver.ResolveDefinition(_typeRefParser.Parse(type));
        for (var i = 0; i < pointerDepth; i++)
        {
            resolved = resolved with { Type = new TypeRef.Pointer(resolved.Type), Symbol = null, Substitutions = new Dictionary<string, TypeRef>(StringComparer.Ordinal) };
        }

        return resolved;
    }

    private IReadOnlyList<ResolvedMethod> ResolveMethods(string type)
    {
        var resolvedType = ResolveConcreteDefinition(type);
        return _memberResolver.GetMethods(resolvedType);
    }

    private string NormalizeSelfType(string type)
    {
        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var resolved = _typeResolver.ResolveDefinition(_typeRefParser.Parse(type));
        return TypeRefFormatter.ToCxString(resolved.Type) + pointerSuffix;
    }

    private bool Unify(string expectedPattern, string actualType, Dictionary<string, string> bindings)
    {
        expectedPattern = Substitute(ResolveAlias(expectedPattern), bindings);
        actualType = Substitute(ResolveAlias(actualType), bindings);

        if (bindings.ContainsKey(expectedPattern))
        {
            return SameType(bindings[expectedPattern], actualType);
        }

        var unboundParameter = _program.Requirements
            .SelectMany(requirement => requirement.TypeParameters)
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(parameter => expectedPattern == parameter);
        if (unboundParameter is not null)
        {
            bindings[unboundParameter] = actualType;
            return true;
        }

        if (expectedPattern.EndsWith("*", StringComparison.Ordinal)
            && actualType.EndsWith("*", StringComparison.Ordinal))
        {
            return Unify(expectedPattern[..^1].TrimEnd(), actualType[..^1].TrimEnd(), bindings);
        }

        if (TryParseGenericUse(expectedPattern, out var expectedName, out var expectedArguments)
            && TryParseGenericUse(actualType, out var actualName, out var actualArguments)
            && expectedName == actualName
            && expectedArguments.Count == actualArguments.Count)
        {
            for (var i = 0; i < expectedArguments.Count; i++)
            {
                if (!Unify(expectedArguments[i], actualArguments[i], bindings))
                {
                    return false;
                }
            }

            return true;
        }

        return SameType(expectedPattern, actualType);
    }

    private bool IsCandidateOwner(FunctionNode candidate, string ownerType)
    {
        var candidateOwnerType = OwnerType(candidate);
        if (candidateOwnerType is null)
        {
            return false;
        }

        var normalizedOwnerType = StripPointer(ResolveAlias(ownerType));
        var ownerBaseName = GetGenericBaseName(normalizedOwnerType);
        return string.Equals(candidateOwnerType, ownerBaseName, StringComparison.Ordinal)
            || SameType(candidateOwnerType, normalizedOwnerType);
    }

    private static string Substitute(string type, IReadOnlyDictionary<string, string> bindings)
        => GenericTypeStringRewriter.Substitute(type, bindings);

    private bool SameType(string left, string right) =>
        LowerType(ResolveAlias(left)) == LowerType(ResolveAlias(right));

    private static string StripPointer(string type)
    {
        while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
        {
            type = type.TrimEnd()[..^1];
        }

        return type.TrimEnd();
    }

    private static string LowerType(string type)
    {
        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        if (!TryParseGenericUse(type, out var name, out var arguments))
        {
            return type + pointerSuffix;
        }

        return $"{name}_{string.Join("_", arguments.Select(LowerType).Select(SanitizeTypeName))}{pointerSuffix}";
    }

    private static string SanitizeTypeName(string type) =>
        type
            .Replace("*", "_ptr", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("<", "_", StringComparison.Ordinal)
            .Replace(">", "", StringComparison.Ordinal)
            .Replace(",", "_", StringComparison.Ordinal);

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

        name = type[..genericStart];
        arguments = SplitGenericArguments(type[(genericStart + 1)..genericEnd]);
        return true;
    }

    private static string GetGenericBaseName(string type)
    {
        type = type.Trim();
        var genericStart = type.IndexOf('<', StringComparison.Ordinal);
        return genericStart < 0
            ? type
            : type[..genericStart].Trim();
    }

    private static IReadOnlyList<string> SplitGenericArguments(string argumentsText)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return [];
        }

        var arguments = new List<string>();
        var start = 0;
        var depth = 0;

        for (var i = 0; i < argumentsText.Length; i++)
        {
            depth += argumentsText[i] switch
            {
                '<' => 1,
                '>' => -1,
                _ => 0,
            };

            if (argumentsText[i] != ',' || depth != 0)
            {
                continue;
            }

            arguments.Add(argumentsText[start..i].Trim());
            start = i + 1;
        }

        arguments.Add(argumentsText[start..].Trim());
        return arguments;
    }

    private static string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

    private static IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> nodes) =>
        nodes.Select(TypeText).ToList();

    private static string TypeText(TypeNode? typeNode) => typeNode?.TypeName ?? string.Empty;

    private static string? TypeTextOrNull(TypeNode? typeNode)
    {
        var type = TypeText(typeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }
}

public sealed record RequirementMatch(
    bool Success,
    string ConcreteType,
    string RequirementName,
    IReadOnlyDictionary<string, string> TypeBindings,
    IReadOnlyList<string> Failures)
{
    public static RequirementMatch Succeeded(
        string concreteType,
        string requirementName,
        IReadOnlyDictionary<string, string> typeBindings) =>
        new(true, concreteType, requirementName, typeBindings, []);

    public static RequirementMatch Failed(
        string concreteType,
        string requirementName,
        IReadOnlyList<string> failures,
        IReadOnlyDictionary<string, string>? typeBindings = null) =>
        new(false, concreteType, requirementName, typeBindings ?? new Dictionary<string, string>(), failures);
}
