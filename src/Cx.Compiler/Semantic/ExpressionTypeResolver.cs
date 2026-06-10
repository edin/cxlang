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
    private readonly IReadOnlyDictionary<string, string> _typeAliases = BuildTypeAliases(program);

    private CallResolver CallResolver => _callResolver ??= new CallResolver(
        program,
        Resolve,
        _currentTypeParameters,
        _currentGenericConstraints);

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
            MemberExpressionNode member => ResolveMember(member, variables),
            CallExpressionNode call => ResolveCall(call, variables),
            GenericCallExpressionNode call => ResolveGenericCall(call, variables),
            IndexExpressionNode index => ResolveIndex(index, variables),
            RawExpressionNode raw => ResolveRaw(raw.SourceText, variables),
            _ => null,
        };
    }

    public TypeRef? ResolveTypeRef(ExpressionNode? expression, IReadOnlyDictionary<string, string> variables)
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

    private TypeRef? ResolveTypeNode(TypeNode? typeNode) =>
        typeNode?.Semantic.Type
        ?? (typeNode?.Syntax is null ? null : _typeSyntaxConverter.Convert(typeNode))
        ?? ParseResolvedType(TypeTextOrNull(typeNode));

    private TypeRef? ParseResolvedType(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? null
            : _typeRefParser.Parse(type);

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

    private TypeRef? ResolveUnaryTypeRef(UnaryExpressionNode unary, IReadOnlyDictionary<string, string> variables)
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
        var whenTrue = Resolve(conditional.WhenTrue, variables);
        var whenFalse = Resolve(conditional.WhenFalse, variables);
        return SameType(whenTrue, whenFalse) ? whenTrue : whenTrue ?? whenFalse;
    }

    private TypeRef? ResolveConditionalTypeRef(ConditionalExpressionNode conditional, IReadOnlyDictionary<string, string> variables)
    {
        var whenTrue = ResolveTypeRef(conditional.WhenTrue, variables);
        var whenFalse = ResolveTypeRef(conditional.WhenFalse, variables);
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

    private TypeRef? ResolveRangeTypeRef(ScalarRangeExpressionNode range, IReadOnlyDictionary<string, string> variables) =>
        ParseResolvedType(ResolveRange(range, variables));

    private static bool IsIntegerLiteral(ExpressionNode expression) =>
        expression is LiteralExpressionNode literal
        && Regex.IsMatch(literal.SourceText.Trim(), @"^-?\d+$");

    private string? ResolveInitializer(InitializerExpressionNode initializer, IReadOnlyDictionary<string, string> variables)
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
            return TypeTextOrNull(global?.TypeNode);
        }

        var isPointer = targetType.TrimEnd().EndsWith("*", StringComparison.Ordinal);
        var normalizedType = StripPointer(ResolveAlias(targetType));

        var structNode = ResolveStruct(normalizedType);
        var field = structNode?.Fields.FirstOrDefault(field => field.Name == member.MemberName);
        if (field is not null)
        {
            return TypeText(field.TypeNode);
        }

        var union = program.TaggedUnions.FirstOrDefault(union => union.Name == normalizedType);
        var variant = union?.Variants.FirstOrDefault(variant => variant.Name == member.MemberName);
        if (variant is not null)
        {
            return TypeText(variant.TypeNode);
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
                    .Concat(method.Parameters.Select(parameter => TypeText(parameter.TypeNode)));
                return $"fn({string.Join(",", parameterTypes)})->{TypeText(method.ReturnTypeNode)}";
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

    private TypeRef? ResolveIndexTypeRef(IndexExpressionNode index, IReadOnlyDictionary<string, string> variables)
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
        CallResolver.Resolve(call.Callee, [], call.Arguments, variables) is { } resolvedCall
            ? TypeRefFormatter.ToCxString(resolvedCall.ReturnType)
            : null;

    private string? ResolveGenericCall(GenericCallExpressionNode call, IReadOnlyDictionary<string, string> variables) =>
        CallResolver.Resolve(call.Callee, TypeArguments(call.TypeArgumentNodes), call.Arguments, variables) is { } resolvedCall
            ? TypeRefFormatter.ToCxString(resolvedCall.ReturnType)
            : null;

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

            if (!TryBindType(TypeText(fixedParameters[i].TypeNode), argumentType, typeParameters, bindings))
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
                    .Select(field =>
                    {
                        var substitutedType = GenericTypeStringRewriter.Substitute(TypeText(field.TypeNode), substitutions);
                        return field with
                        {
                            TypeNode = TypeNode.CreateFromText(field.Location, substitutedType),
                        };
                    })
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

    private static TypeRef? UnwrapPointer(TypeRef type) => type switch
    {
        TypeRef.Pointer pointer => pointer.Element,
        TypeRef.Alias alias => UnwrapPointer(alias.Target),
        _ => null,
    };

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

    private static bool SameType(TypeRef? left, TypeRef? right) =>
        left is not null
        && right is not null
        && string.Equals(
            TypeRefFormatter.ToCxString(left),
            TypeRefFormatter.ToCxString(right),
            StringComparison.Ordinal);

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

    private static IReadOnlyDictionary<string, string> BuildTypeAliases(ProgramNode program)
    {
        var parser = new TypeRefParser(program);
        return program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => FormatTypeNode(group.First().TargetTypeNode, parser), StringComparer.Ordinal);
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

}
