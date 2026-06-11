using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed record CallResolution(
    string Name,
    TypeRef ReturnType,
    IReadOnlyList<TypeRef> ParameterTypes,
    bool IsVariadic,
    FunctionNode? Function = null,
    IReadOnlyList<string>? TypeArguments = null,
    bool IsInstance = false);

internal sealed class CallResolver(
    ProgramNode program,
    Func<ExpressionNode, IReadOnlyDictionary<string, string>, string?> resolveExpressionType,
    IReadOnlyList<string>? currentTypeParameters = null,
    IReadOnlyList<GenericConstraintNode>? currentGenericConstraints = null)
{
    private readonly IReadOnlyList<string> _currentTypeParameters = currentTypeParameters ?? [];
    private readonly IReadOnlyList<GenericConstraintNode> _currentGenericConstraints = currentGenericConstraints ?? [];
    private readonly TypeRefParser _typeRefParser = new(program);
    private readonly TypeSyntaxTypeRefConverter _typeSyntaxConverter = new(program);
    private readonly MethodCallResolver _methodCallResolver = new(program, new TypeSystem(program, currentTypeParameters));

    public CallResolution? Resolve(
        ExpressionNode callee,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        if (callee is MemberExpressionNode member)
        {
            return ResolveMemberCall(member, typeArguments, arguments, variables);
        }

        var name = GetQualifiedName(callee);
        if (name is not null && !variables.ContainsKey(name))
        {
            if (ResolveFunction(name, typeArguments, arguments, variables) is { } directFunctionResolution)
            {
                return directFunctionResolution;
            }

            if (ResolveExternFunction(name, typeArguments, arguments, variables) is { } externResolution)
            {
                return externResolution;
            }
        }

        var calleeType = resolveExpressionType(callee, variables);
        if (Parse(calleeType) is TypeRef.Function functionPointer)
        {
            return new CallResolution(
                callee.SourceText,
                functionPointer.ReturnType,
                functionPointer.Parameters,
                functionPointer.IsVariadic);
        }

        if (name is null)
        {
            return null;
        }

        if (ResolveFunction(name, typeArguments, arguments, variables) is { } functionResolution)
        {
            return functionResolution;
        }

        return ResolveExternFunction(name, typeArguments, arguments, variables);
    }

    private CallResolution? ResolveMemberCall(
        MemberExpressionNode member,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        var targetName = GetQualifiedName(member.Target);
        if (targetName is null)
        {
            return null;
        }

        if (!variables.TryGetValue(targetName, out var targetType))
        {
            if (ResolveStaticRequirementCall(targetName, member.MemberName) is { } requirementCall)
            {
                return requirementCall;
            }

            if (_methodCallResolver.Resolve(member, typeArguments, arguments.Count, variables) is { SkipSelf: false } staticMethodCall)
            {
                return BuildMethodResolution(
                    staticMethodCall,
                    typeArguments,
                    BuildStaticReceiverType(targetName, typeArguments));
            }

            var staticFunction = program.Functions.FirstOrDefault(function =>
                function.IsStatic
                && OwnerType(function) is not null
                && string.Equals(targetName, OwnerType(function), StringComparison.Ordinal)
                && string.Equals(function.Name, member.MemberName, StringComparison.Ordinal)
                && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                    || (typeArguments.Count == 0
                        && function.TypeParameters.Count > 0
                        && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) is not null)));
            if (staticFunction is null)
            {
                return null;
            }

            var staticArguments = typeArguments.Count > 0
                ? typeArguments
                : InferFunctionTypeArguments(staticFunction.TypeParameters, staticFunction.Parameters, arguments, variables, skipSelf: false) ?? [];
            return BuildFunctionResolution(
                $"{targetName}.{member.MemberName}",
                staticFunction,
                staticFunction.TypeParameters,
                staticFunction.Parameters,
                staticFunction.ReturnTypeNode,
                staticArguments,
                skipSelf: false,
                isInstance: false);
        }

        if (_methodCallResolver.Resolve(member, typeArguments, arguments.Count, variables) is { SkipSelf: true } instanceMethodCall)
        {
            return BuildMethodResolution(instanceMethodCall, typeArguments, StripPointer(ResolveAlias(targetType)));
        }

        var receiverType = StripPointer(ResolveAlias(targetType));
        var receiverBaseType = GetGenericBaseName(receiverType);
        var receiverArguments = TryParseGenericUse(receiverType, out _, out var parsedReceiverArguments)
            ? parsedReceiverArguments
            : [];
        var instanceFunction = program.Functions.FirstOrDefault(function =>
            OwnerType(function) is not null
            && !function.IsStatic
            && string.Equals(function.Name, member.MemberName, StringComparison.Ordinal)
            && string.Equals(OwnerType(function), receiverBaseType, StringComparison.Ordinal)
            && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                || (typeArguments.Count == 0 && function.TypeParameters.Count == receiverArguments.Count)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: true, receiverArguments) is not null)));
        if (instanceFunction is not null)
        {
            var instanceArguments = typeArguments.Count > 0
                ? typeArguments
                : receiverArguments.Count == instanceFunction.TypeParameters.Count
                    ? receiverArguments
                    : InferFunctionTypeArguments(instanceFunction.TypeParameters, instanceFunction.Parameters, arguments, variables, skipSelf: true, receiverArguments) ?? [];
            var resolution = BuildFunctionResolution(
                $"{receiverBaseType}.{member.MemberName}",
                instanceFunction,
                instanceFunction.TypeParameters,
                instanceFunction.Parameters,
                instanceFunction.ReturnTypeNode,
                instanceArguments,
                skipSelf: true,
                isInstance: true);
            var selfType = Parse(receiverType);
            return resolution with
            {
                ReturnType = TypeRefRewriter.SubstituteSelf(resolution.ReturnType, selfType),
            };
        }

        var interfaceNode = program.Interfaces.FirstOrDefault(interfaceNode =>
            string.Equals(interfaceNode.Name, receiverType, StringComparison.Ordinal));
        var interfaceMethod = interfaceNode?.Methods.FirstOrDefault(method =>
            string.Equals(method.Name, member.MemberName, StringComparison.Ordinal));
        if (interfaceMethod is null)
        {
            return null;
        }

        return new CallResolution(
            $"{receiverType}.{member.MemberName}",
            ResolveType(interfaceMethod.ReturnTypeNode),
            interfaceMethod.Parameters
                .Where(parameter => !parameter.IsVariadic)
                .Select(parameter => ResolveType(parameter.TypeNode))
                .ToList(),
            interfaceMethod.Parameters.Any(parameter => parameter.IsVariadic));
    }

    private CallResolution? ResolveFunction(
        string name,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        var function = program.Functions.FirstOrDefault(function =>
            OwnerType(function) is null
            && string.Equals(function.Name, name, StringComparison.Ordinal)
            && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) is not null)));
        if (function is null)
        {
            return null;
        }

        var resolvedArguments = typeArguments.Count > 0
            ? typeArguments
            : InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) ?? [];
        return BuildFunctionResolution(
            function.Name,
            function,
            function.TypeParameters,
            function.Parameters,
            function.ReturnTypeNode,
            resolvedArguments,
            skipSelf: false,
            isInstance: false);
    }

    private CallResolution? ResolveExternFunction(
        string name,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables)
    {
        var function = program.ExternFunctions.FirstOrDefault(function =>
            string.Equals(function.Name, name, StringComparison.Ordinal)
            && (MatchesGenericArguments(function.TypeParameters, typeArguments)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) is not null)));
        if (function is null)
        {
            return null;
        }

        var resolvedArguments = typeArguments.Count > 0
            ? typeArguments
            : InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: false) ?? [];
        return BuildFunctionResolution(
            function.Name,
            function: null,
            function.TypeParameters,
            function.Parameters,
            function.ReturnTypeNode,
            resolvedArguments,
            skipSelf: false,
            isInstance: false);
    }

    private CallResolution? ResolveStaticRequirementCall(string targetName, string memberName)
    {
        if (!_currentTypeParameters.Contains(targetName, StringComparer.Ordinal))
        {
            return null;
        }

        foreach (var constraint in _currentGenericConstraints.Where(constraint =>
            string.Equals(constraint.TypeParameter, targetName, StringComparison.Ordinal)))
        {
            foreach (var reference in constraint.Requirements)
            {
                var requirement = program.Requirements.FirstOrDefault(requirement =>
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

                var referenceTypeArguments = TypeArguments(reference.TypeArgumentNodes);
                var arguments = referenceTypeArguments.Count == requirement.TypeParameters.Count
                    ? referenceTypeArguments
                    : requirement.TypeParameters.Count == 1
                        ? [targetName]
                        : [];
                var substitutions = BuildTypeSubstitutions(requirement.TypeParameters, arguments);
                return new CallResolution(
                    $"{targetName}.{memberName}",
                    SubstituteType(
                        ResolveType(function.ReturnTypeNode),
                        substitutions),
                    function.Parameters
                        .Where(parameter => !parameter.IsVariadic)
                        .Select(parameter => SubstituteType(
                            ResolveType(parameter.TypeNode),
                            substitutions))
                        .ToList(),
                    IsVariadic: false);
            }
        }

        return null;
    }

    private CallResolution BuildMethodResolution(
        ResolvedMethodCall methodCall,
        IReadOnlyList<string> explicitTypeArguments,
        string? receiverType)
    {
        var parameterTypes = methodCall.Method.ParameterTypes
            .Skip(methodCall.SkipSelf ? 1 : 0)
            .ToList();
        var function = methodCall.Method.Declaration;
        var typeArguments = ResolveFunctionTypeArguments(function, explicitTypeArguments, receiverType);
        return new CallResolution(
            methodCall.DisplayName,
            methodCall.Method.ReturnType,
            parameterTypes,
            IsVariadic: false,
            function,
            typeArguments,
            methodCall.SkipSelf);
    }

    private CallResolution BuildFunctionResolution(
        string name,
        FunctionNode? function,
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<ParameterNode> parameters,
        TypeNode? returnTypeNode,
        IReadOnlyList<string> typeArguments,
        bool skipSelf,
        bool isInstance)
    {
        var substitutions = BuildTypeSubstitutions(typeParameters, typeArguments);
        var filteredParameters = parameters
            .Skip(skipSelf ? 1 : 0)
            .ToList();
        var parameterTypes = filteredParameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => SubstituteType(ResolveType(parameter.TypeNode), substitutions))
            .ToList();
        return new CallResolution(
            name,
            SubstituteType(ResolveType(returnTypeNode), substitutions),
            parameterTypes,
            filteredParameters.Any(parameter => parameter.IsVariadic),
            function,
            typeArguments,
            isInstance);
    }

    private IReadOnlyList<string> ResolveFunctionTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> explicitTypeArguments,
        string? receiverType)
    {
        if (function.TypeParameters.Count == 0)
        {
            return [];
        }

        if (explicitTypeArguments.Count == function.TypeParameters.Count)
        {
            return explicitTypeArguments;
        }

        if (receiverType is not null
            && TryResolveAdapterBaseArguments(function, receiverType) is { } adapterBaseArguments)
        {
            return adapterBaseArguments;
        }

        return receiverType is not null
            && TryParseGenericUse(StripPointer(receiverType), out _, out var receiverArguments)
            && receiverArguments.Count == function.TypeParameters.Count
                ? receiverArguments
                : [];
    }

    private IReadOnlyList<string>? TryResolveAdapterBaseArguments(
        FunctionNode function,
        string receiverType)
    {
        var currentType = StripPointer(receiverType);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(currentType))
        {
            var currentName = GetGenericBaseName(currentType);
            if (string.Equals(OwnerType(function), currentName, StringComparison.Ordinal)
                && TryParseGenericUse(currentType, out _, out var currentArguments)
                && currentArguments.Count == function.TypeParameters.Count)
            {
                return currentArguments;
            }

            var adapter = program.TypeAdapters.FirstOrDefault(adapter =>
                string.Equals(adapter.Name, currentName, StringComparison.Ordinal));
            if (adapter is null)
            {
                return null;
            }

            var receiverArguments = TryParseGenericUse(currentType, out _, out var parsedReceiverArguments)
                ? parsedReceiverArguments
                : [];
            currentType = TypeText(adapter.BaseTypeNode);
            if (adapter.TypeParameters.Count == receiverArguments.Count)
            {
                var substitutions = adapter.TypeParameters
                    .Zip(receiverArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                currentType = GenericTypeStringRewriter.Substitute(currentType, substitutions);
            }
        }

        return null;
    }

    private string BuildStaticReceiverType(string targetName, IReadOnlyList<string> typeArguments)
    {
        if (typeArguments.Count == 0)
        {
            return targetName;
        }

        var typeParameterCount = program.Structs
            .FirstOrDefault(structNode => string.Equals(structNode.Name, targetName, StringComparison.Ordinal))
            ?.TypeParameters.Count
            ?? program.TypeAdapters
                .FirstOrDefault(adapter => string.Equals(adapter.Name, targetName, StringComparison.Ordinal))
                ?.TypeParameters.Count
            ?? 0;
        return typeParameterCount == typeArguments.Count
            ? $"{targetName}<{string.Join(",", typeArguments)}>"
            : targetName;
    }

    public IReadOnlyList<string>? InferFunctionTypeArguments(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<ParameterNode> parameters,
        IReadOnlyList<ExpressionNode> arguments,
        IReadOnlyDictionary<string, string> variables,
        bool skipSelf,
        IReadOnlyList<string>? seedArguments = null)
    {
        if (typeParameters.Count == 0)
        {
            return [];
        }

        var fixedParameters = parameters
            .Skip(skipSelf ? 1 : 0)
            .Where(parameter => !parameter.IsVariadic)
            .ToList();
        if (arguments.Count < fixedParameters.Count)
        {
            return null;
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (seedArguments is not null && seedArguments.Count == typeParameters.Count)
        {
            foreach (var (parameter, argument) in typeParameters.Zip(seedArguments))
            {
                bindings[parameter] = argument;
            }
        }

        for (var i = 0; i < fixedParameters.Count; i++)
        {
            var argumentType = resolveExpressionType(arguments[i], variables);
            if (argumentType is null)
            {
                return null;
            }

            if (!TryBindType(TypeText(fixedParameters[i].TypeNode), argumentType, typeParameters, bindings))
            {
                return null;
            }
        }

        return typeParameters.All(bindings.ContainsKey)
            ? typeParameters.Select(parameter => bindings[parameter]).ToList()
            : null;
    }

    private TypeRef Parse(string? type) =>
        _typeRefParser.Parse(type);

    private TypeRef ResolveType(TypeNode? typeNode) =>
        typeNode?.Semantic.Type
        ?? (typeNode?.Syntax is null ? null : _typeSyntaxConverter.Convert(typeNode))
        ?? Parse(TypeText(typeNode));

    private IReadOnlyDictionary<string, TypeRef> BuildTypeSubstitutions(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<string> typeArguments) =>
        typeParameters.Count == typeArguments.Count
            ? typeParameters.Zip(typeArguments)
                .ToDictionary(pair => pair.First, pair => Parse(pair.Second), StringComparer.Ordinal)
            : new Dictionary<string, TypeRef>(StringComparer.Ordinal);

    private TypeRef SubstituteType(TypeRef type, IReadOnlyDictionary<string, TypeRef> substitutions) =>
        substitutions.Count == 0
            ? type
            : TypeRefRewriter.Substitute(type, substitutions);

    private string ResolveAlias(string type)
    {
        var pointerSuffix = "";
        type = type.Trim();
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var aliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TypeText(group.First().TargetTypeNode), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(type, out var targetType) && seen.Add(type))
        {
            type = targetType;
        }

        return type + pointerSuffix;
    }

    private static bool MatchesGenericArguments(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<string> explicitArguments)
    {
        if (typeParameters.Count == 0)
        {
            return explicitArguments.Count == 0;
        }

        return explicitArguments.Count == typeParameters.Count;
    }

    private static bool TryBindType(
        string parameterType,
        string argumentType,
        IReadOnlyList<string> typeParameters,
        Dictionary<string, string> bindings)
    {
        parameterType = parameterType.Trim();
        argumentType = argumentType.Trim();
        if (typeParameters.Contains(parameterType, StringComparer.Ordinal))
        {
            return Bind(parameterType, argumentType, bindings);
        }

        if (parameterType.EndsWith("*", StringComparison.Ordinal)
            && argumentType.EndsWith("*", StringComparison.Ordinal))
        {
            return TryBindType(
                parameterType[..^1],
                argumentType[..^1],
                typeParameters,
                bindings);
        }

        if (TryParseGenericUse(parameterType, out var parameterName, out var parameterArguments)
            && TryParseGenericUse(argumentType, out var argumentName, out var argumentArguments)
            && string.Equals(parameterName, argumentName, StringComparison.Ordinal)
            && parameterArguments.Count == argumentArguments.Count)
        {
            for (var i = 0; i < parameterArguments.Count; i++)
            {
                if (!TryBindType(parameterArguments[i], argumentArguments[i], typeParameters, bindings))
                {
                    return false;
                }
            }

            return true;
        }

        return true;
    }

    private static bool Bind(string typeParameter, string typeArgument, Dictionary<string, string> bindings)
    {
        if (!bindings.TryGetValue(typeParameter, out var existing))
        {
            bindings[typeParameter] = typeArgument;
            return true;
        }

        return string.Equals(existing.Trim(), typeArgument.Trim(), StringComparison.Ordinal);
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
        return TypeSyntaxParser.Parse(type) is GenericTypeSyntaxNode generic
            ? TypeSyntaxFormatter.ToCxString(generic.Target)
            : type;
    }

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private static bool TryParseGenericUse(string type, out string name, out IReadOnlyList<string> arguments)
    {
        name = string.Empty;
        arguments = [];
        if (TypeSyntaxParser.Parse(type) is not GenericTypeSyntaxNode generic)
        {
            return false;
        }

        name = TypeSyntaxFormatter.ToCxString(generic.Target);
        arguments = generic.Arguments.Select(TypeSyntaxFormatter.ToCxString).ToList();
        return true;
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

        var type = typeNode.ToTypeRef(_typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private string? TypeTextOrNull(TypeNode? typeNode)
    {
        var type = TypeText(typeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }
}
