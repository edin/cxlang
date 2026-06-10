using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class TypeInferencePass(DiagnosticBag diagnostics)
{
    private ExpressionTypeResolver? _resolver;
    private TypeSystem? _typeSystem;
    private TypeRefParser? _typeRefParser;
    private ProgramNode? _program;
    private IReadOnlyDictionary<string, string> _globals = new Dictionary<string, string>(StringComparer.Ordinal);

    public ProgramNode Apply(ProgramNode program)
    {
        _program = program;
        _typeRefParser = new TypeRefParser(program);
        _resolver = new ExpressionTypeResolver(program);
        var globalVariables = InferGlobalVariables(program.GlobalVariables);
        var programWithGlobals = program with { GlobalVariables = globalVariables };
        _program = programWithGlobals;
        _typeRefParser = new TypeRefParser(programWithGlobals);
        _resolver = new ExpressionTypeResolver(programWithGlobals);
        _typeSystem = new TypeSystem(programWithGlobals);
        _globals = globalVariables
            .Where(global => !string.IsNullOrWhiteSpace(TypeText(global.TypeNode)))
            .GroupBy(global => global.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TypeText(group.First().TypeNode), StringComparer.Ordinal);

        return programWithGlobals with
        {
            Functions = program.Functions.Select(InferFunction).ToList(),
            Structs = program.Structs.Select(structNode => structNode with
            {
                Methods = structNode.Methods.Select(InferFunction).ToList(),
            }).ToList(),
            TaggedUnions = program.TaggedUnions.Select(union => union with
            {
                Methods = union.Methods.Select(InferFunction).ToList(),
            }).ToList(),
        };
    }

    private IReadOnlyList<GlobalVariableNode> InferGlobalVariables(IReadOnlyList<GlobalVariableNode> globals)
    {
        var variables = globals
            .Where(global => !string.IsNullOrWhiteSpace(TypeText(global.TypeNode)))
            .GroupBy(global => global.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TypeText(group.First().TypeNode), StringComparer.Ordinal);
        var inferred = new List<GlobalVariableNode>();

        foreach (var global in globals)
        {
            var initializer = InferExpression(global.Initializer, variables);
            var type = InferVariableType(
                global.Location,
                global.Name,
                TypeText(global.TypeNode),
                initializer,
                variables,
                "global");

            if (!string.IsNullOrWhiteSpace(type))
            {
                variables[global.Name] = type;
            }

            inferred.Add(global with
            {
                TypeNode = CreateInferredTypeNode(global.Location, type),
                Initializer = initializer,
            });
        }

        return inferred;
    }

    private FunctionNode InferFunction(FunctionNode function)
    {
        var variables = _globals
            .Concat(function.Parameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => new KeyValuePair<string, string>(parameter.Name, TypeText(parameter.TypeNode))))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

        return function with
        {
            Body = InferStatements(function.Body, variables),
        };
    }

    private IReadOnlyList<StatementNode> InferStatements(
        IReadOnlyList<StatementNode> statements,
        Dictionary<string, string> variables)
    {
        var inferred = new List<StatementNode>();
        foreach (var statement in statements)
        {
            inferred.Add(InferStatement(statement, variables));
        }

        return inferred;
    }

    private StatementNode InferStatement(
        StatementNode statement,
        Dictionary<string, string> variables) => statement switch
    {
        LetStatement let => InferLetStatement(let, variables),
        ReturnStatement ret => ret with { Expression = InferExpression(ret.Expression, variables) },
        CStatement c => c with { Expression = InferExpression(c.Expression, variables)! },
        IfStatement ifStatement => ifStatement with
        {
            Condition = InferExpression(ifStatement.Condition, variables)!,
            ThenBody = InferStatements(ifStatement.ThenBody, CopyVariables(variables)),
            ElseBranch = ifStatement.ElseBranch is null
                ? null
                : InferStatement(ifStatement.ElseBranch, CopyVariables(variables)),
        },
        ElseBlockStatement elseBlock => elseBlock with
        {
            Body = InferStatements(elseBlock.Body, CopyVariables(variables)),
        },
        WhileStatement whileStatement => whileStatement with
        {
            Condition = InferExpression(whileStatement.Condition, variables)!,
            Body = InferStatements(whileStatement.Body, CopyVariables(variables)),
        },
        ForStatement forStatement => InferForStatement(forStatement, variables),
        ForeachStatement foreachStatement => InferForeachStatement(foreachStatement, variables),
        SwitchStatement switchStatement => switchStatement with
        {
            Expression = InferExpression(switchStatement.Expression, variables)!,
            Cases = switchStatement.Cases.Select(switchCase => switchCase with
            {
                Pattern = InferExpression(switchCase.Pattern, variables)!,
                Body = InferStatements(switchCase.Body, CopyVariables(variables)),
            }).ToList(),
            DefaultBody = InferStatements(switchStatement.DefaultBody, CopyVariables(variables)),
        },
        MatchStatement matchStatement => matchStatement with
        {
            Expression = InferExpression(matchStatement.Expression, variables)!,
            Arms = matchStatement.Arms.Select(arm => arm with
            {
                Body = InferStatements(arm.Body, CopyVariables(variables)),
            }).ToList(),
        },
        _ => statement,
    };

    private LetStatement InferLetStatement(LetStatement let, Dictionary<string, string> variables)
    {
        var initializer = InferExpression(let.Initializer, variables);
        var type = InferVariableType(let.Location, let.Name, TypeText(let.TypeNode), initializer, variables, "local");
        if (!string.IsNullOrWhiteSpace(type))
        {
            variables[let.Name] = type;
        }

        return let with
        {
            TypeNode = CreateInferredTypeNode(let.Location, type),
            Initializer = initializer,
        };
    }

    private ForStatement InferForStatement(ForStatement forStatement, Dictionary<string, string> variables)
    {
        var forVariables = CopyVariables(variables);
        var initializer = InferForInitializer(forStatement.Initializer, forVariables);
        return forStatement with
        {
            Initializer = initializer,
            Condition = InferExpression(forStatement.Condition, forVariables)!,
            Increment = InferExpression(forStatement.Increment, forVariables)!,
            Body = InferStatements(forStatement.Body, forVariables),
        };
    }

    private ForInitializerNode InferForInitializer(
        ForInitializerNode initializer,
        Dictionary<string, string> variables) => initializer switch
    {
        ForDeclarationInitializerNode declaration => InferForDeclarationInitializer(declaration, variables),
        ForExpressionInitializerNode expression => expression with
        {
            Expression = InferExpression(expression.Expression, variables)!,
        },
        _ => initializer,
    };

    private ForDeclarationInitializerNode InferForDeclarationInitializer(
        ForDeclarationInitializerNode declaration,
        Dictionary<string, string> variables)
    {
        var initializer = InferExpression(declaration.Initializer, variables);
        var type = InferVariableType(
            declaration.Location,
            declaration.Name,
            TypeText(declaration.TypeNode),
            initializer,
            variables,
            "for variable");
        if (!string.IsNullOrWhiteSpace(type))
        {
            variables[declaration.Name] = type;
        }

        return declaration with
        {
            TypeNode = CreateInferredTypeNode(declaration.Location, type),
            Initializer = initializer,
        };
    }

    private static TypeNode? CreateInferredTypeNode(Location location, string type) =>
        string.IsNullOrWhiteSpace(type)
            ? null
            : TypeNode.CreateFromText(location, type);

    private ForeachStatement InferForeachStatement(ForeachStatement foreachStatement, Dictionary<string, string> variables)
    {
        var iterableExpression = InferExpression(foreachStatement.IterableExpression, variables);
        var foreachVariables = CopyVariables(variables);
        var iterableType = _resolver?.Resolve(iterableExpression, variables);
        string? elementType = null;
        string? keyType = null;
        if (iterableExpression is ScalarRangeExpressionNode && iterableType is not null)
        {
            elementType = iterableType;
            AddForeachBindings(foreachStatement, foreachVariables, elementType);
        }
        else if (iterableType is not null
            && TryResolveForeachTypes(foreachStatement, iterableType, out elementType, out keyType))
        {
            if (foreachStatement.KeyBinding is { } keyBinding && keyType is not null)
            {
                var keyBindingType = TypeTextOrNull(keyBinding.TypeNode);
                foreachVariables[keyBinding.Name] = keyBindingType is null
                    ? keyType
                    : keyBindingType;
            }

            AddForeachBindings(foreachStatement, foreachVariables, elementType);
        }

        var typedForeachStatement = elementType is null
            ? foreachStatement
            : ApplyForeachBindingTypes(foreachStatement, elementType, keyType);

        return foreachStatement with
        {
            IterableExpression = iterableExpression!,
            IndexBinding = typedForeachStatement.IndexBinding,
            KeyBinding = typedForeachStatement.KeyBinding,
            ValueBinding = typedForeachStatement.ValueBinding,
            Body = InferStatements(foreachStatement.Body, foreachVariables),
        };
    }

    private ForeachStatement ApplyForeachBindingTypes(
        ForeachStatement foreachStatement,
        string elementType,
        string? keyType)
    {
        return foreachStatement with
        {
            IndexBinding = foreachStatement.IndexBinding is null
                ? null
                : FillBindingType(foreachStatement.IndexBinding, "usize"),
            KeyBinding = foreachStatement.KeyBinding is null || keyType is null
                ? foreachStatement.KeyBinding
                : FillBindingType(foreachStatement.KeyBinding, keyType),
            ValueBinding = FillBindingType(foreachStatement.ValueBinding, elementType),
        };
    }

    private ForeachBinding FillBindingType(ForeachBinding binding, string inferredType) =>
        string.IsNullOrWhiteSpace(TypeText(binding.TypeNode))
            ? binding with { TypeNode = CreateInferredTypeNode(binding.Location, inferredType) }
            : binding;

    private void AddForeachBindings(
        ForeachStatement foreachStatement,
        Dictionary<string, string> variables,
        string elementType)
    {
        if (foreachStatement.IndexBinding is { } indexBinding)
        {
            var indexBindingType = TypeTextOrNull(indexBinding.TypeNode);
            variables[indexBinding.Name] = indexBindingType is null
                ? "usize"
                : indexBindingType;
        }

        var valueBindingType = TypeTextOrNull(foreachStatement.ValueBinding.TypeNode);
        var valueType = valueBindingType is null
            ? elementType
            : valueBindingType;
        variables[foreachStatement.ValueBinding.Name] = valueType;
    }

    private bool TryResolveForeachTypes(
        ForeachStatement foreachStatement,
        string iterableType,
        out string elementType,
        out string? keyType)
    {
        elementType = string.Empty;
        keyType = null;

        return _typeSystem?.TryResolveForeachTypes(
            iterableType,
            keyValue: foreachStatement.KeyBinding is not null,
            out elementType,
            out keyType) == true;
    }

    private string InferVariableType(
        Cx.Compiler.Syntax.Location location,
        string name,
        string declaredType,
        ExpressionNode? initializer,
        IReadOnlyDictionary<string, string> variables,
        string subject)
    {
        if (!string.IsNullOrWhiteSpace(declaredType))
        {
            return declaredType;
        }

        if (initializer is null)
        {
            diagnostics.Report(location, $"Cannot infer type for {subject} '{name}' without an initializer.");
            return declaredType;
        }

        if (initializer is LiteralExpressionNode { SourceText: "null" })
        {
            diagnostics.Report(location, $"Cannot infer type for {subject} '{name}' from null; write an explicit pointer type.");
            return declaredType;
        }

        if (initializer is InitializerExpressionNode { TypeNameNode: null })
        {
            diagnostics.Report(location, $"Cannot infer type for {subject} '{name}' from an untyped initializer; write an explicit type.");
            return declaredType;
        }

        var inferredType = _resolver?.Resolve(initializer, variables);
        if (string.IsNullOrWhiteSpace(inferredType) || inferredType == "null")
        {
            diagnostics.Report(
                location,
                BuildUnknownExpressionTypeDiagnostic(subject, name, initializer, variables));
            return declaredType;
        }

        return inferredType;
    }

    private string BuildUnknownExpressionTypeDiagnostic(
        string subject,
        string name,
        ExpressionNode initializer,
        IReadOnlyDictionary<string, string> variables)
    {
        if (TryBuildGenericInferenceDiagnostic(subject, name, initializer, variables) is { } diagnostic)
        {
            return diagnostic;
        }

        return $"Cannot infer type for {subject} '{name}'; expression type is unknown.";
    }

    private string? TryBuildGenericInferenceDiagnostic(
        string subject,
        string name,
        ExpressionNode initializer,
        IReadOnlyDictionary<string, string> variables)
    {
        if (_program is null || _resolver is null)
        {
            return null;
        }

        initializer = UnwrapParentheses(initializer);
        if (initializer is not CallExpressionNode call)
        {
            return null;
        }

        if (call.Callee is NameExpressionNode functionName)
        {
            var function = _program.Functions.FirstOrDefault(function =>
                OwnerType(function) is null
                && function.Name == functionName.SourceText
                && function.TypeParameters.Count > 0);
            if (function is null
                || _resolver.InferFunctionTypeArguments(function.TypeParameters, function.Parameters, call.Arguments, variables, skipSelf: false) is not null)
            {
                return null;
            }

            return BuildGenericCallDiagnostic(subject, name, function, function.Name, function.Name, call.Arguments, skipSelf: false);
        }

        if (call.Callee is not MemberExpressionNode member || GetQualifiedName(member.Target) is not { } targetName)
        {
            return null;
        }

        if (!variables.TryGetValue(targetName, out _))
        {
            var staticFunction = _program.Functions.FirstOrDefault(function =>
                function.IsStatic
                && OwnerType(function) == targetName
                && function.Name == member.MemberName
                && function.TypeParameters.Count > 0);
            if (staticFunction is null
                || _resolver.InferFunctionTypeArguments(staticFunction.TypeParameters, staticFunction.Parameters, call.Arguments, variables, skipSelf: false) is not null)
            {
                return null;
            }

            return BuildGenericCallDiagnostic(
                subject,
                name,
                staticFunction,
                $"{targetName}.{member.MemberName}",
                $"{targetName}<int>.{member.MemberName}",
                call.Arguments,
                skipSelf: false);
        }

        return null;
    }

    private string BuildGenericCallDiagnostic(
        string subject,
        string variableName,
        FunctionNode function,
        string callName,
        string suggestedCall,
        IReadOnlyList<ExpressionNode> arguments,
        bool skipSelf)
    {
        var fixedParameters = function.Parameters
            .Skip(skipSelf ? 1 : 0)
            .Where(parameter => !parameter.IsVariadic)
            .ToList();
        var unbound = function.TypeParameters
            .Where(typeParameter =>
                !fixedParameters.Any(parameter => TypeMentionsParameter(TypeText(parameter.TypeNode), typeParameter))
                && TypeMentionsParameter(TypeText(function.ReturnTypeNode), typeParameter))
            .ToList();

        if (unbound.Count == 0)
        {
            return $"Cannot infer type for {subject} '{variableName}'; generic type arguments for '{callName}' could not be inferred from arguments.";
        }

        var parameterText = string.Join(", ", unbound.Select(parameter => $"'{parameter}'"));
        var plural = unbound.Count == 1 ? "parameter" : "parameters";
        var pronoun = unbound.Count == 1 ? "it" : "they";
        var appears = unbound.Count == 1 ? "appears" : "appear";
        var argumentText = arguments.Count == 0 ? "no arguments" : "the provided arguments";
        var suggestion = function.IsStatic && OwnerType(function) is not null
            ? $" Try '{suggestedCall}(...)' and replace 'int' with the desired type."
            : $" Try '{suggestedCall}<int>(...)' and replace 'int' with the desired type.";

        return $"Cannot infer type for {subject} '{variableName}'; generic type {plural} {parameterText} for '{callName}' cannot be inferred from {argumentText} because {pronoun} only {appears} in the return type.{suggestion}";
    }

    private static bool TypeMentionsParameter(string type, string typeParameter) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            type,
            $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(typeParameter)}(?![A-Za-z0-9_])");

    private static ExpressionNode UnwrapParentheses(ExpressionNode expression) =>
        expression is ParenthesizedExpressionNode parenthesized
            ? UnwrapParentheses(parenthesized.Expression)
            : expression;

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private ExpressionNode? InferExpression(ExpressionNode? expression, IReadOnlyDictionary<string, string> variables)
    {
        if (expression is null)
        {
            return null;
        }

        return expression switch
        {
            ParenthesizedExpressionNode parenthesized => parenthesized with
            {
                Expression = InferExpression(parenthesized.Expression, variables)!,
            },
            CastExpressionNode cast => cast with
            {
                Expression = InferExpression(cast.Expression, variables)!,
            },
            UnaryExpressionNode unary => unary with
            {
                Operand = InferExpression(unary.Operand, variables)!,
            },
            PostfixExpressionNode postfix => postfix with
            {
                Operand = InferExpression(postfix.Operand, variables)!,
            },
            SizeOfExpressionNode sizeOf => sizeOf with
            {
                ExpressionOperand = InferExpression(sizeOf.ExpressionOperand, variables),
            },
            BinaryExpressionNode binary => binary with
            {
                Left = InferExpression(binary.Left, variables)!,
                Right = InferExpression(binary.Right, variables)!,
            },
            ScalarRangeExpressionNode range => range with
            {
                Start = InferExpression(range.Start, variables)!,
                End = InferExpression(range.End, variables)!,
            },
            ConditionalExpressionNode conditional => conditional with
            {
                Condition = InferExpression(conditional.Condition, variables)!,
                WhenTrue = InferExpression(conditional.WhenTrue, variables)!,
                WhenFalse = InferExpression(conditional.WhenFalse, variables)!,
            },
            InitializerExpressionNode initializer => initializer with
            {
                Fields = initializer.Fields.Select(field => field with
                {
                    Value = InferExpression(field.Value, variables)!,
                }).ToList(),
                Values = initializer.Values
                    .Select(value => InferExpression(value, variables)!)
                    .ToList(),
            },
            FunctionExpressionNode function => InferFunctionExpression(function, variables),
            AssignmentExpressionNode assignment => assignment with
            {
                Target = InferExpression(assignment.Target, variables)!,
                Value = InferExpression(assignment.Value, variables)!,
            },
            CallExpressionNode call => call with
            {
                Callee = InferExpression(call.Callee, variables)!,
                Arguments = call.Arguments
                    .Select(argument => InferExpression(argument, variables)!)
                    .ToList(),
            },
            GenericCallExpressionNode call => call with
            {
                Callee = InferExpression(call.Callee, variables)!,
                Arguments = call.Arguments
                    .Select(argument => InferExpression(argument, variables)!)
                    .ToList(),
            },
            MemberExpressionNode member => member with
            {
                Target = InferExpression(member.Target, variables)!,
            },
            IndexExpressionNode index => index with
            {
                Target = InferExpression(index.Target, variables)!,
                Index = InferExpression(index.Index, variables)!,
            },
            _ => expression,
        };
    }

    private FunctionExpressionNode InferFunctionExpression(
        FunctionExpressionNode function,
        IReadOnlyDictionary<string, string> variables)
    {
        var functionVariables = CopyVariables(variables);
        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            functionVariables[parameter.Name] = TypeText(parameter.TypeNode);
        }

        return function with
        {
            ExpressionBody = InferExpression(function.ExpressionBody, functionVariables),
            BlockBody = function.BlockBody is null
                ? null
                : InferStatements(function.BlockBody, functionVariables),
        };
    }

    private static Dictionary<string, string> CopyVariables(IReadOnlyDictionary<string, string> variables) =>
        new(variables, StringComparer.Ordinal);

    private string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

    private string TypeText(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        if (_typeRefParser is null)
        {
            throw new InvalidOperationException("Type inference has no TypeRef parser.");
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
