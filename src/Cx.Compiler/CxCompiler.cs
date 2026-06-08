namespace Cx.Compiler;

using Cx.Compiler.Diagnostics;
using Cx.Compiler.C;
using Cx.Compiler.Derive;
using Cx.Compiler.Lowering;
using Cx.Compiler.Semantic;
using Cx.Compiler.Std;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;
using CxParser = Cx.Compiler.Parser.Parser;

public sealed class CxCompiler
{
    public CompilationResult CompileToC(string source, string path = "<memory>")
    {
        var sourceFile = new SourceFile(path, source);
        return CompileToC([sourceFile]);
    }

    public CompilationResult CompileToC(IEnumerable<SourceFile> sources)
    {
        return CompileToC(sources, nameManglerOptions: null);
    }

    internal CompilationResult CompileToC(
        IEnumerable<SourceFile> sources,
        CNameManglerOptions? nameManglerOptions)
    {
        var (program, diagnostics) = CompileProgram(sources, BuildTests: false);
        if (program is null)
        {
            return CompilationResult.Failed(diagnostics.Diagnostics);
        }

        var cEmitter = new CEmitter(nameManglerOptions);
        var cUnit = cEmitter.LowerToC(program);
        var c = cEmitter.Emit(cUnit);
        return CompilationResult.Succeeded(c, diagnostics.Diagnostics, GetLinkerArguments(program));
    }

    public CompilationResult CompileTestsToC(IEnumerable<SourceFile> sources, string? moduleName = null)
    {
        var (program, diagnostics) = CompileProgram(sources, BuildTests: true, TestModuleName: moduleName);
        if (program is null)
        {
            return CompilationResult.Failed(diagnostics.Diagnostics);
        }

        var cEmitter = new CEmitter();
        var cUnit = cEmitter.LowerToC(program);
        var c = cEmitter.Emit(cUnit);
        return CompilationResult.Succeeded(c, diagnostics.Diagnostics, GetLinkerArguments(program));
    }

    public CompilationResult AuditRawC(IEnumerable<SourceFile> sources)
    {
        var (program, diagnostics) = CompileProgram(sources, BuildTests: false);
        if (program is null)
        {
            return CompilationResult.Failed(diagnostics.Diagnostics);
        }

        var unit = new CEmitter().LowerToC(program);
        var report = new CRawAuditCollector().Collect(unit);
        return CompilationResult.Succeeded(FormatRawAuditReport(report), diagnostics.Diagnostics);
    }

    public CompilationResult AuditRawGenericUses(IEnumerable<SourceFile> sources)
    {
        var (program, diagnostics) = CompileProgram(sources, BuildTests: false);
        if (program is null)
        {
            return CompilationResult.Failed(diagnostics.Diagnostics);
        }

        var collector = new GenericUseCollector(program);
        _ = collector.Collect(program).ToList();
        return CompilationResult.Succeeded(FormatRawGenericUseAuditReport(collector.RawGenericUseAuditEntries), diagnostics.Diagnostics);
    }

    private static (ProgramNode? Program, DiagnosticBag Diagnostics) CompileProgram(
        IEnumerable<SourceFile> sources,
        bool BuildTests,
        string? TestModuleName = null)
    {
        var sourceFiles = sources.ToList();
        var diagnostics = new DiagnosticBag();
        var parser = new CxParser(diagnostics);
        var corePrograms = StandardLibrary.LoadCoreFiles()
            .Select(parser.Parse)
            .ToList();
        var userPrograms = sourceFiles
            .Select(parser.Parse)
            .ToList();

        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        if (BuildTests)
        {
            var userProgramPaths = userPrograms
                .Select(program => program.Location.File.Path)
                .ToHashSet(StringComparer.Ordinal);
            var allPrograms = corePrograms.Concat(userPrograms).ToList();
            userPrograms = BuildTestPrograms(
                allPrograms,
                program => TestModuleName is null
                    ? userProgramPaths.Contains(program.Location.File.Path)
                    : string.Equals(GetModuleName(program), TestModuleName, StringComparison.Ordinal),
                diagnostics,
                TestModuleName).ToList();
            corePrograms = [];
            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }
        }

        var inputPrograms = corePrograms.Concat(userPrograms).ToList();
        var rootProgram = GetRootProgram(userPrograms);
        new ModuleVisibilityAnalyzer(diagnostics, inputPrograms).Analyze(userPrograms);
        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        var preSemanticLowering = new CxPreSemanticLoweringPipeline(diagnostics);
        var postSemanticLowering = new CxPostSemanticLoweringPipeline(diagnostics);
        var moduleNamesByPath = inputPrograms
            .GroupBy(program => program.Location.File.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => GetModuleName(group.Last()), StringComparer.Ordinal);
        var mergedProgram = preSemanticLowering.Lower(MergePrograms(inputPrograms, rootProgram));
        AnnotateModuleNames(mergedProgram, moduleNamesByPath);
        var semanticModel = new SemanticModel();
        new ScopeResolver(diagnostics, semanticModel).Resolve(mergedProgram);
        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        new TypeResolutionPass(diagnostics).Resolve(mergedProgram);
        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        mergedProgram = new TypeInferencePass(diagnostics).Apply(mergedProgram);
        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        new SemanticAnalyzer(diagnostics, inputPrograms).Analyze(mergedProgram);

        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        mergedProgram = postSemanticLowering.Lower(mergedProgram);
        if (diagnostics.HasErrors)
        {
            return (null, diagnostics);
        }

        var generatedSource = DeriveGenerator.Generate(mergedProgram);
        if (!string.IsNullOrWhiteSpace(generatedSource))
        {
            var generatedProgram = parser.Parse(new SourceFile("<derive>", generatedSource));
            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }

