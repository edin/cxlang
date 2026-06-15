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
    private readonly TypeRefParser _typeRefParser = new(program);
    private readonly TypeSyntaxTypeRefConverter _typeSyntaxConverter = new(program);
    private CallResolver? _callResolver;

    private CallResolver CallResolver => _callResolver ??= new CallResolver(
        program,
        Resolve,
        _currentTypeParameters,
        _currentGenericConstraints);

    public string? Resolve(ExpressionNode? expression, IReadOnlyDictionary<string, string> variables)
    {
        return Resolve(
            expression,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    public string? Resolve(ExpressionNode? expression, TypeEnvironment variables)
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
            CastExpressionNode cast => TypeText(cast.TargetTypeNode),
            UnaryExpressionNode unary => ResolveUnary(unary, variables),
            PostfixExpressionNode postfix => Resolve(postfix.Operand, variables),
            SizeOfExpressionNode => "usize",
            BinaryExpressionNode binary => ResolveBinary(binary, variables),
            ScalarRangeExpressionNode range => ResolveRange(range, variables),
            ConditionalExpressionNode conditional => ResolveConditional(conditional, variables),
            InitializerExpressionNode initializer => ResolveInitializer(initializer, variables),
            FunctionExpressionNode functionExpression => ResolveFunctionExpression(functionExpression),
            AssignmentExpressionNode assignment => Resolve(assignment.Target, variables),
            MemberExpressionNode member => FormatTypeRef(ResolveMemberTypeRef(member, variables)),
            CallExpressionNode call => ResolveCall(call, variables),
            GenericCallExpressionNode call => ResolveGenericCall(call, variables),
            IndexExpressionNode index => ResolveIndex(index, variables),
            RawExpressionNode raw => ResolveRaw(raw.SourceText, variables),
            _ => null,
        };
    }

    public TypeRef? ResolveTypeRef(ExpressionNode? expression, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveTypeRef(
            expression,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    public TypeRef? ResolveTypeRef(ExpressionNode? expression, TypeEnvironment variables)
    {
        if (expression is null)
        {
            return null;
        }

        if (expression is SizeOfExpressionNode)
        {
            return ParseResolvedType("usize");
        }

        if (expression is FunctionExpressionNode functionLiteral)
        {
            return ResolveFunctionExpressionTypeRef(functionLiteral);
        }

        if (expression.Semantic.Type is { } semanticType)
        {
            return semanticType;
        }

        return expression switch
        {
            LiteralExpressionNode literal => ParseResolvedType(ResolveLiteral(literal.SourceText)),
            NameExpressionNode name => ResolveNameTypeRef(name.SourceText, variables),
            ParenthesizedExpressionNode parenthesized => ResolveTypeRef(parenthesized.Expression, variables),
            CastExpressionNode cast => ResolveTypeNode(cast.TargetTypeNode),
            UnaryExpressionNode unary => ResolveUnaryTypeRef(unary, variables),
            PostfixExpressionNode postfix => ResolveTypeRef(postfix.Operand, variables),
            SizeOfExpressionNode => ParseResolvedType("usize"),
            BinaryExpressionNode binary => ResolveBinaryTypeRef(binary, variables),
            ScalarRangeExpressionNode range => ResolveRangeTypeRef(range, variables),
            ConditionalExpressionNode conditional => ResolveConditionalTypeRef(conditional, variables),
            InitializerExpressionNode initializer => ResolveInitializerTypeRef(initializer, variables),
            FunctionExpressionNode functionExpression => ResolveFunctionExpressionTypeRef(functionExpression),
            AssignmentExpressionNode assignment => ResolveTypeRef(assignment.Target, variables),
            MemberExpressionNode member => ResolveMemberTypeRef(member, variables),
            IndexExpressionNode index => ResolveIndexTypeRef(index, variables),
            _ => ParseResolvedType(Resolve(expression, variables)),
        };
    }

    private string? ResolveName(string name, IReadOnlyDictionary<string, string> variables)
    {
        if (variables.TryGetValue(name, out var type))
        {
            return type;
        }

        var function = program.Functions.FirstOrDefault(function =>
            OwnerType(function) is null
            && function.TypeParameters.Count == 0
            && function.Name == name);
        if (function is not null)
        {
            return GetFunctionType(function.Parameters, TypeText(function.ReturnTypeNode));
        }

        var externFunction = program.ExternFunctions.FirstOrDefault(function =>
            function.Name == name
            && function.TypeParameters.Count == 0);
        return externFunction is null
            ? null
            : GetFunctionType(externFunction.Parameters, TypeText(externFunction.ReturnTypeNode));
    }

    private string? ResolveName(string name, TypeEnvironment variables)
    {
        if (variables.TryGet(name, out var type))
        {
            return TypeRefFormatter.ToCxString(type);
        }

        return ResolveName(name, variables.ToLegacyStrings());
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

    private TypeRef? ResolveNameTypeRef(string name, IReadOnlyDictionary<string, string> variables) =>
        variables.TryGetValue(name, out var type)
            ? ParseResolvedType(type)
            : ParseResolvedType(ResolveName(name, variables));

    private TypeRef? ResolveNameTypeRef(string name, TypeEnvironment variables) =>
        variables.TryGet(name, out var type)
            ? type
            : ParseResolvedType(ResolveName(name, variables.ToLegacyStrings()));

    private TypeRef? ResolveTypeNode(TypeNode? typeNode) =>
        typeNode?.Semantic.Type
        ?? (typeNode?.Syntax is null ? null : _typeSyntaxConverter.Convert(typeNode))
        ?? ParseResolvedType(TypeTextOrNull(typeNode));

    private TypeRef? ParseResolvedType(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? null
            : _typeRefParser.Parse(type);

    private string? ResolveUnary(UnaryExpressionNode unary, IReadOnlyDictionary<string, string> variables) =>
        FormatTypeRef(ResolveUnaryTypeRef(unary, variables));

    private string? ResolveUnary(UnaryExpressionNode unary, TypeEnvironment variables) =>
        FormatTypeRef(ResolveUnaryTypeRef(unary, variables));

    private TypeRef? ResolveUnaryTypeRef(UnaryExpressionNode unary, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveUnaryTypeRef(
            unary,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private TypeRef? ResolveUnaryTypeRef(UnaryExpressionNode unary, TypeEnvironment variables)
    {
        var operandType = ResolveTypeRef(unary.Operand, variables);
        if (operandType is null)
        {
            return null;
        }

        return unary.Operator switch
        {
            "&" => new TypeRef.Pointer(operandType),
            "*" => UnwrapPointer(operandType),
            "!" => ParseResolvedType("bool"),
            "+" => operandType,
            "-" => operandType,
            _ => null,
        };
    }

    private string? ResolveBinary(BinaryExpressionNode binary, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveBinary(
            binary,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private string? ResolveBinary(BinaryExpressionNode binary, TypeEnvironment variables)
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

    private TypeRef? ResolveBinaryTypeRef(BinaryExpressionNode binary, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveBinaryTypeRef(
            binary,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private TypeRef? ResolveBinaryTypeRef(BinaryExpressionNode binary, TypeEnvironment variables)
    {
        if (binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||")
        {
            return ParseResolvedType("bool");
        }

        if (binary.Operator == "<=>")
        {
            return ParseResolvedType("int");
        }

        var left = ResolveTypeRef(binary.Left, variables);
        var right = ResolveTypeRef(binary.Right, variables);
        return SameType(left, right) ? left : left ?? right;
    }

    private string? ResolveConditional(ConditionalExpressionNode conditional, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveConditional(
            conditional,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private string? ResolveConditional(ConditionalExpressionNode conditional, TypeEnvironment variables)
    {
        var whenTrue = Resolve(conditional.WhenTrue, variables);
        var whenFalse = Resolve(conditional.WhenFalse, variables);
        return SameType(whenTrue, whenFalse) ? whenTrue : whenTrue ?? whenFalse;
    }

    private TypeRef? ResolveConditionalTypeRef(ConditionalExpressionNode conditional, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveConditionalTypeRef(
            conditional,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private TypeRef? ResolveConditionalTypeRef(ConditionalExpressionNode conditional, TypeEnvironment variables)
    {
        var whenTrue = ResolveTypeRef(conditional.WhenTrue, variables);
        var whenFalse = ResolveTypeRef(conditional.WhenFalse, variables);
        return SameType(whenTrue, whenFalse) ? whenTrue : whenTrue ?? whenFalse;
    }

    private string? ResolveRange(ScalarRangeExpressionNode range, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveRange(
            range,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private string? ResolveRange(ScalarRangeExpressionNode range, TypeEnvironment variables)
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

    private TypeRef? ResolveRangeTypeRef(ScalarRangeExpressionNode range, IReadOnlyDictionary<string, string> variables) =>
        ParseResolvedType(ResolveRange(range, variables));

    private TypeRef? ResolveRangeTypeRef(ScalarRangeExpressionNode range, TypeEnvironment variables) =>
        ParseResolvedType(ResolveRange(range, variables));

    private static bool IsIntegerLiteral(ExpressionNode expression) =>
        expression is LiteralExpressionNode literal
        && Regex.IsMatch(literal.SourceText.Trim(), @"^-?\d+$");

    private string? ResolveInitializer(InitializerExpressionNode initializer, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveInitializer(
            initializer,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private string? ResolveInitializer(InitializerExpressionNode initializer, TypeEnvironment variables)
    {
        if (initializer.TypeNameNode is not null)
        {
            return TypeText(initializer.TypeNameNode);
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

    private TypeRef? ResolveInitializerTypeRef(InitializerExpressionNode initializer, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveInitializerTypeRef(
            initializer,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private TypeRef? ResolveInitializerTypeRef(InitializerExpressionNode initializer, TypeEnvironment variables)
    {
        if (initializer.TypeNameNode is not null)
        {
            return ResolveTypeNode(initializer.TypeNameNode);
        }

        if (initializer.Values.Count == 0)
        {
            return null;
        }

        var firstType = ResolveTypeRef(initializer.Values[0], variables);
        return initializer.Values
            .Skip(1)
            .Select(value => ResolveTypeRef(value, variables))
            .All(type => SameType(firstType, type))
            ? firstType
            : null;
    }

    private string ResolveFunctionExpression(FunctionExpressionNode functionExpression)
    {
        var returnType = functionExpression.ReturnTypeNode is null
            ? "int"
            : TypeText(functionExpression.ReturnTypeNode);
        return GetFunctionType(functionExpression.Parameters, returnType);
    }

    private TypeRef ResolveFunctionExpressionTypeRef(FunctionExpressionNode functionExpression)
    {
        var parameters = functionExpression.Parameters
            .Select(parameter => ResolveTypeNode(parameter.TypeNode) ?? new TypeRef.Unknown())
            .ToList();
        var returnType = ResolveTypeNode(
                functionExpression.ReturnTypeNode)
            ?? (functionExpression.ReturnTypeNode is null ? ParseResolvedType("int") : null)
            ?? new TypeRef.Unknown();
        return new TypeRef.Function(parameters, returnType);
    }

    private string GetFunctionType(IReadOnlyList<ParameterNode> parameters, string returnType) =>
        $"fn({string.Join(",", parameters.Select(parameter => TypeText(parameter.TypeNode)))})->{returnType}";

    private TypeRef? ResolveMemberTypeRef(MemberExpressionNode member, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveMemberTypeRef(
            member,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private TypeRef? ResolveMemberTypeRef(MemberExpressionNode member, TypeEnvironment variables)
    {
        var targetType = ResolveTypeRef(member.Target, variables);
        if (targetType is null)
        {
            var staticFunctionType = ResolveStaticFunctionReference(member);
            if (staticFunctionType is not null)
            {
                return ParseResolvedType(staticFunctionType);
            }

            var qualifiedName = ExpressionNameFacts.GetQualifiedName(member);
            var global = program.GlobalVariables.FirstOrDefault(global =>
                string.Equals(global.Name, qualifiedName, StringComparison.Ordinal));
            return ResolveTypeNode(global?.TypeNode);
        }

        var normalizedType = TypeRefFacts.StripPointersAndAliases(targetType);
        var normalizedTypeText = TypeRefFormatter.ToCxString(normalizedType);
        var normalizedTypeName = TypeRefFacts.GetBaseName(normalizedType);

        var structNode = ResolveStruct(normalizedType);
        var field = structNode?.Fields.FirstOrDefault(field => field.Name == member.MemberName);
        if (field is not null)
        {
            return ResolveTypeNode(field.TypeNode);
        }

        var union = program.TaggedUnions.FirstOrDefault(union => union.Name == normalizedTypeName);
        var variant = union?.Variants.FirstOrDefault(variant => variant.Name == member.MemberName);
        if (variant is not null)
        {
            return ResolveTypeNode(variant.TypeNode);
        }

        var interfaceNode = program.Interfaces.FirstOrDefault(interfaceNode => interfaceNode.Name == normalizedTypeName);
        if (interfaceNode is not null)
        {
            if (member.MemberName == "state")
            {
                return new TypeRef.Pointer(new TypeRef.Named("void", []));
            }

            var method = interfaceNode.Methods.FirstOrDefault(method => "v_" + method.Name == member.MemberName);
            if (method is not null)
            {
                var parameterTypes = new[] { new TypeRef.Pointer(new TypeRef.Named("void", [])) }
                    .Concat(method.Parameters.Select(parameter => ResolveTypeNode(parameter.TypeNode) ?? new TypeRef.Unknown()))
                    .ToList();
                return new TypeRef.Function(
                    parameterTypes,
                    ResolveTypeNode(method.ReturnTypeNode) ?? new TypeRef.Unknown());
            }
        }

        return null;
    }

    private string? ResolveStaticFunctionReference(MemberExpressionNode member)
    {
        var targetName = ExpressionNameFacts.GetQualifiedName(member.Target);
        if (targetName is null)
        {
            return null;
        }

        var function = program.Functions.FirstOrDefault(function =>
            function.IsStatic
            && OwnerType(function) is not null
            && function.TypeParameters.Count == 0
            && targetName == OwnerType(function)
            && function.Name == member.MemberName);
        return function is null
            ? null
            : GetFunctionType(function.Parameters, TypeText(function.ReturnTypeNode));
    }

    private string? ResolveIndex(IndexExpressionNode index, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveIndex(
            index,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private string? ResolveIndex(IndexExpressionNode index, TypeEnvironment variables)
    {
        var targetType = ResolveTypeRef(index.Target, variables);
        var elementType = targetType is null ? null : ResolveIndexTypeRef(targetType);
        return elementType is null ? null : TypeRefFormatter.ToCxString(elementType);
    }

    private TypeRef? ResolveIndexTypeRef(IndexExpressionNode index, IReadOnlyDictionary<string, string> variables)
    {
        return ResolveIndexTypeRef(
            index,
            TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));
    }

    private TypeRef? ResolveIndexTypeRef(IndexExpressionNode index, TypeEnvironment variables)
    {
        var targetType = ResolveTypeRef(index.Target, variables);
        return targetType switch
        {
            TypeRef.FixedArray fixedArray => fixedArray.Element,
            TypeRef.Pointer pointer => pointer.Element,
            TypeRef.Alias alias => ResolveIndexTypeRef(alias.Target),
            _ => ParseResolvedType(ResolveIndex(index, variables)),
        };
    }

    private static TypeRef? ResolveIndexTypeRef(TypeRef targetType) => targetType switch
    {
        TypeRef.FixedArray fixedArray => fixedArray.Element,
        TypeRef.Pointer pointer => pointer.Element,
        TypeRef.Alias alias => ResolveIndexTypeRef(alias.Target),
        _ => null,
    };

    private string? ResolveCall(CallExpressionNode call, IReadOnlyDictionary<string, string> variables) =>
        ResolveCall(call, TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));

    private string? ResolveCall(CallExpressionNode call, TypeEnvironment variables) =>
        CallResolver.Resolve(call.Callee, [], call.Arguments, variables) is { } resolvedCall
            ? TypeRefFormatter.ToCxString(resolvedCall.ReturnType)
            : null;

    private string? ResolveGenericCall(GenericCallExpressionNode call, IReadOnlyDictionary<string, string> variables) =>
        ResolveGenericCall(call, TypeEnvironment.FromLegacyStrings(_typeRefParser, variables));

    private string? ResolveGenericCall(GenericCallExpressionNode call, TypeEnvironment variables) =>
        CallResolver.Resolve(call.Callee, TypeArguments(call.TypeArgumentNodes), call.Arguments, variables) is { } resolvedCall
            ? TypeRefFormatter.ToCxString(resolvedCall.ReturnType)
            : null;

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
                bindings.Set(parameter, ParseResolvedType(argument) ?? new TypeRef.Unknown());
            }
        }

        for (var i = 0; i < fixedParameters.Count; i++)
        {
            var argumentType = ResolveTypeRef(arguments[i], variables);
            if (argumentType is null)
            {
                return null;
            }

            if (!TryBindType(ResolveTypeNode(fixedParameters[i].TypeNode), argumentType, typeParameters, bindings))
            {
                return null;
            }
        }

        return typeParameters.All(parameter => bindings.Bindings.ContainsKey(parameter))
            ? typeParameters.Select(parameter => TypeRefFormatter.ToCxString(bindings.Bindings[parameter])).ToList()
            : null;
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
    private string? ResolveRaw(string text, IReadOnlyDictionary<string, string> variables)
    {
        text = text.Trim();
        return variables.TryGetValue(text, out var type)
            ? type
            : ResolveLiteral(text);
    }

    private string? ResolveRaw(string text, TypeEnvironment variables)
    {
        text = text.Trim();
        return variables.TryGet(text, out var type)
            ? TypeRefFormatter.ToCxString(type)
            : ResolveLiteral(text);
    }

    private StructNode? ResolveStruct(TypeRef type)
    {
        if (TypeRefFacts.TryGetNamed(type, out var namedType)
            && namedType.Arguments.Count > 0)
        {
            var definition = program.Structs.FirstOrDefault(structNode =>
                structNode.Name == namedType.Name
                && structNode.TypeParameters.Count == namedType.Arguments.Count);
            if (definition is null)
            {
                return null;
            }

            var substitutions = definition.TypeParameters
                .Zip(namedType.Arguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            return definition with
            {
                Fields = definition.Fields
                    .Select(field =>
                    {
                        var substitutedType = TypeRefFormatter.ToCxString(
                            TypeRefRewriter.Substitute(ResolveTypeNode(field.TypeNode) ?? new TypeRef.Unknown(), substitutions));
                        return field with
                        {
                            TypeNode = TypeNode.CreateFromText(field.Location, substitutedType),
                        };
                    })
                    .ToList(),
            };
        }

        var typeName = TypeRefFacts.GetBaseName(type);
        return program.Structs.FirstOrDefault(structNode =>
            structNode.Name == typeName
            && structNode.TypeParameters.Count == 0);
    }

    private static TypeRef? UnwrapPointer(TypeRef type) =>
        TypeRefFacts.TryGetPointerElement(type, out var element) ? element : null;

    private static bool SameType(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static bool SameType(TypeRef? left, TypeRef? right) =>
        TypeRefFacts.SameType(left, right);

    private static bool SameTypeArguments(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

    private string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

    private IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> typeArgumentNodes) =>
        typeArgumentNodes.Select(TypeText).ToList();

    private string TypeText(TypeNode? typeNode) => FormatTypeNode(typeNode, _typeRefParser);

    private string? TypeTextOrNull(TypeNode? typeNode)
    {
        var type = TypeText(typeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }

    private static string FormatTypeNode(TypeNode? typeNode, TypeRefParser parser)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        var type = typeNode.ToTypeRef(parser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private static string? FormatTypeRef(TypeRef? type) =>
        type is null ? null : TypeRefFormatter.ToCxString(type);

}
