using System.Text.RegularExpressions;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ExpressionTypeResolver(
    ProgramNode program,
    IReadOnlyList<string>? currentTypeParameters = null,
    IReadOnlyList<GenericConstraintNode>? currentGenericConstraints = null)
{
    private readonly IReadOnlyList<string> _currentTypeParameters = currentTypeParameters ?? [];
    private readonly IReadOnlyList<GenericConstraintNode> _currentGenericConstraints = currentGenericConstraints ?? [];
    private readonly IReadOnlyDictionary<string, string> _typeAliases = program.TypeAliases
        .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().TargetType, StringComparer.Ordinal);

    public string? Resolve(ExpressionNode? expression, IReadOnlyDictionary<string, string> variables)
    {
        if (expression is null)
        {
            return null;
        }

        return expression switch
        {
            LiteralExpressionNode literal => ResolveLiteral(literal.SourceText),
            NameExpressionNode name => ResolveName(name.SourceText, variables),
            ParenthesizedExpressionNode parenthesized => Resolve(parenthesized.Expression, variables),
            CastExpressionNode cast => cast.TargetType,
            UnaryExpressionNode unary => ResolveUnary(unary, variables),
            PostfixExpressionNode postfix => Resolve(postfix.Operand, variables),
            SizeOfExpressionNode => "usize",
            BinaryExpressionNode binary => ResolveBinary(binary, variables),
            ScalarRangeExpressionNode range => ResolveRange(range, variables),
            ConditionalExpressionNode conditional => ResolveConditional(conditional, variables),
            InitializerExpressionNode initializer => ResolveInitializer(initializer, variables),
            FunctionExpressionNode functionExpression => ResolveFunctionExpression(functionExpression),
            AssignmentExpressionNode assignment => Resolve(assignment.Target, variables),
            MemberExpressionNode member => ResolveMember(member, variables),
            CallExpressionNode call => ResolveCall(call, variables),
            GenericCallExpressionNode call => ResolveGenericCall(call, variables),
            IndexExpressionNode index => ResolveIndex(index, variables),
            RawExpressionNode raw => ResolveRaw(raw.SourceText, variables),
            _ => null,
        };
    }

    private string? ResolveName(string name, IReadOnlyDictionary<string, string> variables)
    {
        if (variables.TryGetValue(name, out var type))
        {
            return type;
        }

        var function = program.Functions.FirstOrDefault(function =>
            function.OwnerType is null
            && function.TypeParameters.Count == 0
            && function.Name == name);
        if (function is not null)
        {
            return GetFunctionType(function.Parameters, function.ReturnType);
        }

        var externFunction = program.ExternFunctions.FirstOrDefault(function =>
            function.Name == name
            && function.TypeParameters.Count == 0);
        return externFunction is null
            ? null
            : GetFunctionType(externFunction.Parameters, externFunction.ReturnType);
    }

    private static string? ResolveLiteral(string text)
    {
        text = text.Trim();
        if (text is "true" or "false")
        {
            return "bool";
        }

        if (text == "null")
        {
            return "null";
        }

        if (text.StartsWith("\"", StringComparison.Ordinal))
        {
            return "char*";
        }

        if (text.StartsWith("'", StringComparison.Ordinal))
        {
            return "char";
        }

        if (Regex.IsMatch(text, @"^-?\d+$"))
        {
            return "int";
        }

        if (Regex.IsMatch(text, @"^-?(\d+\.\d*|\d*\.\d+)([eE][+-]?\d+)?$"))
        {
            return "double";
        }

        return null;
    }

    private string? ResolveUnary(UnaryExpressionNode unary, IReadOnlyDictionary<string, string> variables)
    {
        var operandType = Resolve(unary.Operand, variables);
        if (operandType is null)
        {
            return null;
        }

        return unary.Operator switch
        {
            "&" => operandType + "*",
            "*" => UnwrapPointer(operandType),
            "!" => "bool",
            "+" => operandType,
            "-" => operandType,
            _ => null,
        };
    }

    private string? ResolveBinary(BinaryExpressionNode binary, IReadOnlyDictionary<string, string> variables)
    {
        if (binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||")
        {
            return "bool";
        }

        if (binary.Operator == "<=>")
        {
            return "int";
        }

        var left = Resolve(binary.Left, variables);
        var right = Resolve(binary.Right, variables);
        return SameType(left, right) ? left : left ?? right;
    }

    private string? ResolveConditional(ConditionalExpressionNode conditional, IReadOnlyDictionary<string, string> variables)
    {
        var whenTrue = Resolve(conditional.WhenTrue, variables);
        var whenFalse = Resolve(conditional.WhenFalse, variables);
        return SameType(whenTrue, whenFalse) ? whenTrue : whenTrue ?? whenFalse;
    }

    private string? ResolveRange(ScalarRangeExpressionNode range, IReadOnlyDictionary<string, string> variables)
    {
        var start = Resolve(range.Start, variables);
        var end = Resolve(range.End, variables);
        if (SameType(start, end))
        {
            return start;
        }

        if (start == "int" && IsIntegerLiteral(range.Start) && end is not null)
        {
            return end;
        }

        if (end == "int" && IsIntegerLiteral(range.End) && start is not null)
        {
            return start;
        }

        return SameType(start, end) ? start : start ?? end;
    }

    private static bool IsIntegerLiteral(ExpressionNode expression) =>
        expression is LiteralExpressionNode literal
        && Regex.IsMatch(literal.SourceText.Trim(), @"^-?\d+$");

    private string? ResolveInitializer(InitializerExpressionNode initializer, IReadOnlyDictionary<string, string> variables)
    {
        if (!string.IsNullOrWhiteSpace(initializer.TypeName))
        {
            return initializer.TypeName;
        }

        if (initializer.Values.Count == 0)
        {
            return null;
        }

        var firstType = Resolve(initializer.Values[0], variables);
        return initializer.Values
            .Skip(1)
            .Select(value => Resolve(value, variables))
            .All(type => SameType(firstType, type))
            ? firstType
            : null;
    }

    private static string ResolveFunctionExpression(FunctionExpressionNode functionExpression)
    {
        var returnType = string.IsNullOrWhiteSpace(functionExpression.ReturnType)
            ? "int"
            : functionExpression.ReturnType;
        return GetFunctionType(functionExpression.Parameters, returnType);
    }

    private static string GetFunctionType(IReadOnlyList<ParameterNode> parameters, string returnType) =>
        $"fn({string.Join(",", parameters.Select(parameter => parameter.Type))})->{returnType}";

    private string? ResolveMember(MemberExpressionNode member, IReadOnlyDictionary<string, string> variables)
    {
        var targetType = Resolve(member.Target, variables);
        if (targetType is null)
        {
            var staticFunctionType = ResolveStaticFunctionReference(member);
            if (staticFunctionType is not null)
            {
                return staticFunctionType;
            }

            var qualifiedName = GetQualifiedName(member);
            var global = program.GlobalVariables.FirstOrDefault(global =>
                string.Equals(global.Name, qualifiedName, StringComparison.Ordinal));
            return global?.Type;
        }

        var isPointer = targetType.TrimEnd().EndsWith("*", StringComparison.Ordinal);
        var normalizedType = StripPointer(ResolveAlias(targetType));

        var structNode = ResolveStruct(normalizedType);
        var field = structNode?.Fields.FirstOrDefault(field => field.Name == member.MemberName);
        if (field is not null)
        {
            return field.Type;
        }

        var union = program.TaggedUnions.FirstOrDefault(union => union.Name == normalizedType);
        var variant = union?.Variants.FirstOrDefault(variant => variant.Name == member.MemberName);
        if (variant is not null)
        {
            return variant.Type;
        }

        var interfaceNode = program.Interfaces.FirstOrDefault(interfaceNode => interfaceNode.Name == normalizedType);
        if (interfaceNode is not null)
        {
            if (member.MemberName == "state")
            {
                return "void*";
            }

            var method = interfaceNode.Methods.FirstOrDefault(method => "v_" + method.Name == member.MemberName);
            if (method is not null)
            {
                var parameterTypes = new[] { "void*" }
                    .Concat(method.Parameters.Select(parameter => parameter.Type));
                return $"fn({string.Join(",", parameterTypes)})->{method.ReturnType}";
            }
        }

        return isPointer ? null : null;
    }

    private string? ResolveStaticFunctionReference(MemberExpressionNode member)
    {
        var targetName = GetQualifiedName(member.Target);
        if (targetName is null)
        {
            return null;
        }

        var function = program.Functions.FirstOrDefault(function =>
            function.IsStatic
            && function.OwnerType is not null
            && function.TypeParameters.Count == 0
            && targetName == function.OwnerType
            && function.Name == member.MemberName);
        return function is null
            ? null
            : GetFunctionType(function.Parameters, function.ReturnType);
    }

    private string? ResolveIndex(IndexExpressionNode index, IReadOnlyDictionary<string, string> variables)
    {
        var targetType = Resolve(index.Target, variables);
        if (targetType is null)
        {
            return null;
        }

        if (TryParseFixedArrayType(targetType, out var arrayElementType, out _))
        {
            return arrayElementType;
        }

        return UnwrapPointer(targetType);
    }

    private string? ResolveCall(CallExpressionNode call, IReadOnlyDictionary<string, string> variables)
    {
        if (call.Callee is MemberExpressionNode member)
        {
            var memberReturnType = ResolveMemberCall(member, [], call.Arguments, variables);
            if (memberReturnType is not null)
            {
                return memberReturnType;
            }
        }

        var calleeType = Resolve(call.Callee, variables);
        if (TryParseFunctionType(calleeType, out _, out var functionPointerReturnType))
        {
            return functionPointerReturnType;
        }

        var name = GetQualifiedName(call.Callee);
        if (name is null)
        {
            return null;
        }

        var function = program.Functions.FirstOrDefault(function =>
            function.OwnerType is null
            && function.TypeParameters.Count == 0
            && function.Name == name);
        if (function is not null)
        {
            return function.ReturnType;
        }

        var genericFunction = program.Functions.FirstOrDefault(function =>
            function.OwnerType is null
            && function.TypeParameters.Count > 0
            && function.Name == name
            && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false) is not null);
        if (genericFunction is not null
            && InferFunctionTypeArguments(genericFunction.TypeParameters, genericFunction.Parameters, call.Arguments, variables, skipSelf: false) is { } inferredArguments)
        {
            return ResolveFunctionReturnType(genericFunction, inferredArguments);
        }

        var externFunction = program.ExternFunctions.FirstOrDefault(function =>
            function.Name == name
            && function.TypeParameters.Count == 0);
        if (externFunction is not null)
        {
            return externFunction.ReturnType;
        }

        var genericExternFunction = program.ExternFunctions.FirstOrDefault(function =>
            function.Name == name
            && function.TypeParameters.Count > 0
            && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false) is not null);
        return genericExternFunction is not null
            && InferFunctionTypeArguments(genericExternFunction.TypeParameters, genericExternFunction.Parameters, call.Arguments, variables, skipSelf: false) is { } inferredExternArguments
            ? ResolveExternFunctionReturnType(genericExternFunction, inferredExternArguments)
            : null;
    }

    private string? ResolveGenericCall(GenericCallExpressionNode call, IReadOnlyDictionary<string, string> variables)
    {
        if (call.Callee is MemberExpressionNode member)
        {
            return ResolveMemberCall(member, call.TypeArguments, call.Arguments, variables);
        }

        var name = GetQualifiedName(call.Callee);
        if (name is null)
        {
            return null;
        }

        var function = program.Functions.FirstOrDefault(function =>
            function.OwnerType is null
            && function.Name == name
            && MatchesGenericArguments(function, call.TypeArguments));
        if (function is not null)
        {
            return ResolveFunctionReturnType(function, call.TypeArguments);
        }

        var externFunction = program.ExternFunctions.FirstOrDefault(function =>
            function.Name == name
            && MatchesGenericArguments(function, call.TypeArguments));
        if (externFunction is not null)
        {
            return ResolveExternFunctionReturnType(externFunction, call.TypeArguments);
        }

        var staticFunction = program.Functions.FirstOrDefault(function =>
            function.IsStatic
            && function.OwnerType is not null
            && name == $"{function.OwnerType}.{function.Name}"
            && MatchesGenericArguments(function, call.TypeArguments));
        return staticFunction is null
            ? null
            : ResolveFunctionReturnType(staticFunction, call.TypeArguments);
    }

    private string? ResolveMemberCall(
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
            if (ResolveStaticRequirementCallReturnType(targetName, member.MemberName) is { } requirementReturnType)
            {
                return requirementReturnType;
            }

            var staticFunction = program.Functions.FirstOrDefault(function =>
                function.IsStatic
                && function.OwnerType is not null
                && targetName == function.OwnerType
                && function.Name == member.MemberName
                && (MatchesGenericArguments(function, typeArguments)
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
            return ResolveFunctionReturnType(staticFunction, staticArguments);
        }

        var normalizedType = StripPointer(ResolveAlias(targetType));
        var receiverTypeArguments = TryParseGenericUse(normalizedType, out _, out var parsedReceiverArguments)
            ? parsedReceiverArguments
            : [];
        var receiverBaseType = GetGenericBaseName(normalizedType);
        if (FindAdapterExpose(receiverBaseType, member.MemberName) is { } adapterExpose)
        {
            var baseType = SubstituteAdapterBaseType(adapterExpose.Adapter, receiverTypeArguments);
            var baseTypeArguments = TryParseGenericUse(baseType, out _, out var parsedBaseArguments)
                ? parsedBaseArguments
                : [];
            var baseOwnerType = GetGenericBaseName(baseType);
            var exposedFunction = program.Functions.FirstOrDefault(function =>
                function.OwnerType is not null
                && !function.IsStatic
                && function.Name == adapterExpose.Expose.SourceName
                && SameType(function.OwnerType, baseOwnerType)
                && (MatchesGenericArguments(function, typeArguments, baseTypeArguments)
                    || (typeArguments.Count == 0
                        && function.TypeParameters.Count > 0
                        && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: true, baseTypeArguments) is not null)));
            if (exposedFunction is not null)
            {
                var exposedArguments = typeArguments.Count > 0
                    ? typeArguments
                    : baseTypeArguments.Count == exposedFunction.TypeParameters.Count
                        ? baseTypeArguments
                        : InferFunctionTypeArguments(exposedFunction.TypeParameters, exposedFunction.Parameters, arguments, variables, skipSelf: true, baseTypeArguments) ?? [];
                return ResolveFunctionReturnType(exposedFunction, exposedArguments);
            }
        }

        var instanceFunction = program.Functions.FirstOrDefault(function =>
            function.OwnerType is not null
            && !function.IsStatic
            && function.Name == member.MemberName
            && SameType(function.OwnerType, receiverBaseType)
            && (MatchesGenericArguments(function, typeArguments, receiverTypeArguments)
                || (typeArguments.Count == 0
                    && function.TypeParameters.Count > 0
                    && InferFunctionTypeArguments(function.TypeParameters, function.Parameters, arguments, variables, skipSelf: true, receiverTypeArguments) is not null)));
        if (instanceFunction is not null)
        {
            var instanceArguments = typeArguments.Count > 0
                ? typeArguments
                : receiverTypeArguments.Count == instanceFunction.TypeParameters.Count
                    ? receiverTypeArguments
                    : InferFunctionTypeArguments(instanceFunction.TypeParameters, instanceFunction.Parameters, arguments, variables, skipSelf: true, receiverTypeArguments) ?? [];
            return SubstituteSelfType(
                ResolveFunctionReturnType(instanceFunction, instanceArguments),
                normalizedType);
        }

        var interfaceNode = program.Interfaces.FirstOrDefault(interfaceNode => interfaceNode.Name == normalizedType);
        var interfaceMethod = interfaceNode?.Methods.FirstOrDefault(method => method.Name == member.MemberName);
        return interfaceMethod?.ReturnType;
    }

    private string? ResolveStaticRequirementCallReturnType(string targetName, string memberName)
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
                return SubstituteGenericType(function.ReturnType, substitutions);
            }
        }

        return null;
    }

    private sealed record AdapterExposeMatch(TypeAdapterNode Adapter, ExposeMethodNode Expose);

    private AdapterExposeMatch? FindAdapterExpose(string? adapterName, string exposedName)
    {
        if (adapterName is null)
        {
            return null;
        }

        foreach (var adapter in program.TypeAdapters.Where(adapter => adapter.Name == adapterName))
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
        return SubstituteGenericType(adapter.BaseType, substitutions);
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
            var argumentType = Resolve(arguments[i], variables);
            if (argumentType is null)
            {
                return null;
            }

            if (!TryBindType(fixedParameters[i].Type, argumentType, typeParameters, bindings))
            {
                return null;
            }
        }

        return typeParameters.All(bindings.ContainsKey)
            ? typeParameters.Select(parameter => bindings[parameter]).ToList()
            : null;
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

    private static bool MatchesGenericArguments(
        FunctionNode function,
        IReadOnlyList<string> explicitArguments,
        IReadOnlyList<string>? receiverArguments = null)
    {
        if (function.TypeParameters.Count == 0)
        {
            return explicitArguments.Count == 0;
        }

        if (explicitArguments.Count > 0)
        {
            return explicitArguments.Count == function.TypeParameters.Count;
        }

        return receiverArguments is not null
            && receiverArguments.Count == function.TypeParameters.Count;
    }

    private static bool MatchesGenericArguments(
        ExternFunctionNode function,
        IReadOnlyList<string> explicitArguments)
    {
        if (function.TypeParameters.Count == 0)
        {
            return explicitArguments.Count == 0;
        }

        return explicitArguments.Count == function.TypeParameters.Count;
    }

    private static string ResolveFunctionReturnType(
        FunctionNode function,
        IReadOnlyList<string> explicitArguments,
        IReadOnlyList<string>? receiverArguments = null)
    {
        var arguments = explicitArguments.Count > 0
            ? explicitArguments
            : receiverArguments ?? [];
        if (function.TypeParameters.Count == 0 || arguments.Count != function.TypeParameters.Count)
        {
            return function.ReturnType;
        }

        var substitutions = function.TypeParameters
            .Zip(arguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var returnType = SubstituteGenericType(function.ReturnType, substitutions);
        return function.OwnerType is null
            ? returnType
            : SubstituteSelfType(returnType, BuildSelfType(function, arguments));
    }

    private static string ResolveExternFunctionReturnType(
        ExternFunctionNode function,
        IReadOnlyList<string> explicitArguments)
    {
        if (function.TypeParameters.Count == 0 || explicitArguments.Count != function.TypeParameters.Count)
        {
            return function.ReturnType;
        }

        var substitutions = function.TypeParameters
            .Zip(explicitArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        return SubstituteGenericType(function.ReturnType, substitutions);
    }

    private static string BuildSelfType(FunctionNode function, IReadOnlyList<string> arguments)
    {
        if (function.OwnerType is null || arguments.Count == 0)
        {
            return function.OwnerType ?? string.Empty;
        }

        return $"{function.OwnerType}<{string.Join(",", arguments)}>";
    }

    private string? ResolveRaw(string text, IReadOnlyDictionary<string, string> variables)
    {
        text = text.Trim();
        return variables.TryGetValue(text, out var type)
            ? type
            : ResolveLiteral(text);
    }

    private StructNode? ResolveStruct(string type)
    {
        if (TryParseGenericUse(type, out var genericName, out var arguments))
        {
            var definition = program.Structs.FirstOrDefault(structNode =>
                structNode.Name == genericName
                && structNode.TypeParameters.Count == arguments.Count);
            if (definition is null)
            {
                return null;
            }

            var substitutions = definition.TypeParameters
                .Zip(arguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            return definition with
            {
                Fields = definition.Fields
                    .Select(field => field with { Type = SubstituteGenericType(field.Type, substitutions) })
                    .ToList(),
            };
        }

        return program.Structs.FirstOrDefault(structNode =>
            structNode.Name == type
            && structNode.TypeParameters.Count == 0);
    }

    private string ResolveAlias(string type)
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

    private static string? UnwrapPointer(string type)
    {
        type = type.Trim();
        return type.EndsWith("*", StringComparison.Ordinal)
            ? type[..^1].TrimEnd()
            : null;
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

    private static bool SameType(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static bool SameTypeArguments(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

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

    private static bool TryParseFunctionType(
        string? type,
        out IReadOnlyList<string> parameters,
        out string returnType)
    {
        parameters = [];
        returnType = string.Empty;
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

        parameters = SplitGenericArguments(type[3..close]);
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

    private static string SubstituteGenericType(string type, IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var (parameter, argument) in substitutions)
        {
            type = Regex.Replace(type, $@"\b{Regex.Escape(parameter)}\b", argument);
        }

        return type;
    }

    private static string SubstituteSelfType(string type, string selfType) =>
        Regex.Replace(type, @"\bSelf\b", selfType);
}
