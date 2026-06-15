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
        IReadOnlyDictionary<string, string> variables) =>
        Resolve(
            callee,
            typeArguments,
            arguments,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));

    public CallResolution? Resolve(
        ExpressionNode callee,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        TypeEnvironment variables)
    {
        var legacyVariables = variables.ToLegacyStrings();
        if (callee is MemberExpressionNode member)
        {
            return ResolveMemberCall(member, typeArguments, arguments, variables);
        }

        var name = ExpressionNameFacts.GetQualifiedName(callee);
        if (name is not null && !variables.Types.ContainsKey(name))
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

        var calleeType = resolveExpressionType(callee, legacyVariables);
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
        TypeEnvironment variables)
    {
        var legacyVariables = variables.ToLegacyStrings();
        var targetName = ExpressionNameFacts.GetQualifiedName(member.Target);
        if (targetName is null)
        {
            return null;
        }

        if (!variables.TryGet(targetName, out var targetType))
        {
            if (ResolveStaticRequirementCall(targetName, member.MemberName) is { } requirementCall)
            {
                return requirementCall;
            }

            if (_methodCallResolver.Resolve(member, typeArguments, arguments.Count, legacyVariables) is { SkipSelf: false } staticMethodCall)
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

        if (_methodCallResolver.Resolve(member, typeArguments, arguments.Count, legacyVariables) is { SkipSelf: true } instanceMethodCall)
        {
            var methodReceiverType = NormalizeReceiverType(targetType);
            return BuildMethodResolution(
                instanceMethodCall,
                typeArguments,
                methodReceiverType is null ? null : TypeRefFormatter.ToCxString(methodReceiverType));
        }

        var receiverTypeRef = NormalizeReceiverType(targetType);
        if (receiverTypeRef is null)
        {
            return null;
        }

        var receiverType = TypeRefFormatter.ToCxString(receiverTypeRef);
        var receiverBaseType = TypeRefFacts.GetBaseName(receiverTypeRef) ?? receiverType;
        var receiverArguments = TypeRefFacts.TryGetGenericArguments(receiverTypeRef, out var parsedReceiverArguments)
            ? parsedReceiverArguments.Select(TypeRefFormatter.ToCxString).ToList()
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
            var selfType = receiverTypeRef;
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
        TypeEnvironment variables)
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
        TypeEnvironment variables)
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

        var receiverTypeRef = receiverType is null ? null : NormalizeReceiverType(receiverType);
        if (receiverTypeRef is not null
            && TryResolveAdapterBaseArguments(function, receiverTypeRef) is { } adapterBaseArguments)
        {
            return adapterBaseArguments;
        }

        return receiverTypeRef is not null
            && TypeRefFacts.TryGetGenericArguments(receiverTypeRef, out var receiverArguments)
            && receiverArguments.Count == function.TypeParameters.Count
                ? receiverArguments.Select(TypeRefFormatter.ToCxString).ToList()
                : [];
    }

    private IReadOnlyList<string>? TryResolveAdapterBaseArguments(
        FunctionNode function,
        TypeRef receiverType)
    {
        var currentType = receiverType;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(TypeRefFormatter.ToCxString(currentType)))
        {
            var currentName = TypeRefFacts.GetBaseName(currentType);
            if (currentName is null)
            {
                return null;
            }

            if (string.Equals(OwnerType(function), currentName, StringComparison.Ordinal)
                && TypeRefFacts.TryGetGenericArguments(currentType, out var currentArguments)
                && currentArguments.Count == function.TypeParameters.Count)
            {
                return currentArguments.Select(TypeRefFormatter.ToCxString).ToList();
            }

            var adapter = program.TypeAdapters.FirstOrDefault(adapter =>
                string.Equals(adapter.Name, currentName, StringComparison.Ordinal));
            if (adapter is null)
            {
                return null;
            }

            var receiverArguments = TypeRefFacts.TryGetGenericArguments(currentType, out var parsedReceiverArguments)
                ? parsedReceiverArguments
                : [];
            if (adapter.TypeParameters.Count == receiverArguments.Count)
            {
                var substitutions = adapter.TypeParameters
                    .Zip(receiverArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                currentType = TypeRefRewriter.Substitute(ResolveType(adapter.BaseTypeNode), substitutions);
            }
            else
            {
                currentType = ResolveType(adapter.BaseTypeNode);
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
        IReadOnlyList<string>? seedArguments = null) =>
        InferFunctionTypeArguments(
            typeParameters,
            parameters,
            arguments,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables),
            skipSelf,
            seedArguments);

    public IReadOnlyList<string>? InferFunctionTypeArguments(
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<ParameterNode> parameters,
        IReadOnlyList<ExpressionNode> arguments,
        TypeEnvironment variables,
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

        var bindings = new TypeBindings();
        if (seedArguments is not null && seedArguments.Count == typeParameters.Count)
        {
            foreach (var (parameter, argument) in typeParameters.Zip(seedArguments))
            {
                bindings.Set(parameter, Parse(argument));
            }
        }

        for (var i = 0; i < fixedParameters.Count; i++)
        {
            var argumentType = ResolveArgumentType(arguments[i], variables);
            if (argumentType is null)
            {
                return null;
            }

            if (!TryBindType(ResolveType(fixedParameters[i].TypeNode), argumentType, typeParameters, bindings))
            {
                return null;
            }
        }

        return typeParameters.All(parameter => bindings.Bindings.ContainsKey(parameter))
            ? typeParameters.Select(parameter => TypeRefFormatter.ToCxString(bindings.Bindings[parameter])).ToList()
            : null;
    }

    private TypeRef? ResolveArgumentType(ExpressionNode argument, TypeEnvironment variables)
    {
        if (argument is NameExpressionNode name
            && variables.TryGet(name.SourceText, out var type))
        {
            return type;
        }

        var argumentType = resolveExpressionType(argument, variables.ToLegacyStrings());
        return argumentType is null ? null : Parse(argumentType);
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

    private TypeRef? NormalizeReceiverType(string type)
    {
        var parsed = Parse(type);
        return parsed is TypeRef.Unknown
            ? null
            : TypeRefFacts.StripPointersAndAliases(parsed);
    }

    private static TypeRef? NormalizeReceiverType(TypeRef type) =>
        type is TypeRef.Unknown ? null : TypeRefFacts.StripPointersAndAliases(type);

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
        TypeRef? parameterType,
        TypeRef? argumentType,
        IReadOnlyList<string> typeParameters,
        TypeBindings bindings)
    {
        if (parameterType is null || argumentType is null)
        {
            return true;
        }

        parameterType = TypeRefFacts.UnwrapAlias(parameterType);
        argumentType = TypeRefFacts.UnwrapAlias(argumentType);

        if (parameterType is TypeRef.Named { Arguments.Count: 0 } parameterNamed
            && typeParameters.Contains(parameterNamed.Name, StringComparer.Ordinal))
        {
            return Bind(parameterNamed.Name, argumentType, bindings);
        }

        if (TypeRefFacts.TryGetPointerElement(parameterType, out var parameterElement)
            && TypeRefFacts.TryGetPointerElement(argumentType, out var argumentElement))
        {
            return TryBindType(parameterElement, argumentElement, typeParameters, bindings);
        }

        if (parameterType is TypeRef.Named parameterGeneric
            && argumentType is TypeRef.Named argumentGeneric
            && string.Equals(parameterGeneric.Name, argumentGeneric.Name, StringComparison.Ordinal)
            && parameterGeneric.Arguments.Count > 0
            && parameterGeneric.Arguments.Count == argumentGeneric.Arguments.Count)
        {
            var parameterArguments = parameterGeneric.Arguments;
            var argumentArguments = argumentGeneric.Arguments;
            for (var i = 0; i < parameterArguments.Count; i++)
            {
                if (!TryBindType(parameterArguments[i], argumentArguments[i], typeParameters, bindings))
                {
                    return false;
                }
            }

            return true;
        }

        if (parameterType is TypeRef.FixedArray parameterArray
            && argumentType is TypeRef.FixedArray argumentArray
            && string.Equals(parameterArray.Length, argumentArray.Length, StringComparison.Ordinal))
        {
            return TryBindType(parameterArray.Element, argumentArray.Element, typeParameters, bindings);
        }

        if (parameterType is TypeRef.Function parameterFunction
            && argumentType is TypeRef.Function argumentFunction
            && parameterFunction.Parameters.Count == argumentFunction.Parameters.Count
            && parameterFunction.IsVariadic == argumentFunction.IsVariadic)
        {
            for (var i = 0; i < parameterFunction.Parameters.Count; i++)
            {
                if (!TryBindType(parameterFunction.Parameters[i], argumentFunction.Parameters[i], typeParameters, bindings))
                {
                    return false;
                }
            }

            return TryBindType(parameterFunction.ReturnType, argumentFunction.ReturnType, typeParameters, bindings);
        }

        return true;
    }

    private static bool Bind(string typeParameter, TypeRef typeArgument, TypeBindings bindings)
    {
        if (!bindings.TryGet(typeParameter, out var existing))
        {
            bindings.Set(typeParameter, typeArgument);
            return true;
        }

        return TypeRefFacts.SameType(existing, typeArgument);
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
