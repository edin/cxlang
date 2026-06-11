using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;
using CxParser = Cx.Compiler.Parser.Parser;

namespace Cx.Compiler.Lowering;

internal static class LambdaLowerer
{
    public static ProgramNode Lower(ProgramNode program, DiagnosticBag diagnostics)
    {
        var state = new State(new TypeRefParser(program));
        var globals = program.GlobalVariables
            .Select(global => global with
            {
                Initializer = global.Initializer is null
                    ? null
                    : LowerExpressionNode(global.Initializer, TryParseFunctionReturnType(TypeText(global.TypeNode, state)), "global", state),
            })
            .ToList();

        var functions = program.Functions
            .Select(function => LowerFunction(function, state))
            .Concat(state.GeneratedFunctions)
            .ToList();

        if (state.GeneratedSources.Count > 0)
        {
            var source = string.Join(Environment.NewLine, state.GeneratedSources);
            var generatedProgram = new CxParser(diagnostics)
                .Parse(new SourceFile("<lambda>", source));

            functions.AddRange(generatedProgram.Functions.Select(function => LowerFunction(function, state)));
        }

        return program with
        {
            GlobalVariables = globals,
            Functions = functions,
        };
    }

    private static FunctionNode LowerFunction(FunctionNode function, State state)
    {
        var parentName = GetParentName(function);
        return function with
        {
            Body = function.Body
                .Select(statement => LowerStatement(statement, TypeText(function.ReturnTypeNode, state), parentName, state))
                .ToList(),
        };
    }

    private static StatementNode LowerStatement(
        StatementNode statement,
        string functionReturnType,
        string parentName,
        State state) => statement switch
    {
        LetStatement let => let with
        {
            Initializer = let.Initializer is null
                ? null
                : LowerExpressionNode(let.Initializer, TryParseFunctionReturnType(TypeText(let.TypeNode, state)), parentName, state),
        },
        ReturnStatement ret => ret with
        {
            Expression = ret.Expression is null
                ? null
                : LowerExpressionNode(ret.Expression, TryParseFunctionReturnType(functionReturnType), parentName, state),
        },
        CStatement c => c with
        {
            Expression = LowerExpressionNode(c.Expression, null, parentName, state),
        },
        IfStatement ifStatement => ifStatement with
        {
            Condition = LowerExpressionNode(ifStatement.Condition, null, parentName, state),
            ThenBody = ifStatement.ThenBody
                .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                .ToList(),
            ElseBranch = ifStatement.ElseBranch is null
                ? null
                : LowerStatement(ifStatement.ElseBranch, functionReturnType, parentName, state),
        },
        ElseBlockStatement elseBlock => elseBlock with
        {
            Body = elseBlock.Body
                .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                .ToList(),
        },
        WhileStatement whileStatement => whileStatement with
        {
            Condition = LowerExpressionNode(whileStatement.Condition, null, parentName, state),
            Body = whileStatement.Body
                .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                .ToList(),
        },
        ForStatement forStatement => forStatement with
        {
            Initializer = LowerForInitializer(forStatement.Initializer, parentName, state),
            Condition = LowerExpressionNode(forStatement.Condition, null, parentName, state),
            Increment = LowerExpressionNode(forStatement.Increment, null, parentName, state),
            Body = forStatement.Body
                .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                .ToList(),
        },
        ForeachStatement foreachStatement => foreachStatement with
        {
            IterableExpression = LowerExpressionNode(foreachStatement.IterableExpression, null, parentName, state),
            Body = foreachStatement.Body
                .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                .ToList(),
        },
        SwitchStatement switchStatement => switchStatement with
        {
            Expression = LowerExpressionNode(switchStatement.Expression, null, parentName, state),
            Cases = switchStatement.Cases.Select(switchCase => switchCase with
            {
                Pattern = LowerExpressionNode(switchCase.Pattern, null, parentName, state),
                Body = switchCase.Body
                    .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                    .ToList(),
            }).ToList(),
            DefaultBody = switchStatement.DefaultBody
                .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                .ToList(),
        },
        MatchStatement matchStatement => matchStatement with
        {
            Expression = LowerExpressionNode(matchStatement.Expression, null, parentName, state),
            Arms = matchStatement.Arms.Select(arm => arm with
            {
                Body = arm.Body
                    .Select(child => LowerStatement(child, functionReturnType, parentName, state))
                    .ToList(),
            }).ToList(),
        },
        _ => statement,
    };