            mergedProgram = preSemanticLowering.Lower(MergePrograms(inputPrograms.Append(generatedProgram), rootProgram));
            semanticModel = new SemanticModel();
            new ScopeResolver(diagnostics, semanticModel).Resolve(mergedProgram);
            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }

            new TypeResolutionPass(diagnostics).Resolve(mergedProgram);
            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }

            mergedProgram = new TypeInferencePass(diagnostics).Apply(mergedProgram);
            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }

            new SemanticAnalyzer(diagnostics, inputPrograms.Append(generatedProgram).ToList()).Analyze(mergedProgram);

            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }

            mergedProgram = postSemanticLowering.Lower(mergedProgram);
            if (diagnostics.HasErrors)
            {
                return (null, diagnostics);
            }
        }

        return (mergedProgram, diagnostics);
    }

    private static IReadOnlyList<ProgramNode> BuildTestPrograms(
        IReadOnlyList<ProgramNode> programs,
        Func<ProgramNode, bool> collectTestsFromProgram,
        DiagnosticBag diagnostics,
        string? moduleName)
    {
        var selectedPrograms = programs
            .Where(collectTestsFromProgram)
            .ToList();
        var testCases = selectedPrograms
            .SelectMany(program => program.Tests.Select(test => (Program: program, Test: test)))
            .ToList();
        if (testCases.Count == 0)
        {
            var location = selectedPrograms.FirstOrDefault()?.Location
                ?? programs.FirstOrDefault()?.Location
                ?? new Location(new SourceFile("<tests>", string.Empty), 0, 1, 1);
            diagnostics.Report(location, moduleName is null
                ? "No tests found."
                : $"No tests found in module '{moduleName}'.");
            return programs;
        }

        var generatedNames = new Dictionary<TestNode, string>();
        var selectedTestSet = testCases
            .Select(testCase => testCase.Test)
            .ToHashSet();
        var rewrittenPrograms = programs
            .Select(program =>
            {
                var testFunctions = program.Tests
                    .Where(selectedTestSet.Contains)
                    .Select((test, index) =>
                    {
                        var functionName = BuildTestFunctionName(program, test, index);
                        generatedNames[test] = functionName;
                        return new FunctionNode(
                            test.Location,
                            IsStatic: false,
                            OwnerType: null,
                            Name: functionName,
                            TypeParameters: [],
                            TypeArguments: [],
                            GenericConstraints: [],
                            Parameters:
                            [
                                new ParameterNode(test.Location, "runner", [], TypeNode: RewriteTypeNode(null, "TestRunner*"))
                            ],
                            Body: RewriteTestStatements(test.Body),
                            Attributes: [],
                            ReturnTypeNode: RewriteTypeNode(null, "void"));
                    })
                    .ToList();

                return program with
                {
                    Functions = program.Functions
                        .Where(function => function.OwnerType is not null || function.Name != "main")
                        .Concat(testFunctions)
                        .ToList(),
                };
            })
            .ToList();

        var rootLocation = new Location(new SourceFile("<tests>", string.Empty), 0, 1, 1);
        var rootDeclarations = new List<TopLevelNode>();
        foreach (var importedModuleName in testCases
            .Select(testCase => testCase.Program.Module?.Name)
            .Where(importedModuleName => !string.IsNullOrWhiteSpace(importedModuleName))
            .Distinct(StringComparer.Ordinal))
        {
            rootDeclarations.Add(new ImportNode(rootLocation, importedModuleName!, Alias: null));
        }

        rootDeclarations.Add(new FunctionNode(
            rootLocation,
            IsStatic: false,
            OwnerType: null,
            Name: "main",
            TypeParameters: [],
            TypeArguments: [],
            GenericConstraints: [],
            Parameters: [],
            Body: BuildTestMainBody(testCases, generatedNames),
            Attributes: [],
            ReturnTypeNode: RewriteTypeNode(null, "int")));

        rewrittenPrograms.Add(new ProgramNode(rootLocation, rootDeclarations));
        return rewrittenPrograms;
    }

    private static string BuildTestFunctionName(ProgramNode program, TestNode test, int index)
    {
        var moduleName = program.Module?.Name ?? "root";
        var text = "__cx_test_" + moduleName + "_" + test.Name + "_" + index;
        return new string(text.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    }

    private static IReadOnlyList<StatementNode> BuildTestMainBody(
        IReadOnlyList<(ProgramNode Program, TestNode Test)> testCases,
        IReadOnlyDictionary<TestNode, string> generatedNames)
    {
        var location = new Location(new SourceFile("<tests>", string.Empty), 0, 1, 1);
        var body = new List<StatementNode>
        {
            new LetStatement(
                location,
                IsConst: false,
                Name: "runner",
                Initializer: new RawExpressionNode(location, "TestRunner.create()"),
                TypeNode: RewriteTypeNode(null, "TestRunner")),
        };

        foreach (var (_, test) in testCases)
        {
            body.Add(new CStatement(
                test.Location,
                new RawExpressionNode(test.Location, $"runner.begin(\"{EscapeStringLiteral(test.Name)}\")")));
            body.Add(new CStatement(
                test.Location,
                new RawExpressionNode(test.Location, $"{generatedNames[test]}(&runner)")));
            body.Add(new CStatement(
                test.Location,
                new RawExpressionNode(test.Location, "runner.end()")));
        }

        body.Add(new ReturnStatement(
            location,
            new RawExpressionNode(location, "runner.result()")));
        return body;
    }

    private static IReadOnlyList<StatementNode> RewriteTestStatements(IReadOnlyList<StatementNode> statements) =>
        statements.Select(RewriteTestStatement).ToList();

    private static StatementNode RewriteTestStatement(StatementNode statement) => statement switch
    {
        LetStatement let => let with
        {
            Initializer = let.Initializer is null ? null : RewriteTestExpression(let.Initializer),
        },
        ReturnStatement ret => ret with { Expression = RewriteTestExpression(ret.Expression) },
        CStatement c => c with { Expression = RewriteTestExpression(c.Expression) },
        IfStatement ifStatement => ifStatement with
        {
            Condition = RewriteTestExpression(ifStatement.Condition),
            ThenBody = RewriteTestStatements(ifStatement.ThenBody),
            ElseBranch = ifStatement.ElseBranch is null ? null : RewriteTestStatement(ifStatement.ElseBranch),
        },
        ElseBlockStatement elseBlock => elseBlock with { Body = RewriteTestStatements(elseBlock.Body) },
        WhileStatement whileStatement => whileStatement with
        {
            Condition = RewriteTestExpression(whileStatement.Condition),
            Body = RewriteTestStatements(whileStatement.Body),
        },
        ForStatement forStatement => forStatement with
        {
            Initializer = RewriteTestForInitializer(forStatement.Initializer),
            Condition = RewriteTestExpression(forStatement.Condition),
            Increment = RewriteTestExpression(forStatement.Increment),
            Body = RewriteTestStatements(forStatement.Body),
        },
        ForeachStatement foreachStatement => foreachStatement with
        {
            IterableExpression = RewriteTestExpression(foreachStatement.IterableExpression),
            Body = RewriteTestStatements(foreachStatement.Body),
        },
        SwitchStatement switchStatement => switchStatement with
        {
            Expression = RewriteTestExpression(switchStatement.Expression),
            Cases = switchStatement.Cases.Select(switchCase => switchCase with
            {
                Pattern = RewriteTestExpression(switchCase.Pattern),
                Body = RewriteTestStatements(switchCase.Body),
            }).ToList(),
            DefaultBody = RewriteTestStatements(switchStatement.DefaultBody),
        },
        MatchStatement matchStatement => matchStatement with
        {
            Expression = RewriteTestExpression(matchStatement.Expression),
            Arms = matchStatement.Arms.Select(arm => arm with { Body = RewriteTestStatements(arm.Body) }).ToList(),
        },
        _ => statement,
    };

    private static ForInitializerNode RewriteTestForInitializer(ForInitializerNode initializer) => initializer switch
    {
        ForDeclarationInitializerNode declaration => declaration with
        {
            Initializer = declaration.Initializer is null ? null : RewriteTestExpression(declaration.Initializer),
        },
        ForExpressionInitializerNode expression => expression with
        {
            Expression = RewriteTestExpression(expression.Expression),
        },
        _ => initializer,
    };

    private static ExpressionNode RewriteTestExpression(ExpressionNode expression) => expression switch
    {
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect" },
            Arguments.Count: 1,
        } call => call with
        {
            SourceText = string.Empty,
            Callee = new MemberExpressionNode(call.Callee.Location, "runner.expect", new NameExpressionNode(call.Callee.Location, "runner"), "expect"),
            Arguments = call.Arguments
                .Select(RewriteTestExpression)
                .Append(new LiteralExpressionNode(call.Location, "\"expect failed\""))
                .ToList(),
        },
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_true" },
            Arguments.Count: 1,
        } call => RewriteTestHelperCall(call, "expect_true", "expect_true failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_false" },
            Arguments.Count: 1,
        } call => RewriteTestHelperCall(call, "expect_false", "expect_false failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_bool" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_bool", "expect_eq_bool failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_int" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_int", "expect_eq_int failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_u64" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_u64", "expect_eq_u64 failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_usize" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_usize", "expect_eq_usize failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_double" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_double", "expect_eq_double failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_near_double" },
            Arguments.Count: 3,
        } call => RewriteTestHelperCall(call, "expect_near_double", "expect_near_double failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_string" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_string", "expect_eq_string failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_eq_string_view" },
            Arguments.Count: 2,
        } call => RewriteTestHelperCall(call, "expect_string_view", "expect_eq_string_view failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_null" },
            Arguments.Count: 1,
        } call => RewriteTestHelperCall(call, "expect_null", "expect_null failed"),
        CallExpressionNode
        {
            Callee: NameExpressionNode { SourceText: "expect_not_null" },
            Arguments.Count: 1,
        } call => RewriteTestHelperCall(call, "expect_not_null", "expect_not_null failed"),
        ParenthesizedExpressionNode parenthesized => parenthesized with { Expression = RewriteTestExpression(parenthesized.Expression) },
        CastExpressionNode cast => cast with { Expression = RewriteTestExpression(cast.Expression) },
        UnaryExpressionNode unary => unary with { Operand = RewriteTestExpression(unary.Operand) },
        PostfixExpressionNode postfix => postfix with { Operand = RewriteTestExpression(postfix.Operand) },
        SizeOfExpressionNode sizeOf => sizeOf with
        {
            ExpressionOperand = sizeOf.ExpressionOperand is null ? null : RewriteTestExpression(sizeOf.ExpressionOperand),
        },
        BinaryExpressionNode binary => binary with
        {
            Left = RewriteTestExpression(binary.Left),
            Right = RewriteTestExpression(binary.Right),
        },
        ConditionalExpressionNode conditional => conditional with
        {
            Condition = RewriteTestExpression(conditional.Condition),
            WhenTrue = RewriteTestExpression(conditional.WhenTrue),
            WhenFalse = RewriteTestExpression(conditional.WhenFalse),
        },
        ScalarRangeExpressionNode range => range with
        {
            Start = RewriteTestExpression(range.Start),
            End = RewriteTestExpression(range.End),
        },
        InitializerExpressionNode initializer => initializer with
        {
            Fields = initializer.Fields.Select(field => field with { Value = RewriteTestExpression(field.Value) }).ToList(),
            Values = initializer.Values.Select(RewriteTestExpression).ToList(),
        },
        FunctionExpressionNode functionExpression => functionExpression with
        {
            ExpressionBody = functionExpression.ExpressionBody is null ? null : RewriteTestExpression(functionExpression.ExpressionBody),
            BlockBody = functionExpression.BlockBody is null ? null : RewriteTestStatements(functionExpression.BlockBody),
        },
        AssignmentExpressionNode assignment => assignment with
        {
            Target = RewriteTestExpression(assignment.Target),
            Value = RewriteTestExpression(assignment.Value),
        },
        CallExpressionNode call => call with
        {
            Callee = RewriteTestExpression(call.Callee),
            Arguments = call.Arguments.Select(RewriteTestExpression).ToList(),
        },
        GenericCallExpressionNode call => call with
        {
            Callee = RewriteTestExpression(call.Callee),
            Arguments = call.Arguments.Select(RewriteTestExpression).ToList(),
        },
        MemberExpressionNode member => member with { Target = RewriteTestExpression(member.Target) },
        IndexExpressionNode index => index with
        {
            Target = RewriteTestExpression(index.Target),
            Index = RewriteTestExpression(index.Index),
        },
        RawExpressionNode raw when raw.SourceText.TrimStart().StartsWith("expect(", StringComparison.Ordinal) => raw with
        {
            SourceText = "runner.expect(" + raw.SourceText.Trim()[7..^1] + ", \"expect failed\")",
        },
        _ => expression,
    };

    private static CallExpressionNode RewriteTestHelperCall(
        CallExpressionNode call,
        string methodName,
        string message) => call with
        {
            SourceText = string.Empty,
            Callee = new MemberExpressionNode(
                call.Callee.Location,
                "runner." + methodName,
                new NameExpressionNode(call.Callee.Location, "runner"),
                methodName),
            Arguments = call.Arguments
                .Select(RewriteTestExpression)
                .Append(new LiteralExpressionNode(call.Location, $"\"{message}\""))
                .ToList(),
        };

    private static string EscapeStringLiteral(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string FormatRawAuditReport(CRawAuditReport report)
    {
        if (!report.HasEntries)
        {
            return "No raw C escapes found.";
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Raw C escapes: {report.Entries.Count}");
        foreach (var group in report.Entries.GroupBy(entry => entry.Category).OrderBy(group => group.Key))
        {
            builder.AppendLine($"{group.Key}: {group.Count()}");
        }

        builder.AppendLine();
        foreach (var entry in report.Entries)
        {
            builder.AppendLine($"{entry.Kind} {entry.Category} at {entry.Path}");
            builder.AppendLine($"  {entry.Text}");
        }

        return builder.ToString();
    }

    private static string FormatRawGenericUseAuditReport(IReadOnlyList<RawGenericUseAuditEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "No raw generic use fallback found.";
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Raw generic use fallback: {entries.Count}");
        foreach (var entry in entries)
        {
            builder.AppendLine($"- {entry.Context}: {entry.FunctionName}<{string.Join(", ", entry.TypeArguments)}>");
            builder.AppendLine($"  reason: {entry.Reason}");
            builder.AppendLine($"  expression: {entry.Expression}");
        }

        return builder.ToString();
    }

    public CompilationResult AuditAstCompleteness(IEnumerable<SourceFile> sources, bool includeStandardLibrary = false)
    {
        var sourceFiles = sources.ToList();
        var diagnostics = new DiagnosticBag();
        var parser = new CxParser(diagnostics);
        var userPrograms = sourceFiles
            .Select(parser.Parse)
            .ToList();
        var programs = includeStandardLibrary
            ? StandardLibrary.LoadCoreFiles().Select(parser.Parse).Concat(userPrograms).ToList()
            : userPrograms;

        if (!diagnostics.HasErrors)
        {
            new AstCompletenessAnalyzer(diagnostics).Analyze(programs);
        }

        return diagnostics.HasErrors
            ? CompilationResult.Failed(diagnostics.Diagnostics)
            : CompilationResult.Succeeded(string.Empty, diagnostics.Diagnostics);
    }

    private static IReadOnlyList<string> GetLinkerArguments(ProgramNode program)
    {
        var currentPlatform = GetCurrentPlatform();
        return program.CDeclarations
            .SelectMany(declaration => declaration.Links)
            .Where(link => link.Platform is null || string.Equals(link.Platform, currentPlatform, StringComparison.OrdinalIgnoreCase))
            .Select(link => link.Library.StartsWith("-", StringComparison.Ordinal) ? link.Library : "-l" + link.Library)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return "unknown";
    }

    private static ProgramNode GetRootProgram(IReadOnlyList<ProgramNode> userPrograms) =>
        userPrograms.FirstOrDefault(program => program.Functions.Any(function =>
            function.OwnerType is null
            && function.Name == "main"))
        ?? userPrograms.LastOrDefault()
        ?? throw new InvalidOperationException("At least one source file is required.");

    private static ProgramNode MergePrograms(IEnumerable<ProgramNode> programs, ProgramNode rootProgram)
    {
        var allPrograms = programs.ToList();
        var visibleModules = SelectVisibleModules(allPrograms, rootProgram);
        var aliasMap = GetImportAliasMap(allPrograms, visibleModules);
        var symbolImportMap = GetSymbolImportMap(allPrograms, visibleModules);
        var unaliasedModules = GetUnaliasedImportModules(allPrograms, visibleModules);
        var programList = allPrograms
            .Where(program => IsVisibleProgram(program, visibleModules))
            .Select(program => ProjectImportedProgram(program, aliasMap, symbolImportMap, unaliasedModules, rootProgram))
            .ToList();

        return rootProgram with
        {
            Includes = programList.SelectMany(program => program.Includes).ToList(),
            CDeclarations = programList.SelectMany(program => program.CDeclarations).ToList(),
            ExternFunctions = programList
                .SelectMany(program => program.ExternFunctions
                    .Concat(program.CDeclarations.SelectMany(declaration => declaration.Functions)))
                .ToList(),
            AttributeDeclarations = programList.SelectMany(program => program.AttributeDeclarations).ToList(),
            TypeAliases = programList
                .SelectMany(program => program.TypeAliases
                    .Concat(program.CDeclarations
                        .SelectMany(declaration => declaration.TypeAliases)))
                .ToList(),
            Requirements = programList.SelectMany(program => program.Requirements).ToList(),
            Enums = programList
                .SelectMany(program => program.Enums
                    .Concat(program.CDeclarations.SelectMany(declaration => declaration.Enums)))
                .ToList(),
            Interfaces = programList.SelectMany(program => program.Interfaces).ToList(),
            Structs = programList
                .SelectMany(program => program.Structs
                    .Concat(program.CDeclarations.SelectMany(declaration => declaration.Structs)))
                .ToList(),
            TypeAdapters = programList.SelectMany(program => program.TypeAdapters).ToList(),
            Extensions = programList.SelectMany(program => program.Extensions).ToList(),
            TaggedUnions = programList
                .SelectMany(program => program.TaggedUnions
                    .Concat(program.CDeclarations.SelectMany(declaration => declaration.Unions)))
                .ToList(),
            GlobalVariables = programList
                .SelectMany(program => program.GlobalVariables
                    .Concat(program.CDeclarations.SelectMany(declaration => declaration.Constants)))
                .ToList(),
            Functions = programList
                .SelectMany(program => program.Functions
                    .Concat(program.Structs.SelectMany(structNode => structNode.Methods))
                    .Concat(program.TaggedUnions.SelectMany(taggedUnion => taggedUnion.Methods)))
                .ToList(),
        };
    }

    private static IReadOnlySet<string> SelectVisibleModules(
        IReadOnlyList<ProgramNode> programs,
        ProgramNode rootProgram)
    {
        var modules = new HashSet<string>(StringComparer.Ordinal)
        {
            GetModuleName(rootProgram),
            "std.core",
        };

        if (GetModuleName(rootProgram).Length == 0)
        {
            modules.Add(string.Empty);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var program in programs)
            {
                var moduleName = GetModuleName(program);
                if (!IsVisibleProgram(program, modules))
                {
                    continue;
                }

                foreach (var importedModule in program.Imports.Select(import => import.ModuleName)
                    .Concat(program.SymbolImports.Select(import => import.ModuleName)))
                {
                    if (modules.Add(importedModule))
                    {
                        changed = true;
                    }
                }
            }
        }

        return modules;
    }

    private static bool IsVisibleProgram(ProgramNode program, IReadOnlySet<string> modules)
    {
        var moduleName = GetModuleName(program);
        return modules.Contains(moduleName)
            || string.Equals(program.Location.File.Path, "<derive>", StringComparison.Ordinal);
    }

    private static string GetModuleName(ProgramNode program) =>
        program.Module?.Name ?? string.Empty;

    private static void AnnotateModuleNames(
        ProgramNode program,
        IReadOnlyDictionary<string, string> moduleNamesByPath)
    {
        foreach (var declaration in program.Declarations)
        {
            AnnotateModuleName(declaration, moduleNamesByPath);
        }
    }

    private static void AnnotateModuleName(
        SyntaxNode node,
        IReadOnlyDictionary<string, string> moduleNamesByPath)
    {
        if (moduleNamesByPath.TryGetValue(node.Location.File.Path, out var moduleName))
        {
            node.Semantic.ModuleName = moduleName;
        }

        switch (node)
        {
            case StructNode structNode:
                foreach (var method in structNode.Methods)
                {
                    AnnotateModuleName(method, moduleNamesByPath);
                }

                break;
            case TaggedUnionNode union:
                foreach (var method in union.Methods)
                {
                    AnnotateModuleName(method, moduleNamesByPath);
                }

                break;
            case TypeAdapterNode adapter:
                foreach (var method in adapter.Methods)
                {
                    AnnotateModuleName(method, moduleNamesByPath);
                }

                break;
            case ExtensionNode extension:
                foreach (var method in extension.Methods)
                {
                    AnnotateModuleName(method, moduleNamesByPath);
                }

                break;
        }
    }

    private static IReadOnlyDictionary<string, string> GetImportAliasMap(
        IReadOnlyList<ProgramNode> programs,
        IReadOnlySet<string> visibleModules) =>
        programs
            .Where(program => IsVisibleProgram(program, visibleModules))
            .SelectMany(program => program.Imports
                .Where(import => import.Alias is not null)
                .Select(import => (import.ModuleName, Alias: import.Alias!)))
            .GroupBy(item => item.ModuleName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Alias, StringComparer.Ordinal);

    private static IReadOnlySet<string> GetUnaliasedImportModules(
        IReadOnlyList<ProgramNode> programs,
        IReadOnlySet<string> visibleModules) =>
        programs
            .Where(program => IsVisibleProgram(program, visibleModules))
            .SelectMany(program => program.Imports
                .Where(import => import.Alias is null)
                .Select(import => import.ModuleName))
            .Append("std.core")
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetSymbolImportMap(
        IReadOnlyList<ProgramNode> programs,
        IReadOnlySet<string> visibleModules) =>
        programs
            .Where(program => IsVisibleProgram(program, visibleModules))
            .SelectMany(program => program.SymbolImports.SelectMany(import =>
                import.Symbols.Select(symbol => new
                {
                    import.ModuleName,
                    symbol.Name,
                    VisibleName = symbol.Alias ?? symbol.Name,
                })))
            .GroupBy(item => item.ModuleName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group
                    .GroupBy(item => item.Name, StringComparer.Ordinal)
                    .ToDictionary(symbolGroup => symbolGroup.Key, symbolGroup => symbolGroup.Last().VisibleName, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static ProgramNode ProjectImportedProgram(
        ProgramNode program,
        IReadOnlyDictionary<string, string> aliasMap,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> symbolImportMap,
        IReadOnlySet<string> unaliasedModules,
        ProgramNode rootProgram)
    {
        var moduleName = GetModuleName(program);
        if (string.Equals(moduleName, GetModuleName(rootProgram), StringComparison.Ordinal)
            || moduleName.Length == 0
            || unaliasedModules.Contains(moduleName))
        {
            return symbolImportMap.TryGetValue(moduleName, out var fullModuleSymbols)
                ? MergeProjectedProgram(program, ProjectSymbolImportProgram(program, fullModuleSymbols))
                : program;
        }

        if (aliasMap.TryGetValue(moduleName, out var alias))
        {
            var qualifiedProgram = QualifyProgram(program, alias);
            return symbolImportMap.TryGetValue(moduleName, out var aliasedModuleSymbols)
                ? MergeProjectedProgram(qualifiedProgram, ProjectSymbolImportProgram(program, aliasedModuleSymbols))
                : qualifiedProgram;
        }

        return symbolImportMap.TryGetValue(moduleName, out var symbols)
            ? ProjectSymbolImportProgram(program, symbols)
            : EmptyProgram(program);
    }

    private static ProgramNode MergeProjectedProgram(ProgramNode program, ProgramNode projected) =>
        program with
        {
            CDeclarations = program.CDeclarations.Concat(projected.CDeclarations).ToList(),
            ExternFunctions = program.ExternFunctions.Concat(projected.ExternFunctions).ToList(),
            TypeAliases = program.TypeAliases.Concat(projected.TypeAliases).ToList(),
            Enums = program.Enums.Concat(projected.Enums).ToList(),
            Interfaces = program.Interfaces.Concat(projected.Interfaces).ToList(),
            Structs = program.Structs.Concat(projected.Structs).ToList(),
            TypeAdapters = program.TypeAdapters.Concat(projected.TypeAdapters).ToList(),
            Extensions = program.Extensions.Concat(projected.Extensions).ToList(),
            TaggedUnions = program.TaggedUnions.Concat(projected.TaggedUnions).ToList(),
            GlobalVariables = program.GlobalVariables.Concat(projected.GlobalVariables).ToList(),
            Functions = program.Functions.Concat(projected.Functions).ToList(),
        };

    private static ProgramNode QualifyProgram(ProgramNode program, string alias)
    {
        var typeNames = GetDeclaredTypeNames(program);
        return program with
        {
            CDeclarations = program.CDeclarations.Select(declaration => QualifyCDeclaration(declaration, alias, typeNames)).ToList(),
            ExternFunctions = program.ExternFunctions.Select(function => QualifyExternFunction(function, alias, typeNames)).ToList(),
            TypeAliases = program.TypeAliases.Select(typeAlias => typeAlias with
            {
                Name = QualifyName(alias, typeAlias.Name),
                TargetTypeNode = RewriteTypeNode(typeAlias.TargetTypeNode, QualifyType(typeAlias.TargetType, alias, typeNames)),
            }).ToList(),
            Enums = program.Enums.Select(enumNode => enumNode with
            {
                Name = QualifyName(alias, enumNode.Name),
                Members = enumNode.Members.Select(member => member with { Name = QualifyName(alias, member.Name) }).ToList(),
            }).ToList(),
            Interfaces = program.Interfaces.Select(interfaceNode => interfaceNode with
            {
                Name = QualifyName(alias, interfaceNode.Name),
                Methods = interfaceNode.Methods.Select(method => method with
                {
                    ReturnTypeNode = RewriteTypeNode(method.ReturnTypeNode, QualifyType(method.ReturnType, alias, typeNames)),
                    Parameters = method.Parameters.Select(parameter => QualifyParameter(parameter, alias, typeNames)).ToList(),
                }).ToList(),
            }).ToList(),
            Structs = program.Structs.Select(structNode => structNode with
            {
                Name = QualifyName(alias, structNode.Name),
                Fields = structNode.Fields.Select(field => field with { TypeNode = RewriteTypeNode(field.TypeNode, QualifyType(field.Type, alias, typeNames)) }).ToList(),
            }).ToList(),
            TypeAdapters = program.TypeAdapters.Select(adapter => adapter with
            {
                Name = QualifyName(alias, adapter.Name),
                BaseTypeNode = RewriteTypeNode(adapter.BaseTypeNode, QualifyType(adapter.BaseType, alias, typeNames)),
                Methods = adapter.Methods.Select(method => method with
                {
                    OwnerTypeNode = RewriteTypeNode(method.OwnerTypeNode, QualifyName(alias, method.OwnerType ?? adapter.Name)),
                    ReturnTypeNode = RewriteTypeNode(method.ReturnTypeNode, QualifyType(method.ReturnType, alias, typeNames)),
                    Parameters = method.Parameters.Select(parameter => QualifyParameter(parameter, alias, typeNames)).ToList(),
                }).ToList(),
            }).ToList(),
            Extensions = program.Extensions.Select(extension => extension with
            {
                TargetTypeNode = RewriteTypeNode(extension.TargetTypeNode, QualifyName(alias, extension.TargetType)),
                Methods = extension.Methods.Select(method => method with
                {
                    OwnerTypeNode = RewriteTypeNode(method.OwnerTypeNode, QualifyName(alias, method.OwnerType ?? extension.TargetType)),
                    ReturnTypeNode = RewriteTypeNode(method.ReturnTypeNode, QualifyType(method.ReturnType, alias, typeNames)),
                    Parameters = method.Parameters.Select(parameter => QualifyParameter(parameter, alias, typeNames)).ToList(),
                }).ToList(),
            }).ToList(),
            TaggedUnions = program.TaggedUnions.Select(union => union with
            {
                Name = QualifyName(alias, union.Name),
                Variants = union.Variants.Select(variant => variant with { TypeNode = RewriteTypeNode(variant.TypeNode, QualifyType(variant.Type, alias, typeNames)) }).ToList(),
            }).ToList(),
            GlobalVariables = program.GlobalVariables.Select(global => global with
            {
                Name = QualifyName(alias, global.Name),
                TypeNode = RewriteTypeNode(global.TypeNode, QualifyType(global.Type, alias, typeNames)),
            }).ToList(),
            Functions = program.Functions.Select(function => function.OwnerType is null
                ? QualifyFunction(function, alias, typeNames) with { Name = QualifyName(alias, function.Name) }
                : function).ToList(),
        };
    }

    private static ProgramNode ProjectSymbolImportProgram(
        ProgramNode program,
        IReadOnlyDictionary<string, string> symbols)
    {
        var typeNames = GetDeclaredTypeNames(program);
        return program with
        {
            CDeclarations = program.CDeclarations.Select(declaration => ProjectSymbolImportCDeclaration(declaration, symbols, typeNames)).ToList(),
            ExternFunctions = program.ExternFunctions
                .Where(function => symbols.ContainsKey(function.Name))
                .Select(function => RenameExternFunction(function, symbols[function.Name], symbols, typeNames))
                .ToList(),
            TypeAliases = program.TypeAliases
                .Where(typeAlias => symbols.ContainsKey(typeAlias.Name))
                .Select(typeAlias => typeAlias with
                {
                    Name = symbols[typeAlias.Name],
                    TargetTypeNode = RewriteTypeNode(typeAlias.TargetTypeNode, ProjectSymbolImportType(typeAlias.TargetType, symbols, typeNames)),
                })
                .ToList(),
            Enums = program.Enums
                .Where(enumNode => symbols.ContainsKey(enumNode.Name) || enumNode.Members.Any(member => symbols.ContainsKey(member.Name)))
                .Select(enumNode => enumNode with
                {
                    Name = symbols.GetValueOrDefault(enumNode.Name, enumNode.Name),
                    Members = enumNode.Members
                        .Where(member => symbols.ContainsKey(member.Name))
                        .Select(member => member with { Name = symbols[member.Name] })
                        .ToList(),
                })
                .ToList(),
            Interfaces = program.Interfaces
                .Where(interfaceNode => symbols.ContainsKey(interfaceNode.Name))
                .Select(interfaceNode => interfaceNode with { Name = symbols[interfaceNode.Name] })
                .ToList(),
            Structs = program.Structs
                .Where(structNode => symbols.ContainsKey(structNode.Name))
                .Select(structNode => structNode with
                {
                    Name = symbols[structNode.Name],
                    Fields = structNode.Fields.Select(field => field with { TypeNode = RewriteTypeNode(field.TypeNode, ProjectSymbolImportType(field.Type, symbols, typeNames)) }).ToList(),
                })
                .ToList(),
            TypeAdapters = program.TypeAdapters
                .Where(adapter => symbols.ContainsKey(adapter.Name))
                .Select(adapter => adapter with
                {
                    Name = symbols[adapter.Name],
                    BaseTypeNode = RewriteTypeNode(adapter.BaseTypeNode, ProjectSymbolImportType(adapter.BaseType, symbols, typeNames)),
                    Methods = adapter.Methods.Select(method => method with
                    {
                        OwnerTypeNode = RewriteTypeNode(method.OwnerTypeNode, symbols[adapter.Name]),
                        ReturnTypeNode = RewriteTypeNode(method.ReturnTypeNode, ProjectSymbolImportType(method.ReturnType, symbols, typeNames)),
                        Parameters = method.Parameters.Select(parameter => RenameParameter(parameter, symbols, typeNames)).ToList(),
                    }).ToList(),
                })
                .ToList(),
            Extensions = program.Extensions
                .Where(extension => symbols.ContainsKey(extension.TargetType))
                .Select(extension => extension with
                {
                    TargetTypeNode = RewriteTypeNode(extension.TargetTypeNode, symbols[extension.TargetType]),
                    Methods = extension.Methods.Select(method => method with
                    {
                        OwnerTypeNode = RewriteTypeNode(method.OwnerTypeNode, symbols[extension.TargetType]),
                        ReturnTypeNode = RewriteTypeNode(method.ReturnTypeNode, ProjectSymbolImportType(method.ReturnType, symbols, typeNames)),
                        Parameters = method.Parameters.Select(parameter => RenameParameter(parameter, symbols, typeNames)).ToList(),
                    }).ToList(),
                })
                .ToList(),
            TaggedUnions = program.TaggedUnions
                .Where(union => symbols.ContainsKey(union.Name))
                .Select(union => union with
                {
                    Name = symbols[union.Name],
                    Variants = union.Variants.Select(variant => variant with { TypeNode = RewriteTypeNode(variant.TypeNode, ProjectSymbolImportType(variant.Type, symbols, typeNames)) }).ToList(),
                })
                .ToList(),
            GlobalVariables = program.GlobalVariables
                .Where(global => symbols.ContainsKey(global.Name))
                .Select(global => global with
                {
                    Name = symbols[global.Name],
                    TypeNode = RewriteTypeNode(global.TypeNode, ProjectSymbolImportType(global.Type, symbols, typeNames)),
                })
                .ToList(),
            Functions = program.Functions
                .Where(function => function.OwnerType is not null || symbols.ContainsKey(function.Name))
                .Select(function => function.OwnerType is null
                    ? RenameFunction(function, symbols[function.Name], symbols, typeNames)
                    : function)
                .ToList(),
        };
    }

    private static CDeclareNode ProjectSymbolImportCDeclaration(
        CDeclareNode declaration,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlySet<string> typeNames) =>
        declaration with
        {
            TypeAliases = declaration.TypeAliases
                .Where(typeAlias => symbols.ContainsKey(typeAlias.Name))
                .Select(typeAlias => typeAlias with
                {
                    Name = symbols[typeAlias.Name],
                    TargetTypeNode = RewriteTypeNode(typeAlias.TargetTypeNode, ProjectSymbolImportType(typeAlias.TargetType, symbols, typeNames)),
                })
                .ToList(),
            Enums = declaration.Enums
                .Where(enumNode => symbols.ContainsKey(enumNode.Name) || enumNode.Members.Any(member => symbols.ContainsKey(member.Name)))
                .Select(enumNode => enumNode with
                {
                    Name = symbols.GetValueOrDefault(enumNode.Name, enumNode.Name),
                    Members = enumNode.Members
                        .Where(member => symbols.ContainsKey(member.Name))
                        .Select(member => member with { Name = symbols[member.Name] })
                        .ToList(),
                })
                .ToList(),
            Structs = declaration.Structs
                .Where(structNode => symbols.ContainsKey(structNode.Name))
                .Select(structNode => structNode with
                {
                    Name = symbols[structNode.Name],
                    Fields = structNode.Fields.Select(field => field with { TypeNode = RewriteTypeNode(field.TypeNode, ProjectSymbolImportType(field.Type, symbols, typeNames)) }).ToList(),
                })
                .ToList(),
            Unions = declaration.Unions
                .Where(union => symbols.ContainsKey(union.Name))
                .Select(union => union with
                {
                    Name = symbols[union.Name],
                    Variants = union.Variants.Select(variant => variant with { TypeNode = RewriteTypeNode(variant.TypeNode, ProjectSymbolImportType(variant.Type, symbols, typeNames)) }).ToList(),
                })
                .ToList(),
            Constants = declaration.Constants
                .Where(global => symbols.ContainsKey(global.Name))
                .Select(global => global with
                {
                    Name = symbols[global.Name],
                    TypeNode = RewriteTypeNode(global.TypeNode, ProjectSymbolImportType(global.Type, symbols, typeNames)),
                })
                .ToList(),
            Functions = declaration.Functions
                .Where(function => symbols.ContainsKey(function.Name))
                .Select(function => RenameExternFunction(function, symbols[function.Name], symbols, typeNames))
                .ToList(),
        };

    private static ProgramNode EmptyProgram(ProgramNode program) =>
        program with
        {
            Declarations = [],
            Includes = [],
            CDeclarations = [],
            ExternFunctions = [],
            AttributeDeclarations = [],
            TypeAliases = [],
            Requirements = [],
            Enums = [],
            Interfaces = [],
            Structs = [],
            TypeAdapters = [],
            Extensions = [],
            TaggedUnions = [],
            GlobalVariables = [],
            Functions = [],
        };

    private static CDeclareNode QualifyCDeclaration(
        CDeclareNode declaration,
        string alias,
        IReadOnlySet<string> typeNames) =>
        declaration with
        {
            TypeAliases = declaration.TypeAliases.Select(typeAlias => typeAlias with
            {
                Name = QualifyName(alias, typeAlias.Name),
                TargetTypeNode = RewriteTypeNode(typeAlias.TargetTypeNode, QualifyType(typeAlias.TargetType, alias, typeNames)),
            }).ToList(),
            Enums = declaration.Enums.Select(enumNode => enumNode with
            {
                Name = QualifyName(alias, enumNode.Name),
                Members = enumNode.Members.Select(member => member with { Name = QualifyName(alias, member.Name) }).ToList(),
            }).ToList(),
            Structs = declaration.Structs.Select(structNode => structNode with
            {
                Name = QualifyName(alias, structNode.Name),
                Fields = structNode.Fields.Select(field => field with { TypeNode = RewriteTypeNode(field.TypeNode, QualifyType(field.Type, alias, typeNames)) }).ToList(),
            }).ToList(),
            Unions = declaration.Unions.Select(union => union with
            {
                Name = QualifyName(alias, union.Name),
                Variants = union.Variants.Select(variant => variant with { TypeNode = RewriteTypeNode(variant.TypeNode, QualifyType(variant.Type, alias, typeNames)) }).ToList(),
            }).ToList(),
            Constants = declaration.Constants.Select(global => global with
            {
                Name = QualifyName(alias, global.Name),
                TypeNode = RewriteTypeNode(global.TypeNode, QualifyType(global.Type, alias, typeNames)),
            }).ToList(),
            Functions = declaration.Functions.Select(function => QualifyExternFunction(function, alias, typeNames)).ToList(),
        };

    private static ExternFunctionNode QualifyExternFunction(
        ExternFunctionNode function,
        string alias,
        IReadOnlySet<string> typeNames) =>
        function with
        {
            Name = QualifyName(alias, function.Name),
            ReturnTypeNode = RewriteTypeNode(function.ReturnTypeNode, QualifyType(function.ReturnType, alias, typeNames)),
            Parameters = function.Parameters.Select(parameter => QualifyParameter(parameter, alias, typeNames)).ToList(),
        };

    private static FunctionNode QualifyFunction(
        FunctionNode function,
        string alias,
        IReadOnlySet<string> typeNames) =>
        function with
        {
            ReturnTypeNode = RewriteTypeNode(function.ReturnTypeNode, QualifyType(function.ReturnType, alias, typeNames)),
            Parameters = function.Parameters.Select(parameter => QualifyParameter(parameter, alias, typeNames)).ToList(),
        };

    private static ExternFunctionNode RenameExternFunction(
        ExternFunctionNode function,
        string visibleName,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlySet<string> typeNames) =>
        function with
        {
            Name = visibleName,
            ReturnTypeNode = RewriteTypeNode(function.ReturnTypeNode, ProjectSymbolImportType(function.ReturnType, symbols, typeNames)),
            Parameters = function.Parameters.Select(parameter => RenameParameter(parameter, symbols, typeNames)).ToList(),
        };

    private static FunctionNode RenameFunction(
        FunctionNode function,
        string visibleName,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlySet<string> typeNames) =>
        function with
        {
            Name = visibleName,
            ReturnTypeNode = RewriteTypeNode(function.ReturnTypeNode, ProjectSymbolImportType(function.ReturnType, symbols, typeNames)),
            Parameters = function.Parameters.Select(parameter => RenameParameter(parameter, symbols, typeNames)).ToList(),
        };

    private static ParameterNode RenameParameter(
        ParameterNode parameter,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlySet<string> typeNames) =>
        parameter.IsVariadic
            ? parameter
            : parameter with { TypeNode = RewriteTypeNode(parameter.TypeNode, ProjectSymbolImportType(parameter.Type, symbols, typeNames)) };

    private static ParameterNode QualifyParameter(
        ParameterNode parameter,
        string alias,
        IReadOnlySet<string> typeNames) =>
        parameter.IsVariadic
            ? parameter
            : parameter with { TypeNode = RewriteTypeNode(parameter.TypeNode, QualifyType(parameter.Type, alias, typeNames)) };

    private static string QualifyName(string alias, string name) =>
        name.Contains('.', StringComparison.Ordinal) ? name : alias + "." + name;

    private static IReadOnlySet<string> GetDeclaredTypeNames(ProgramNode program) =>
        program.TypeAliases.Select(typeAlias => typeAlias.Name)
            .Concat(program.Structs.Select(structNode => structNode.Name))
            .Concat(program.TypeAdapters.Select(adapter => adapter.Name))
            .Concat(program.Enums.Select(enumNode => enumNode.Name))
            .Concat(program.Interfaces.Select(interfaceNode => interfaceNode.Name))
            .Concat(program.TaggedUnions.Select(union => union.Name))
            .Concat(program.CDeclarations.SelectMany(declaration => declaration.TypeAliases.Select(typeAlias => typeAlias.Name)))
            .Concat(program.CDeclarations.SelectMany(declaration => declaration.Structs.Select(structNode => structNode.Name)))
            .Concat(program.CDeclarations.SelectMany(declaration => declaration.Enums.Select(enumNode => enumNode.Name)))
            .Concat(program.CDeclarations.SelectMany(declaration => declaration.Unions.Select(union => union.Name)))
            .ToHashSet(StringComparer.Ordinal);

    private static string QualifyType(
        string type,
        string alias,
        IReadOnlySet<string> typeNames)
    {
        foreach (var typeName in typeNames.OrderByDescending(name => name.Length))
        {
            type = System.Text.RegularExpressions.Regex.Replace(
                type,
                $@"(?<![A-Za-z0-9_\.]){System.Text.RegularExpressions.Regex.Escape(typeName)}(?![A-Za-z0-9_])",
                QualifyName(alias, typeName));
        }

        return type;
    }

    private static string ProjectSymbolImportType(
        string type,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlySet<string> typeNames)
    {
        foreach (var typeName in typeNames.OrderByDescending(name => name.Length))
        {
            if (!symbols.TryGetValue(typeName, out var visibleName))
            {
                continue;
            }

            type = System.Text.RegularExpressions.Regex.Replace(
                type,
                $@"(?<![A-Za-z0-9_\.]){System.Text.RegularExpressions.Regex.Escape(typeName)}(?![A-Za-z0-9_])",
                visibleName);
        }

        return type;
    }

    private static TypeNode RewriteTypeNode(TypeNode? typeNode, string typeName) =>
        new(
            typeNode?.Location ?? new Location(new SourceFile("<type-rewrite>", string.Empty), 0, 1, 1),
            typeName,
            TypeSyntaxParser.Parse(typeName));
}
