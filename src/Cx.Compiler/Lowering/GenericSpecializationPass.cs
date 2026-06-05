using System.Text.RegularExpressions;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class GenericSpecializationPass
{
    public static ProgramNode Apply(ProgramNode program, DiagnosticBag diagnostics)
    {
        if (diagnostics.HasErrors)
        {
            return program;
        }

        var specializations = new Dictionary<string, FunctionNode>(StringComparer.Ordinal);
        var pending = new Queue<GenericFunctionUse>();

        foreach (var expression in EnumerateExpressions(program))
        {
            EnqueueResolvedUse(expression, pending);
        }

        while (pending.TryDequeue(out var use))
        {
            var key = Key(use.Function, use.TypeArguments);
            if (specializations.ContainsKey(key)
                || use.Function.TypeParameters.Count != use.TypeArguments.Count)
            {
                continue;
            }

            var specialized = InstantiateFunction(use.Function, use.TypeArguments);
            specializations.Add(key, specialized);
            foreach (var expression in EnumerateExpressions(specialized.Body))
            {
                EnqueueResolvedUse(expression, pending);
            }
        }

        var concreteStructs = SpecializeStructs(program, specializations.Values);
        var concreteStructNames = concreteStructs
            .Select(structNode => structNode.Name)
            .ToHashSet(StringComparer.Ordinal);
        var loweredProgram = concreteStructNames.Count == 0
            ? program
            : RewriteGenericStructTypeReferences(program, concreteStructNames);
        var loweredSpecializations = concreteStructNames.Count == 0
            ? specializations
            : specializations.ToDictionary(
                pair => pair.Key,
                pair => RewriteFunctionGenericStructTypeReferences(pair.Value, concreteStructNames),
                StringComparer.Ordinal);

        RetargetResolvedGenericCalls(loweredProgram, loweredSpecializations);
        RetargetResolvedGenericCalls(loweredSpecializations.Values, loweredSpecializations);
        if (specializations.Count == 0 && concreteStructs.Count == 0)
        {
            return loweredProgram;
        }

        return loweredProgram with
        {
            Structs = loweredProgram.Structs.Concat(concreteStructs).ToList(),
            Functions = loweredProgram.Functions.Concat(loweredSpecializations.Values).ToList(),
        };
    }

    private static ProgramNode RewriteGenericStructTypeReferences(
        ProgramNode program,
        IReadOnlySet<string> concreteStructNames) =>
        program with
        {
            ExternFunctions = program.ExternFunctions
                .Select(function => function with
                {
                    ReturnType = RewriteConcreteGenericStructTypes(function.ReturnType, concreteStructNames),
                    Parameters = RewriteParameters(function.Parameters, concreteStructNames),
                })
                .ToList(),
            TypeAliases = program.TypeAliases
                .Select(alias => alias with { TargetType = RewriteConcreteGenericStructTypes(alias.TargetType, concreteStructNames) })
                .ToList(),
            Structs = program.Structs
                .Select(structNode => RewriteStructGenericStructTypeReferences(structNode, concreteStructNames))
                .ToList(),
            TypeAdapters = program.TypeAdapters
                .Select(adapter => adapter with
                {
                    BaseType = RewriteConcreteGenericStructTypes(adapter.BaseType, concreteStructNames),
                    ExposedMethods = adapter.ExposedMethods
                        .Select(method => method with
                        {
                            ReturnType = method.ReturnType is null
                                ? null
                                : RewriteConcreteGenericStructTypes(method.ReturnType, concreteStructNames),
                        })
                        .ToList(),
                    Methods = adapter.Methods
                        .Select(method => RewriteFunctionGenericStructTypeReferences(method, concreteStructNames))
                        .ToList(),
                })
                .ToList(),
            Extensions = program.Extensions
                .Select(extension => extension with
                {
                    TargetType = RewriteConcreteGenericStructTypes(extension.TargetType, concreteStructNames),
                    Methods = extension.Methods
                        .Select(method => RewriteFunctionGenericStructTypeReferences(method, concreteStructNames))
                        .ToList(),
                })
                .ToList(),
            TaggedUnions = program.TaggedUnions
                .Select(taggedUnion => taggedUnion with
                {
                    Variants = taggedUnion.Variants
                        .Select(variant => variant with
                        {
                            Type = RewriteConcreteGenericStructTypes(variant.Type, concreteStructNames),
                        })
                        .ToList(),
                    Methods = taggedUnion.Methods
                        .Select(method => RewriteFunctionGenericStructTypeReferences(method, concreteStructNames))
                        .ToList(),
                })
                .ToList(),
            GlobalVariables = program.GlobalVariables
                .Select(global => global with
                {
                    Type = RewriteConcreteGenericStructTypes(global.Type, concreteStructNames),
                    Initializer = global.Initializer is null
                        ? null
                        : RewriteExpressionGenericStructTypeReferences(global.Initializer, concreteStructNames),
                })
                .ToList(),
            Functions = program.Functions
                .Select(function => RewriteFunctionGenericStructTypeReferences(function, concreteStructNames))
                .ToList(),
            Tests = program.Tests
                .Select(test => test with
                {
                    Body = test.Body
                        .Select(statement => RewriteStatementGenericStructTypeReferences(statement, concreteStructNames))
                        .ToList(),
                })
                .ToList(),
        };

    private static StructNode RewriteStructGenericStructTypeReferences(
        StructNode structNode,
        IReadOnlySet<string> concreteStructNames) =>
        structNode with
        {
            Requirements = RewriteStructRequirements(structNode.Requirements, concreteStructNames),
            Fields = structNode.Fields
                .Select(field => field with { Type = RewriteConcreteGenericStructTypes(field.Type, concreteStructNames) })
                .ToList(),
            Methods = structNode.Methods
                .Select(method => RewriteFunctionGenericStructTypeReferences(method, concreteStructNames))
                .ToList(),
        };

    private static FunctionNode RewriteFunctionGenericStructTypeReferences(
        FunctionNode function,
        IReadOnlySet<string> concreteStructNames)
    {
        var rewritten = function with
        {
            OwnerType = function.OwnerType is null
                ? null
                : RewriteConcreteGenericStructTypes(function.OwnerType, concreteStructNames),
            GenericConstraints = RewriteGenericConstraints(function.GenericConstraints, concreteStructNames),
            ReturnType = RewriteConcreteGenericStructTypes(function.ReturnType, concreteStructNames),
            Parameters = RewriteParameters(function.Parameters, concreteStructNames),
            Body = function.Body
                .Select(statement => RewriteStatementGenericStructTypeReferences(statement, concreteStructNames))
                .ToList(),
        };
        return CopySemantic(function, rewritten);
    }

    private static IReadOnlyList<ParameterNode> RewriteParameters(
        IReadOnlyList<ParameterNode> parameters,
        IReadOnlySet<string> concreteStructNames) =>
        parameters
            .Select(parameter => parameter.IsVariadic
                ? parameter
                : parameter with { Type = RewriteConcreteGenericStructTypes(parameter.Type, concreteStructNames) })
            .ToList();

    private static IReadOnlyList<GenericConstraintNode> RewriteGenericConstraints(
        IReadOnlyList<GenericConstraintNode> constraints,
        IReadOnlySet<string> concreteStructNames) =>
        constraints
            .Select(constraint => constraint with
            {
                Requirements = RewriteStructRequirements(constraint.Requirements, concreteStructNames),
            })
            .ToList();

    private static IReadOnlyList<StructRequirementNode> RewriteStructRequirements(
        IReadOnlyList<StructRequirementNode> requirements,
        IReadOnlySet<string> concreteStructNames) =>
        requirements
            .Select(requirement => requirement with
            {
                TypeArguments = requirement.TypeArguments
                    .Select(argument => RewriteConcreteGenericStructTypes(argument, concreteStructNames))
                    .ToList(),
            })
            .ToList();

    private static StatementNode RewriteStatementGenericStructTypeReferences(
        StatementNode statement,
        IReadOnlySet<string> concreteStructNames)
    {
        var rewritten = statement switch
        {
            LetStatement let => let with
            {
                Type = RewriteConcreteGenericStructTypes(let.Type, concreteStructNames),
                Initializer = RewriteOptionalExpressionGenericStructTypeReferences(let.Initializer, concreteStructNames),
            },
            ReturnStatement ret => ret with
            {
                Expression = RewriteExpressionGenericStructTypeReferences(ret.Expression, concreteStructNames),
            },
            CStatement c => c with
            {
                Expression = RewriteExpressionGenericStructTypeReferences(c.Expression, concreteStructNames),
            },
            IfStatement ifStatement => ifStatement with
            {
                Condition = RewriteExpressionGenericStructTypeReferences(ifStatement.Condition, concreteStructNames),
                ThenBody = ifStatement.ThenBody
                    .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                    .ToList(),
                ElseBranch = ifStatement.ElseBranch is null
                    ? null
                    : RewriteStatementGenericStructTypeReferences(ifStatement.ElseBranch, concreteStructNames),
            },
            ElseBlockStatement elseBlock => elseBlock with
            {
                Body = elseBlock.Body
                    .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                    .ToList(),
            },
            WhileStatement whileStatement => whileStatement with
            {
                Condition = RewriteExpressionGenericStructTypeReferences(whileStatement.Condition, concreteStructNames),
                Body = whileStatement.Body
                    .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                    .ToList(),
            },
            ForStatement forStatement => forStatement with
            {
                Initializer = RewriteForInitializerGenericStructTypeReferences(forStatement.Initializer, concreteStructNames),
                Condition = RewriteExpressionGenericStructTypeReferences(forStatement.Condition, concreteStructNames),
                Increment = RewriteExpressionGenericStructTypeReferences(forStatement.Increment, concreteStructNames),
                Body = forStatement.Body
                    .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                    .ToList(),
            },
            ForeachStatement foreachStatement => foreachStatement with
            {
                IndexBinding = foreachStatement.IndexBinding is null
                    ? null
                    : RewriteForeachBinding(foreachStatement.IndexBinding, concreteStructNames),
                KeyBinding = foreachStatement.KeyBinding is null
                    ? null
                    : RewriteForeachBinding(foreachStatement.KeyBinding, concreteStructNames),
                ValueBinding = RewriteForeachBinding(foreachStatement.ValueBinding, concreteStructNames),
                IterableExpression = RewriteExpressionGenericStructTypeReferences(foreachStatement.IterableExpression, concreteStructNames),
                Body = foreachStatement.Body
                    .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                    .ToList(),
            },
            SwitchStatement switchStatement => switchStatement with
            {
                Expression = RewriteExpressionGenericStructTypeReferences(switchStatement.Expression, concreteStructNames),
                Cases = switchStatement.Cases
                    .Select(switchCase => switchCase with
                    {
                        Pattern = RewriteExpressionGenericStructTypeReferences(switchCase.Pattern, concreteStructNames),
                        Body = switchCase.Body
                            .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                            .ToList(),
                    })
                    .ToList(),
                DefaultBody = switchStatement.DefaultBody
                    .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                    .ToList(),
            },
            MatchStatement matchStatement => matchStatement with
            {
                Expression = RewriteExpressionGenericStructTypeReferences(matchStatement.Expression, concreteStructNames),
                Arms = matchStatement.Arms
                    .Select(arm => arm with
                    {
                        Body = arm.Body
                            .Select(nested => RewriteStatementGenericStructTypeReferences(nested, concreteStructNames))
                            .ToList(),
                    })
                    .ToList(),
            },
            _ => statement,
        };

        return CopySemantic(statement, rewritten);
    }

    private static ForInitializerNode RewriteForInitializerGenericStructTypeReferences(
        ForInitializerNode initializer,
        IReadOnlySet<string> concreteStructNames)
    {
        var rewritten = initializer switch
        {
            ForDeclarationInitializerNode declaration => declaration with
            {
                Type = RewriteConcreteGenericStructTypes(declaration.Type, concreteStructNames),
                Initializer = RewriteOptionalExpressionGenericStructTypeReferences(declaration.Initializer, concreteStructNames),
            },
            ForExpressionInitializerNode expression => expression with
            {
                Expression = RewriteExpressionGenericStructTypeReferences(expression.Expression, concreteStructNames),
            },
            _ => initializer,
        };
        return CopySemantic(initializer, rewritten);
    }

    private static ForeachBinding RewriteForeachBinding(
        ForeachBinding binding,
        IReadOnlySet<string> concreteStructNames) =>
        CopySemantic(binding, binding with
        {
            Type = RewriteConcreteGenericStructTypes(binding.Type, concreteStructNames),
        });

    private static ExpressionNode? RewriteOptionalExpressionGenericStructTypeReferences(
        ExpressionNode? expression,
        IReadOnlySet<string> concreteStructNames) =>
        expression is null ? null : RewriteExpressionGenericStructTypeReferences(expression, concreteStructNames);

    private static ExpressionNode RewriteExpressionGenericStructTypeReferences(
        ExpressionNode expression,
        IReadOnlySet<string> concreteStructNames)
    {
        var rewritten = expression switch
        {
            ParenthesizedExpressionNode parenthesized => parenthesized with
            {
                Expression = RewriteExpressionGenericStructTypeReferences(parenthesized.Expression, concreteStructNames),
            },
            CastExpressionNode cast => cast with
            {
                TargetType = RewriteConcreteGenericStructTypes(cast.TargetType, concreteStructNames),
                Expression = RewriteExpressionGenericStructTypeReferences(cast.Expression, concreteStructNames),
            },
            UnaryExpressionNode unary => unary with
            {
                Operand = RewriteExpressionGenericStructTypeReferences(unary.Operand, concreteStructNames),
            },
            PostfixExpressionNode postfix => postfix with
            {
                Operand = RewriteExpressionGenericStructTypeReferences(postfix.Operand, concreteStructNames),
            },
            SizeOfExpressionNode sizeOf => sizeOf with
            {
                TypeOperand = sizeOf.TypeOperand is null
                    ? null
                    : RewriteConcreteGenericStructTypes(sizeOf.TypeOperand, concreteStructNames),
                ExpressionOperand = RewriteOptionalExpressionGenericStructTypeReferences(sizeOf.ExpressionOperand, concreteStructNames),
            },
            BinaryExpressionNode binary => binary with
            {
                Left = RewriteExpressionGenericStructTypeReferences(binary.Left, concreteStructNames),
                Right = RewriteExpressionGenericStructTypeReferences(binary.Right, concreteStructNames),
            },
            ScalarRangeExpressionNode range => range with
            {
                Start = RewriteExpressionGenericStructTypeReferences(range.Start, concreteStructNames),
                End = RewriteExpressionGenericStructTypeReferences(range.End, concreteStructNames),
            },
            ConditionalExpressionNode conditional => conditional with
            {
                Condition = RewriteExpressionGenericStructTypeReferences(conditional.Condition, concreteStructNames),
                WhenTrue = RewriteExpressionGenericStructTypeReferences(conditional.WhenTrue, concreteStructNames),
                WhenFalse = RewriteExpressionGenericStructTypeReferences(conditional.WhenFalse, concreteStructNames),
            },
            InitializerExpressionNode initializer => initializer with
            {
                TypeName = initializer.TypeName is null
                    ? null
                    : RewriteConcreteGenericStructTypes(initializer.TypeName, concreteStructNames),
                Fields = initializer.Fields
                    .Select(field => field with
                    {
                        Value = RewriteExpressionGenericStructTypeReferences(field.Value, concreteStructNames),
                    })
                    .ToList(),
                Values = initializer.Values
                    .Select(value => RewriteExpressionGenericStructTypeReferences(value, concreteStructNames))
                    .ToList(),
            },
            FunctionExpressionNode functionExpression => functionExpression with
            {
                Parameters = RewriteParameters(functionExpression.Parameters, concreteStructNames),
                ReturnType = functionExpression.ReturnType is null
                    ? null
                    : RewriteConcreteGenericStructTypes(functionExpression.ReturnType, concreteStructNames),
                ExpressionBody = RewriteOptionalExpressionGenericStructTypeReferences(functionExpression.ExpressionBody, concreteStructNames),
                BlockBody = functionExpression.BlockBody?
                    .Select(statement => RewriteStatementGenericStructTypeReferences(statement, concreteStructNames))
                    .ToList(),
            },
            AssignmentExpressionNode assignment => assignment with
            {
                Target = RewriteExpressionGenericStructTypeReferences(assignment.Target, concreteStructNames),
                Value = RewriteExpressionGenericStructTypeReferences(assignment.Value, concreteStructNames),
            },
            CallExpressionNode call => call with
            {
                Callee = RewriteExpressionGenericStructTypeReferences(call.Callee, concreteStructNames),
                Arguments = call.Arguments
                    .Select(argument => RewriteExpressionGenericStructTypeReferences(argument, concreteStructNames))
                    .ToList(),
            },
            GenericCallExpressionNode call => call with
            {
                Callee = RewriteExpressionGenericStructTypeReferences(call.Callee, concreteStructNames),
                TypeArguments = call.TypeArguments
                    .Select(argument => RewriteConcreteGenericStructTypes(argument, concreteStructNames))
                    .ToList(),
                Arguments = call.Arguments
                    .Select(argument => RewriteExpressionGenericStructTypeReferences(argument, concreteStructNames))
                    .ToList(),
            },
            MemberExpressionNode member => member with
            {
                Target = RewriteExpressionGenericStructTypeReferences(member.Target, concreteStructNames),
            },
            IndexExpressionNode index => index with
            {
                Target = RewriteExpressionGenericStructTypeReferences(index.Target, concreteStructNames),
                Index = RewriteExpressionGenericStructTypeReferences(index.Index, concreteStructNames),
            },
            _ => expression,
        };

        return CopySemantic(expression, rewritten);
    }

    private static IReadOnlyList<StructNode> SpecializeStructs(
        ProgramNode program,
        IEnumerable<FunctionNode> specializedFunctions)
    {
        var genericDefinitions = program.Structs
            .Where(structNode => !structNode.IsHeaderDeclaration)
            .Where(structNode => structNode.TypeParameters.Count > 0)
            .ToDictionary(structNode => structNode.Name, StringComparer.Ordinal);
        if (genericDefinitions.Count == 0)
        {
            return [];
        }

        var concreteStructs = program.Structs
            .Where(structNode => !structNode.IsHeaderDeclaration)
            .Where(structNode => structNode.TypeParameters.Count == 0)
            .ToDictionary(structNode => structNode.Name, StringComparer.Ordinal);
        var emitted = new HashSet<string>(concreteStructs.Keys, StringComparer.Ordinal);
        var pending = new Queue<GenericStructUse>();

        void CollectFromType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            foreach (var use in FindGenericStructUses(type))
            {
                if (genericDefinitions.ContainsKey(use.Name))
                {
                    pending.Enqueue(use);
                }
            }
        }

        foreach (var typeAlias in program.TypeAliases)
        {
            CollectFromType(typeAlias.TargetType);
        }

        foreach (var adapter in program.TypeAdapters)
        {
            if (!adapter.TypeParameters.Any(parameter => Regex.IsMatch(adapter.BaseType, $@"\b{Regex.Escape(parameter)}\b")))
            {
                CollectFromType(adapter.BaseType);
            }
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            CollectFromType(externFunction.ReturnType);
            foreach (var parameter in externFunction.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                CollectFromType(parameter.Type);
            }
        }

        foreach (var structNode in program.Structs.Where(structNode => structNode.TypeParameters.Count == 0))
        {
            foreach (var field in structNode.Fields)
            {
                CollectFromType(field.Type);
            }
        }

        foreach (var taggedUnion in program.TaggedUnions)
        {
            foreach (var variant in taggedUnion.Variants)
            {
                CollectFromType(variant.Type);
            }
        }

        foreach (var global in program.GlobalVariables)
        {
            CollectFromType(global.Type);
        }

        foreach (var function in program.Functions.Concat(specializedFunctions))
        {
            CollectFromFunction(function, CollectFromType);
        }

        var result = new List<StructNode>();
        while (pending.TryDequeue(out var use))
        {
            var concreteName = LowerGenericTypeName(use.Name, use.Arguments);
            if (!emitted.Add(concreteName)
                || !genericDefinitions.TryGetValue(use.Name, out var definition)
                || definition.TypeParameters.Count != use.Arguments.Count)
            {
                continue;
            }

            var substitutions = definition.TypeParameters
                .Zip(use.Arguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            var fields = definition.Fields
                .Select(field =>
                {
                    var fieldType = SubstituteGenericType(field.Type, substitutions);
                    CollectFromType(fieldType);
                    return new StructFieldNode(field.Location, field.Name, fieldType, field.Attributes);
                })
                .ToList();
            var requirements = definition.Requirements
                .Select(requirement => new StructRequirementNode(
                    requirement.Location,
                    requirement.Name,
                    requirement.TypeArguments
                        .Select(argument => SubstituteGenericType(argument, substitutions))
                        .ToList()))
                .ToList();
            var specialized = new StructNode(
                definition.Location,
                concreteName,
                [],
                [],
                requirements,
                fields,
                [],
                definition.Attributes);
            specialized.Semantic.ModuleName = definition.Semantic.ModuleName;
            result.Add(specialized);
        }

        return result;
    }

    private static void CollectFromFunction(FunctionNode function, Action<string?> collectFromType)
    {
        collectFromType(function.ReturnType);
        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            collectFromType(parameter.Type);
        }

        foreach (var statement in function.Body)
        {
            CollectFromStatement(statement, collectFromType);
        }
    }

    private static void CollectFromStatement(StatementNode statement, Action<string?> collectFromType)
    {
        switch (statement)
        {
            case LetStatement let:
                collectFromType(let.Type);
                break;
            case IfStatement ifStatement:
                foreach (var nested in ifStatement.ThenBody)
                {
                    CollectFromStatement(nested, collectFromType);
                }

                if (ifStatement.ElseBranch is not null)
                {
                    CollectFromStatement(ifStatement.ElseBranch, collectFromType);
                }

                break;
            case ElseBlockStatement elseBlock:
                foreach (var nested in elseBlock.Body)
                {
                    CollectFromStatement(nested, collectFromType);
                }

                break;
            case WhileStatement whileStatement:
                foreach (var nested in whileStatement.Body)
                {
                    CollectFromStatement(nested, collectFromType);
                }

                break;
            case ForStatement forStatement:
                if (forStatement.Initializer is ForDeclarationInitializerNode declaration)
                {
                    collectFromType(declaration.Type);
                }

                foreach (var nested in forStatement.Body)
                {
                    CollectFromStatement(nested, collectFromType);
                }

                break;
            case ForeachStatement foreachStatement:
                collectFromType(foreachStatement.ValueBinding.Type);
                if (foreachStatement.IndexBinding is not null)
                {
                    collectFromType(foreachStatement.IndexBinding.Type);
                }

                if (foreachStatement.KeyBinding is not null)
                {
                    collectFromType(foreachStatement.KeyBinding.Type);
                }

                foreach (var nested in foreachStatement.Body)
                {
                    CollectFromStatement(nested, collectFromType);
                }

                break;
            case SwitchStatement switchStatement:
                foreach (var switchCase in switchStatement.Cases)
                {
                    foreach (var nested in switchCase.Body)
                    {
                        CollectFromStatement(nested, collectFromType);
                    }
                }

                foreach (var nested in switchStatement.DefaultBody)
                {
                    CollectFromStatement(nested, collectFromType);
                }

                break;
            case MatchStatement matchStatement:
                foreach (var arm in matchStatement.Arms)
                {
                    foreach (var nested in arm.Body)
                    {
                        CollectFromStatement(nested, collectFromType);
                    }
                }

                break;
        }
    }

    private static void EnqueueResolvedUse(ExpressionNode expression, Queue<GenericFunctionUse> pending)
    {
        if (expression.Semantic.ResolvedCall is { Function.TypeParameters.Count: > 0 } resolved
            && resolved.TypeArguments.Count == resolved.Function.TypeParameters.Count)
        {
            pending.Enqueue(new GenericFunctionUse(resolved.Function, resolved.TypeArguments));
        }
    }

    private static void RetargetResolvedGenericCalls(
        ProgramNode program,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        RetargetResolvedGenericCalls(EnumerateExpressions(program), specializations);
    }

    private static void RetargetResolvedGenericCalls(
        IEnumerable<FunctionNode> functions,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        RetargetResolvedGenericCalls(
            functions.SelectMany(function => EnumerateExpressions(function.Body)),
            specializations);
    }

    private static void RetargetResolvedGenericCalls(
        IEnumerable<ExpressionNode> expressions,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        foreach (var expression in expressions)
        {
            RetargetResolvedGenericCall(expression, specializations);
        }
    }

    private static void RetargetResolvedGenericCall(
        ExpressionNode expression,
        IReadOnlyDictionary<string, FunctionNode> specializations)
    {
        if (expression.Semantic.ResolvedCall is not { Function.TypeParameters.Count: > 0 } resolved
            || resolved.TypeArguments.Count != resolved.Function.TypeParameters.Count
            || !specializations.TryGetValue(Key(resolved.Function, resolved.TypeArguments), out var specialized))
        {
            return;
        }

        EnsureFunctionSymbol(specialized);
        expression.Semantic.Symbol = specialized.Semantic.Symbol;
        expression.Semantic.ResolvedCall = new ResolvedCallInfo(
            specialized,
            resolved.TypeArguments,
            resolved.IsInstance);
    }

    private static void EnsureFunctionSymbol(FunctionNode function)
    {
        if (function.Semantic.Symbol is { Kind: SymbolKind.Function })
        {
            return;
        }

        function.Semantic.Symbol = new Symbol(
            function.Name,
            SymbolKind.Function,
            function.ReturnType,
            function.Location,
            function);
    }

    private static FunctionNode InstantiateFunction(FunctionNode function, IReadOnlyList<string> arguments)
    {
        var substitutions = function.TypeParameters
            .Zip(arguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var selfType = function.OwnerType is not null && arguments.Count > 0
            ? $"{function.OwnerType}<{string.Join(",", arguments)}>"
            : function.OwnerType;
        var specialized = function with
        {
            TypeParameters = [],
            TypeArguments = arguments,
            ReturnType = SubstituteSelfType(SubstituteGenericType(function.ReturnType, substitutions), selfType),
            Parameters = function.Parameters
                .Select(parameter => parameter with { Type = SubstituteSelfType(SubstituteGenericType(parameter.Type, substitutions), selfType) })
                .ToList(),
            Body = function.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
        };
        specialized.Semantic.ModuleName = function.Semantic.ModuleName;
        EnsureFunctionSymbol(specialized);
        return specialized;
    }

    private static StatementNode SubstituteStatement(
        StatementNode statement,
        IReadOnlyDictionary<string, string> substitutions) => statement switch
    {
        LetStatement let => let with
        {
            Type = SubstituteGenericType(let.Type, substitutions),
            Initializer = SubstituteOptionalExpression(let.Initializer, substitutions),
        },
        ReturnStatement ret => ret with { Expression = SubstituteExpression(ret.Expression, substitutions) },
        CStatement c => c with { Expression = SubstituteExpression(c.Expression, substitutions) },
        IfStatement ifStatement => ifStatement with
        {
            Condition = SubstituteExpression(ifStatement.Condition, substitutions),
            ThenBody = ifStatement.ThenBody.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
            ElseBranch = ifStatement.ElseBranch is null ? null : SubstituteStatement(ifStatement.ElseBranch, substitutions),
        },
        ElseBlockStatement elseBlock => elseBlock with
        {
            Body = elseBlock.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
        },
        WhileStatement whileStatement => whileStatement with
        {
            Condition = SubstituteExpression(whileStatement.Condition, substitutions),
            Body = whileStatement.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
        },
        ForStatement forStatement => forStatement with
        {
            Initializer = SubstituteForInitializer(forStatement.Initializer, substitutions),
            Condition = SubstituteExpression(forStatement.Condition, substitutions),
            Increment = SubstituteExpression(forStatement.Increment, substitutions),
            Body = forStatement.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
        },
        ForeachStatement foreachStatement => foreachStatement with
        {
            IterableExpression = SubstituteExpression(foreachStatement.IterableExpression, substitutions),
            Body = foreachStatement.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
        },
        SwitchStatement switchStatement => switchStatement with
        {
            Expression = SubstituteExpression(switchStatement.Expression, substitutions),
            Cases = switchStatement.Cases.Select(switchCase => switchCase with
            {
                Pattern = SubstituteExpression(switchCase.Pattern, substitutions),
                Body = switchCase.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
            }).ToList(),
            DefaultBody = switchStatement.DefaultBody.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
        },
        MatchStatement matchStatement => matchStatement with
        {
            Expression = SubstituteExpression(matchStatement.Expression, substitutions),
            Arms = matchStatement.Arms.Select(arm => arm with
            {
                Body = arm.Body.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
            }).ToList(),
        },
        _ => statement,
    };

    private static ExpressionNode? SubstituteOptionalExpression(
        ExpressionNode? expression,
        IReadOnlyDictionary<string, string> substitutions) =>
        expression is null ? null : SubstituteExpression(expression, substitutions);

    private static ForInitializerNode SubstituteForInitializer(
        ForInitializerNode initializer,
        IReadOnlyDictionary<string, string> substitutions) => initializer switch
    {
        ForDeclarationInitializerNode declaration => declaration with
        {
            Type = SubstituteGenericType(declaration.Type, substitutions),
            Initializer = SubstituteOptionalExpression(declaration.Initializer, substitutions),
        },
        ForExpressionInitializerNode expression => expression with
        {
            Expression = SubstituteExpression(expression.Expression, substitutions),
        },
        _ => initializer,
    };

    private static ExpressionNode SubstituteExpression(
        ExpressionNode expression,
        IReadOnlyDictionary<string, string> substitutions)
    {
        var sourceText = SubstituteGenericType(expression.SourceText, substitutions);
        return expression switch
        {
            ParenthesizedExpressionNode parenthesized => parenthesized with
            {
                SourceText = sourceText,
                Expression = SubstituteExpression(parenthesized.Expression, substitutions),
            },
            CastExpressionNode cast => cast with
            {
                SourceText = sourceText,
                TargetType = SubstituteGenericType(cast.TargetType, substitutions),
                Expression = SubstituteExpression(cast.Expression, substitutions),
            },
            UnaryExpressionNode unary => unary with
            {
                SourceText = sourceText,
                Operand = SubstituteExpression(unary.Operand, substitutions),
            },
            PostfixExpressionNode postfix => postfix with
            {
                SourceText = sourceText,
                Operand = SubstituteExpression(postfix.Operand, substitutions),
            },
            SizeOfExpressionNode sizeOf => sizeOf with
            {
                SourceText = sourceText,
                TypeOperand = sizeOf.TypeOperand is null ? null : SubstituteGenericType(sizeOf.TypeOperand, substitutions),
                ExpressionOperand = SubstituteOptionalExpression(sizeOf.ExpressionOperand, substitutions),
            },
            BinaryExpressionNode binary => binary with
            {
                SourceText = sourceText,
                Left = SubstituteExpression(binary.Left, substitutions),
                Right = SubstituteExpression(binary.Right, substitutions),
            },
            ScalarRangeExpressionNode range => range with
            {
                SourceText = sourceText,
                Start = SubstituteExpression(range.Start, substitutions),
                End = SubstituteExpression(range.End, substitutions),
            },
            ConditionalExpressionNode conditional => conditional with
            {
                SourceText = sourceText,
                Condition = SubstituteExpression(conditional.Condition, substitutions),
                WhenTrue = SubstituteExpression(conditional.WhenTrue, substitutions),
                WhenFalse = SubstituteExpression(conditional.WhenFalse, substitutions),
            },
            InitializerExpressionNode initializer => initializer with
            {
                SourceText = sourceText,
                TypeName = initializer.TypeName is null ? null : SubstituteGenericType(initializer.TypeName, substitutions),
                Fields = initializer.Fields.Select(field => field with { Value = SubstituteExpression(field.Value, substitutions) }).ToList(),
                Values = initializer.Values.Select(value => SubstituteExpression(value, substitutions)).ToList(),
            },
            FunctionExpressionNode functionExpression => functionExpression with
            {
                SourceText = sourceText,
                ExpressionBody = SubstituteOptionalExpression(functionExpression.ExpressionBody, substitutions),
                BlockBody = functionExpression.BlockBody?.Select(statement => SubstituteStatement(statement, substitutions)).ToList(),
            },
            AssignmentExpressionNode assignment => assignment with
            {
                SourceText = sourceText,
                Target = SubstituteExpression(assignment.Target, substitutions),
                Value = SubstituteExpression(assignment.Value, substitutions),
            },
            CallExpressionNode call => call with
            {
                SourceText = sourceText,
                Callee = SubstituteExpression(call.Callee, substitutions),
                Arguments = call.Arguments.Select(argument => SubstituteExpression(argument, substitutions)).ToList(),
            },
            GenericCallExpressionNode call => call with
            {
                SourceText = sourceText,
                Callee = SubstituteExpression(call.Callee, substitutions),
                TypeArguments = call.TypeArguments.Select(argument => SubstituteGenericType(argument, substitutions)).ToList(),
                Arguments = call.Arguments.Select(argument => SubstituteExpression(argument, substitutions)).ToList(),
            },
            MemberExpressionNode member => member with
            {
                SourceText = sourceText,
                Target = SubstituteExpression(member.Target, substitutions),
            },
            IndexExpressionNode index => index with
            {
                SourceText = sourceText,
                Target = SubstituteExpression(index.Target, substitutions),
                Index = SubstituteExpression(index.Index, substitutions),
            },
            _ => expression with { SourceText = sourceText },
        };
    }

    private static IEnumerable<ExpressionNode> EnumerateExpressions(ProgramNode program)
    {
        foreach (var global in program.GlobalVariables.Where(global => global.Initializer is not null))
        {
            foreach (var expression in EnumerateExpressions(global.Initializer!))
            {
                yield return expression;
            }
        }

        foreach (var function in program.Functions)
        {
            foreach (var expression in EnumerateExpressions(function.Body))
            {
                yield return expression;
            }
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateExpressions(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement { Initializer: not null } let:
                    foreach (var expression in EnumerateExpressions(let.Initializer)) yield return expression;
                    break;
                case ReturnStatement ret:
                    foreach (var expression in EnumerateExpressions(ret.Expression)) yield return expression;
                    break;
                case CStatement c:
                    foreach (var expression in EnumerateExpressions(c.Expression)) yield return expression;
                    break;
                case IfStatement ifStatement:
                    foreach (var expression in EnumerateExpressions(ifStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(ifStatement.ThenBody)) yield return expression;
                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var expression in EnumerateExpressions([ifStatement.ElseBranch])) yield return expression;
                    }
                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var expression in EnumerateExpressions(elseBlock.Body)) yield return expression;
                    break;
                case WhileStatement whileStatement:
                    foreach (var expression in EnumerateExpressions(whileStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(whileStatement.Body)) yield return expression;
                    break;
                case ForStatement forStatement:
                    foreach (var expression in EnumerateForInitializerExpressions(forStatement.Initializer)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Condition)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Increment)) yield return expression;
                    foreach (var expression in EnumerateExpressions(forStatement.Body)) yield return expression;
                    break;
                case ForeachStatement foreachStatement:
                    foreach (var expression in EnumerateExpressions(foreachStatement.IterableExpression)) yield return expression;
                    foreach (var expression in EnumerateExpressions(foreachStatement.Body)) yield return expression;
                    break;
                case SwitchStatement switchStatement:
                    foreach (var expression in EnumerateExpressions(switchStatement.Expression)) yield return expression;
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var expression in EnumerateExpressions(switchCase.Pattern)) yield return expression;
                        foreach (var expression in EnumerateExpressions(switchCase.Body)) yield return expression;
                    }
                    foreach (var expression in EnumerateExpressions(switchStatement.DefaultBody)) yield return expression;
                    break;
                case MatchStatement matchStatement:
                    foreach (var expression in EnumerateExpressions(matchStatement.Expression)) yield return expression;
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var expression in EnumerateExpressions(arm.Body)) yield return expression;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<ExpressionNode> EnumerateForInitializerExpressions(ForInitializerNode initializer) => initializer switch
    {
        ForDeclarationInitializerNode { Initializer: not null } declaration => EnumerateExpressions(declaration.Initializer),
        ForExpressionInitializerNode expression => EnumerateExpressions(expression.Expression),
        _ => [],
    };

    private static IEnumerable<ExpressionNode> EnumerateExpressions(ExpressionNode expression)
    {
        yield return expression;
        switch (expression)
        {
            case ParenthesizedExpressionNode parenthesized:
                foreach (var child in EnumerateExpressions(parenthesized.Expression)) yield return child;
                break;
            case CastExpressionNode cast:
                foreach (var child in EnumerateExpressions(cast.Expression)) yield return child;
                break;
            case UnaryExpressionNode unary:
                foreach (var child in EnumerateExpressions(unary.Operand)) yield return child;
                break;
            case PostfixExpressionNode postfix:
                foreach (var child in EnumerateExpressions(postfix.Operand)) yield return child;
                break;
            case SizeOfExpressionNode { ExpressionOperand: not null } sizeOf:
                foreach (var child in EnumerateExpressions(sizeOf.ExpressionOperand)) yield return child;
                break;
            case BinaryExpressionNode binary:
                foreach (var child in EnumerateExpressions(binary.Left)) yield return child;
                foreach (var child in EnumerateExpressions(binary.Right)) yield return child;
                break;
            case ScalarRangeExpressionNode range:
                foreach (var child in EnumerateExpressions(range.Start)) yield return child;
                foreach (var child in EnumerateExpressions(range.End)) yield return child;
                break;
            case ConditionalExpressionNode conditional:
                foreach (var child in EnumerateExpressions(conditional.Condition)) yield return child;
                foreach (var child in EnumerateExpressions(conditional.WhenTrue)) yield return child;
                foreach (var child in EnumerateExpressions(conditional.WhenFalse)) yield return child;
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    foreach (var child in EnumerateExpressions(field.Value)) yield return child;
                }
                foreach (var value in initializer.Values)
                {
                    foreach (var child in EnumerateExpressions(value)) yield return child;
                }
                break;
            case FunctionExpressionNode function:
                if (function.ExpressionBody is not null)
                {
                    foreach (var child in EnumerateExpressions(function.ExpressionBody)) yield return child;
                }
                if (function.BlockBody is not null)
                {
                    foreach (var child in EnumerateExpressions(function.BlockBody)) yield return child;
                }
                break;
            case AssignmentExpressionNode assignment:
                foreach (var child in EnumerateExpressions(assignment.Target)) yield return child;
                foreach (var child in EnumerateExpressions(assignment.Value)) yield return child;
                break;
            case CallExpressionNode call:
                foreach (var child in EnumerateExpressions(call.Callee)) yield return child;
                foreach (var argument in call.Arguments)
                {
                    foreach (var child in EnumerateExpressions(argument)) yield return child;
                }
                break;
            case GenericCallExpressionNode call:
                foreach (var child in EnumerateExpressions(call.Callee)) yield return child;
                foreach (var argument in call.Arguments)
                {
                    foreach (var child in EnumerateExpressions(argument)) yield return child;
                }
                break;
            case MemberExpressionNode member:
                foreach (var child in EnumerateExpressions(member.Target)) yield return child;
                break;
            case IndexExpressionNode index:
                foreach (var child in EnumerateExpressions(index.Target)) yield return child;
                foreach (var child in EnumerateExpressions(index.Index)) yield return child;
                break;
        }
    }

    private static string SubstituteSelfType(string type, string? selfType) =>
        selfType is null
            ? type
            : Regex.Replace(type, @"\bSelf\b", selfType);

    private static string SubstituteGenericType(string type, IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var (name, value) in substitutions.OrderByDescending(pair => pair.Key.Length))
        {
            type = Regex.Replace(type, $@"\b{Regex.Escape(name)}\b", value);
        }

        return type;
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

            var nameEnd = i;
            while (i < type.Length && char.IsWhiteSpace(type[i]))
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

            var name = type[nameStart..nameEnd];
            var arguments = SplitGenericArguments(type[(i + 1)..close]);
            uses.Add(new GenericStructUse(name, arguments, type[nameStart..(close + 1)]));

            foreach (var argument in arguments)
            {
                uses.AddRange(FindGenericStructUses(argument));
            }

            i = close;
        }

        return uses;
    }

    private static string LowerGenericTypeName(string name, IReadOnlyList<string> arguments) =>
        SanitizeTypeName($"{name}_{string.Join("_", arguments.Select(LowerTypeName))}");

    private static string LowerTypeName(string type)
    {
        type = type.Trim();
        foreach (var use in FindGenericStructUses(type).OrderByDescending(use => use.SourceText.Length))
        {
            type = type.Replace(use.SourceText, LowerGenericTypeName(use.Name, use.Arguments), StringComparison.Ordinal);
        }

        return SanitizeTypeName(type
            .Replace("const ", "const_", StringComparison.Ordinal)
            .Replace("*", "_ptr", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal));
    }

    private static string RewriteConcreteGenericStructTypes(
        string type,
        IReadOnlySet<string> concreteStructNames)
    {
        foreach (var use in FindGenericStructUses(type).OrderByDescending(use => use.SourceText.Length))
        {
            var concreteName = LowerGenericTypeName(use.Name, use.Arguments);
            if (!concreteStructNames.Contains(concreteName))
            {
                continue;
            }

            type = type.Replace(use.SourceText, concreteName, StringComparison.Ordinal);
        }

        return type;
    }

    private static string SanitizeTypeName(string type) =>
        Regex.Replace(type, "[^A-Za-z0-9_]", "_");

    private static int FindMatchingGenericClose(string type, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < type.Length; i++)
        {
            if (type[i] == '<')
            {
                depth++;
            }
            else if (type[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
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

    private static bool IsIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch == '_';

    private static bool IsIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    private static string Key(FunctionNode function, IReadOnlyList<string> arguments) =>
        $"{(function.OwnerType is null ? function.Name : $"{function.OwnerType}.{function.Name}")}<{string.Join(",", arguments)}>";

    private static T CopySemantic<T>(SyntaxNode source, T target)
        where T : SyntaxNode
    {
        target.Semantic.Type = source.Semantic.Type;
        target.Semantic.Symbol = source.Semantic.Symbol;
        target.Semantic.Origin = source.Semantic.Origin;
        target.Semantic.ModuleName = source.Semantic.ModuleName;
        target.Semantic.ResolvedCall = source.Semantic.ResolvedCall;
        return target;
    }

    private sealed record GenericStructUse(string Name, IReadOnlyList<string> Arguments, string SourceText);

    private sealed record GenericFunctionUse(FunctionNode Function, IReadOnlyList<string> TypeArguments);
}