    private static ForInitializerNode LowerForInitializer(
        ForInitializerNode initializer,
        string parentName,
        State state) => initializer switch
    {
        ForDeclarationInitializerNode declaration => declaration with
        {
            Initializer = declaration.Initializer is null
                ? null
                : LowerExpressionNode(declaration.Initializer, null, parentName, state),
        },
        ForExpressionInitializerNode expression => expression with
        {
            Expression = LowerExpressionNode(expression.Expression, null, parentName, state),
        },
        _ => initializer,
    };

    private static ExpressionNode LowerExpressionNode(
        ExpressionNode expression,
        string? contextualReturnType,
        string parentName,
        State state) => expression switch
        {
            FunctionExpressionNode functionExpression => new NameExpressionNode(
                functionExpression.Location,
                LowerFunctionExpression(functionExpression, contextualReturnType, parentName, state)),
            ParenthesizedExpressionNode parenthesized => parenthesized with
            {
                Expression = LowerExpressionNode(parenthesized.Expression, contextualReturnType, parentName, state),
            },
            CastExpressionNode cast => cast with
            {
                Expression = LowerExpressionNode(cast.Expression, null, parentName, state),
            },
            UnaryExpressionNode unary => unary with
            {
                Operand = LowerExpressionNode(unary.Operand, null, parentName, state),
            },
            PostfixExpressionNode postfix => postfix with
            {
                Operand = LowerExpressionNode(postfix.Operand, null, parentName, state),
            },
            SizeOfExpressionNode sizeOf => sizeOf with
            {
                ExpressionOperand = sizeOf.ExpressionOperand is null
                    ? null
                    : LowerExpressionNode(sizeOf.ExpressionOperand, null, parentName, state),
            },
            BinaryExpressionNode binary => binary with
            {
                Left = LowerExpressionNode(binary.Left, null, parentName, state),
                Right = LowerExpressionNode(binary.Right, null, parentName, state),
            },
            ConditionalExpressionNode conditional => conditional with
            {
                Condition = LowerExpressionNode(conditional.Condition, null, parentName, state),
                WhenTrue = LowerExpressionNode(conditional.WhenTrue, contextualReturnType, parentName, state),
                WhenFalse = LowerExpressionNode(conditional.WhenFalse, contextualReturnType, parentName, state),
            },
            InitializerExpressionNode initializer => initializer with
            {
                Fields = initializer.Fields
                    .Select(field => field with { Value = LowerExpressionNode(field.Value, null, parentName, state) })
                    .ToList(),
                Values = initializer.Values
                    .Select(value => LowerExpressionNode(value, null, parentName, state))
                    .ToList(),
            },
            AssignmentExpressionNode assignment => assignment with
            {
                Target = LowerExpressionNode(assignment.Target, null, parentName, state),
                Value = LowerExpressionNode(assignment.Value, null, parentName, state),
            },
            CallExpressionNode call => call with
            {
                Callee = LowerExpressionNode(call.Callee, null, parentName, state),
                Arguments = call.Arguments
                    .Select(argument => LowerExpressionNode(argument, null, parentName, state))
                    .ToList(),
            },
            GenericCallExpressionNode call => call with
            {
                Callee = LowerExpressionNode(call.Callee, null, parentName, state),
                Arguments = call.Arguments
                    .Select(argument => LowerExpressionNode(argument, null, parentName, state))
                    .ToList(),
            },
            MemberExpressionNode member => member with
            {
                Target = LowerExpressionNode(member.Target, null, parentName, state),
            },
            IndexExpressionNode index => index with
            {
                Target = LowerExpressionNode(index.Target, null, parentName, state),
                Index = LowerExpressionNode(index.Index, null, parentName, state),
            },
            RawExpressionNode raw => raw with
            {
                SourceText = LowerExpression(raw.SourceText, contextualReturnType, parentName, state),
            },
            _ => expression,
        };

    private static string LowerFunctionExpression(
        FunctionExpressionNode functionExpression,
        string? contextualReturnType,
        string parentName,
        State state)
    {
        var explicitReturnType = TypeText(functionExpression.ReturnTypeNode, state);
        var returnType = string.IsNullOrWhiteSpace(explicitReturnType)
            ? contextualReturnType ?? "int"
            : explicitReturnType;
        var functionName = $"{parentName}_fn_{state.NextId++}";

        if (functionExpression.BlockBody is not null)
        {
            state.GeneratedFunctions.Add(new FunctionNode(
                functionExpression.Location,
                IsStatic: false,
                OwnerType: null,
                Name: functionName,
                TypeParameters: [],
                TypeArguments: [],
                GenericConstraints: [],
                Parameters: functionExpression.Parameters,
                Body: functionExpression.BlockBody
                    .Select(statement => LowerStatement(statement, returnType, parentName, state))
                    .ToList(),
                Attributes: [],
                ReturnTypeNode: CreateTypeNode(returnType)));
            return functionName;
        }

        var expressionBody = functionExpression.ExpressionBody is null
            ? new LiteralExpressionNode(functionExpression.Location, "0")
            : LowerExpressionNode(functionExpression.ExpressionBody, returnType, parentName, state);

        state.GeneratedFunctions.Add(new FunctionNode(
            functionExpression.Location,
            IsStatic: false,
            OwnerType: null,
            Name: functionName,
            TypeParameters: [],
            TypeArguments: [],
            GenericConstraints: [],
            Parameters: functionExpression.Parameters,
            Body:
            [
                new ReturnStatement(functionExpression.Location, expressionBody)
            ],
            Attributes: [],
            ReturnTypeNode: CreateTypeNode(returnType)));

        return functionName;
    }

