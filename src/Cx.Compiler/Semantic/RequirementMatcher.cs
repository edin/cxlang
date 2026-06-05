using System.Text.RegularExpressions;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

public sealed class RequirementMatcher
{
    private readonly ProgramNode _program;
    private readonly IReadOnlyDictionary<string, StructNode> _concreteStructs;
    private readonly IReadOnlyDictionary<string, string> _typeAliases;

    public RequirementMatcher(ProgramNode program, IReadOnlyList<StructNode>? concreteStructs = null)
    {
        _program = program;
        _concreteStructs = (concreteStructs ?? [])
            .Concat(program.Structs.Where(structNode => structNode.TypeParameters.Count == 0))
            .GroupBy(structNode => structNode.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _typeAliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().TargetType, StringComparer.Ordinal);
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
            ["Self"] = StripPointer(ResolveAlias(concreteType)),
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
        var hasStructType = TryResolveStruct(concreteType, out var structNode);
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
                        failures.Add($"Missing field '{field.Name}: {Substitute(field.Type, bindings)}'.");
                        break;
                    }

                    MatchField(field, structNode, bindings, failures);
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
            ["Self"] = StripPointer(ResolveAlias(concreteType)),
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
        var method = _program.Functions.FirstOrDefault(candidate =>
            IsCandidateOwner(candidate, ownerType)
            && !candidate.IsStatic
            && candidate.Name == interfaceMethod.Name
            && candidate.Parameters.Count == expectedParameterCount);

        if (method is null)
        {
            failures.Add($"Missing method '{interfaceMethod.Name}' with receiver 'Self*'.");
            return;
        }

        var candidateBindings = BuildCandidateBindings(method, ownerType, bindings);
        var receiver = method.Parameters[0];
        if (!Unify("Self*", receiver.Type, candidateBindings))
        {
            failures.Add(
                $"Method '{interfaceMethod.Name}' receiver has type '{Substitute(receiver.Type, candidateBindings)}', expected '{Substitute("Self*", bindings)}'.");
        }

        for (var i = 0; i < interfaceMethod.Parameters.Count; i++)
        {
            var expected = interfaceMethod.Parameters[i];
            var actual = method.Parameters[i + 1];
            if (!Unify(expected.Type, actual.Type, candidateBindings))
            {
                failures.Add(
                    $"Method '{interfaceMethod.Name}' parameter {i + 1} has type '{Substitute(actual.Type, candidateBindings)}', expected '{Substitute(expected.Type, bindings)}'.");
            }
        }

        if (!Unify(interfaceMethod.ReturnType, method.ReturnType, candidateBindings))
        {
            failures.Add(
                $"Method '{interfaceMethod.Name}' returns '{Substitute(method.ReturnType, candidateBindings)}', expected '{Substitute(interfaceMethod.ReturnType, bindings)}'.");
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
                var arguments = required.TypeArguments
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
        StructNode structNode,
        Dictionary<string, string> bindings,
        List<string> failures)
    {
        var actualField = structNode.Fields.FirstOrDefault(candidate => candidate.Name == field.Name);
        if (actualField is null)
        {
            failures.Add($"Missing field '{field.Name}: {Substitute(field.Type, bindings)}'.");
            return;
        }

        if (!Unify(field.Type, actualField.Type, bindings))
        {
            failures.Add(
                $"Field '{field.Name}' has type '{actualField.Type}', expected '{Substitute(field.Type, bindings)}'.");
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
        var method = _program.Functions.FirstOrDefault(candidate =>
            IsCandidateOwner(candidate, ownerType)
            && !candidate.IsStatic
            && candidate.Name == function.Name
            && candidate.Parameters.Count == function.Parameters.Count);

        if (method is null)
        {
            var freeFunction = _program.Functions.FirstOrDefault(candidate =>
                candidate.OwnerType is null
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

        var candidateBindings = BuildCandidateBindings(method, ownerType, bindings);
        var failureStart = failures.Count;
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            if (!Unify(function.Parameters[i].Type, method.Parameters[i].Type, candidateBindings))
            {
                failures.Add(
                    $"Method '{function.Name}' parameter {i + 1} has type '{Substitute(method.Parameters[i].Type, candidateBindings)}', expected '{Substitute(function.Parameters[i].Type, bindings)}'.");
            }
        }

        if (!Unify(function.ReturnType, method.ReturnType, candidateBindings))
        {
            failures.Add(
                $"Method '{function.Name}' returns '{Substitute(method.ReturnType, candidateBindings)}', expected '{Substitute(function.ReturnType, bindings)}'.");
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
        var method = _program.Functions.FirstOrDefault(candidate =>
            IsCandidateOwner(candidate, ownerType)
            && candidate.IsStatic
            && candidate.Name == function.Name
            && candidate.Parameters.Count == function.Parameters.Count);

        if (method is null)
        {
            failures.Add($"Missing static function '{function.Name}'.");
            return;
        }

        var candidateBindings = BuildCandidateBindings(method, ownerType, bindings);
        var failureStart = failures.Count;
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            if (!Unify(function.Parameters[i].Type, method.Parameters[i].Type, candidateBindings))
            {
                failures.Add(
                    $"Static method '{function.Name}' parameter {i + 1} has type '{Substitute(method.Parameters[i].Type, candidateBindings)}', expected '{Substitute(function.Parameters[i].Type, bindings)}'.");
            }
        }

        if (!Unify(function.ReturnType, method.ReturnType, candidateBindings))
        {
            failures.Add(
                $"Static method '{function.Name}' returns '{Substitute(method.ReturnType, candidateBindings)}', expected '{Substitute(function.ReturnType, bindings)}'.");
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
        if (candidate.OwnerType is null)
        {
            return bindings;
        }

        var normalizedOwnerType = StripPointer(ResolveAlias(ownerType));
        if (!TryParseGenericUse(normalizedOwnerType, out var ownerName, out var ownerArguments)
            || !string.Equals(ownerName, candidate.OwnerType, StringComparison.Ordinal))
        {
            return bindings;
        }

        var ownerDefinition = _program.Structs.FirstOrDefault(structNode =>
            string.Equals(structNode.Name, candidate.OwnerType, StringComparison.Ordinal)
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
            if (!Unify(requirement.Parameters[i].Type, candidate.Parameters[i].Type, bindings))
            {
                return false;
            }
        }

        return Unify(requirement.ReturnType, candidate.ReturnType, bindings);
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
            .Select(field => new StructFieldNode(field.Location, field.Name, Substitute(field.Type, substitutions), field.Attributes))
            .ToList();

        structNode = new StructNode(definition.Location, LowerType(resolvedType), [], [], [], fields, [], definition.Attributes);
        return true;
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
        if (candidate.OwnerType is null)
        {
            return false;
        }

        var normalizedOwnerType = StripPointer(ResolveAlias(ownerType));
        var ownerBaseName = GetGenericBaseName(normalizedOwnerType);
        return string.Equals(candidate.OwnerType, ownerBaseName, StringComparison.Ordinal)
            || SameType(candidate.OwnerType, normalizedOwnerType);
    }

    private static string Substitute(string type, IReadOnlyDictionary<string, string> bindings)
    {
        foreach (var (name, value) in bindings)
        {
            type = Regex.Replace(type, $@"\b{Regex.Escape(name)}\b", value);
        }

        return type;
    }

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