    private static string LowerExpression(string expression, string? contextualReturnType, string parentName, State state)
    {
        var index = 0;
        while (index < expression.Length)
        {
            var lambdaStart = FindLambdaStart(expression, index);
            if (lambdaStart < 0)
            {
                break;
            }

            if (!TryParseLambda(expression, lambdaStart, contextualReturnType, parentName, state, out var parsed))
            {
                index = lambdaStart + 2;
                continue;
            }

            expression = expression[..lambdaStart] + parsed.FunctionName + expression[parsed.End..];
            index = lambdaStart + parsed.FunctionName.Length;
        }

        return expression;
    }

    private static bool TryParseLambda(
        string expression,
        int lambdaStart,
        string? contextualReturnType,
        string parentName,
        State state,
        out ParsedLambda parsed)
    {
        parsed = default;
        var scan = lambdaStart + 2;
        SkipWhitespace(expression, ref scan);
        if (scan >= expression.Length || expression[scan] != '(')
        {
            return false;
        }

        var closeParameters = FindMatching(expression, scan, '(', ')');
        if (closeParameters < 0)
        {
            return false;
        }

        var parameterText = expression[(scan + 1)..closeParameters];
        var parameters = ParseParameters(parameterText);
        scan = closeParameters + 1;
        SkipWhitespace(expression, ref scan);

        var returnType = contextualReturnType;
        if (scan + 1 < expression.Length && expression[scan] == '-' && expression[scan + 1] == '>')
        {
            scan += 2;
            var returnTypeStart = scan;
            var fatArrow = FindFatArrow(expression, scan);
            var blockOpen = FindTopLevelOpeningBrace(expression, scan);
            if (fatArrow < 0 && blockOpen < 0)
            {
                return false;
            }

            if (blockOpen >= 0 && (fatArrow < 0 || blockOpen < fatArrow))
            {
                returnType = expression[returnTypeStart..blockOpen].Trim();
                scan = blockOpen;
            }
            else
            {
                returnType = expression[returnTypeStart..fatArrow].Trim();
                scan = fatArrow;
            }
        }

        if (scan < expression.Length && expression[scan] == '{')
        {
            var closeBrace = FindMatching(expression, scan, '{', '}');
            if (closeBrace < 0)
            {
                return false;
            }

            returnType = string.IsNullOrWhiteSpace(returnType) ? "int" : returnType;
            var blockFunctionName = $"{parentName}_fn_{state.NextId++}";
            var blockBody = expression[(scan + 1)..closeBrace];
            state.GeneratedSources.Add(BuildFunctionSource(blockFunctionName, returnType, parameters, blockBody));
            parsed = new ParsedLambda(blockFunctionName, closeBrace + 1);
            return true;
        }

        if (scan + 1 >= expression.Length || expression[scan] != '=' || expression[scan + 1] != '>')
        {
            return false;
        }

        scan += 2;
        var bodyStart = scan;
        var bodyEnd = FindLambdaBodyEnd(expression, bodyStart);
        var body = expression[bodyStart..bodyEnd].Trim();
        if (parameters.Count == 0 || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        returnType = string.IsNullOrWhiteSpace(returnType) ? "int" : returnType;
        var functionName = $"{parentName}_fn_{state.NextId++}";
        var location = Location.Synthetic("<lambda>");
        state.GeneratedFunctions.Add(new FunctionNode(
            location,
            IsStatic: false,
            OwnerType: null,
            Name: functionName,
            TypeParameters: [],
            TypeArguments: [],
            GenericConstraints: [],
            Parameters: parameters,
            Body:
            [
                new ReturnStatement(
                    location,
                    new RawExpressionNode(location, body))
            ],
            Attributes: [],
            ReturnTypeNode: CreateTypeNode(returnType)));

        parsed = new ParsedLambda(functionName, bodyEnd);
        return true;
    }

    private static string BuildFunctionSource(
        string functionName,
        string returnType,
        IReadOnlyList<ParameterNode> parameters,
        string body)
    {
        var parser = new TypeRefParser(new ProgramNode(
            Location.Synthetic("<lambda>"),
            []));
        var parameterText = string.Join(", ", parameters.Select(parameter => $"{parameter.Name}: {TypeText(parameter.TypeNode, parser)}"));
        return $"fn {functionName}({parameterText}) -> {returnType} {{{body}}}";
    }

    private static List<ParameterNode> ParseParameters(string parameterText)
    {
        var parameters = new List<ParameterNode>();
        foreach (var parameter in SplitTopLevel(parameterText, ','))
        {
            var colon = FindTopLevel(parameter, ':');
            if (colon <= 0)
            {
                continue;
            }

            var name = parameter[..colon].Trim();
            var type = NormalizeLambdaType(parameter[(colon + 1)..].Trim());
            if (name.Length == 0 || type.Length == 0)
            {
                continue;
            }

            parameters.Add(new ParameterNode(
                Location.Synthetic("<lambda>"),
                name,
                [],
                TypeNode: CreateTypeNode(type)));
        }

        return parameters;
    }

    private static TypeNode CreateTypeNode(string type)
    {
        return TypeNode.CreateFromText(Location.Synthetic("<lambda>"), type);
    }

    private static string TypeText(TypeNode? typeNode, State state) =>
        TypeText(typeNode, state.TypeRefParser);

    private static string TypeText(TypeNode? typeNode, TypeRefParser typeRefParser)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        var type = typeNode.ToTypeRef(typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private static int FindLambdaStart(string expression, int start)
    {
        for (var i = start; i <= expression.Length - 2; i++)
        {
            if (expression[i] != 'f' || expression[i + 1] != 'n')
            {
                continue;
            }

            var beforeOk = i == 0 || !IsIdentifierPart(expression[i - 1]);
            var after = i + 2;
            while (after < expression.Length && char.IsWhiteSpace(expression[after]))
            {
                after++;
            }

            if (beforeOk && after < expression.Length && expression[after] == '(')
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeLambdaType(string type) =>
        type
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("\t", "", StringComparison.Ordinal);

    private static int FindFatArrow(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            depth += text[i] switch
            {
                '(' or '[' or '{' or '<' => 1,
                ')' or ']' or '}' or '>' => -1,
                _ => 0,
            };

            if (depth == 0 && text[i] == '=' && text[i + 1] == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindTopLevelOpeningBrace(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '(' or '[' or '<' => 1,
                ')' or ']' or '>' => -1,
                _ => 0,
            };

            if (depth == 0 && text[i] == '{')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLambdaBodyEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '(' or '[' or '{' => 1,
                ')' or ']' or '}' => -1,
                _ => 0,
            };

            if (depth < 0)
            {
                return i;
            }

            if (depth == 0 && text[i] == ',')
            {
                return i;
            }
        }

        return text.Length;
    }

    private static string? TryParseFunctionReturnType(string type)
    {
        return TypeSyntaxParser.Parse(type) is FunctionTypeSyntaxNode function
            ? TypeSyntaxFormatter.ToCxString(function.ReturnType)
            : null;
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
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

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var values = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '(' or '[' or '{' or '<' => 1,
                ')' or ']' or '}' or '>' => -1,
                _ => 0,
            };

            if (depth == 0 && text[i] == separator)
            {
                values.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        values.Add(text[start..].Trim());
        return values;
    }

    private static int FindTopLevel(string text, char value)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '(' or '[' or '{' or '<' => 1,
                ')' or ']' or '}' or '>' => -1,
                _ => 0,
            };

            if (depth == 0 && text[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static string GetParentName(FunctionNode function)
    {
        var parser = new TypeRefParser(new ProgramNode(
            Location.Synthetic("<lambda>"),
            []));
        var ownerType = TypeText(function.OwnerTypeNode, parser);
        var name = string.IsNullOrWhiteSpace(ownerType)
            ? function.Name
            : $"{ownerType}_{function.Name}";
        return new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    }

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private sealed class State(TypeRefParser typeRefParser)
    {
        public TypeRefParser TypeRefParser { get; } = typeRefParser;

        public int NextId { get; set; }

        public List<FunctionNode> GeneratedFunctions { get; } = [];

        public List<string> GeneratedSources { get; } = [];
    }

    private readonly record struct ParsedLambda(string FunctionName, int End);
}
