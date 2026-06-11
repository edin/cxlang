using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ModuleVisibilityAnalyzer(
    DiagnosticBag diagnostics,
    IReadOnlyList<ProgramNode> availablePrograms)
{
    private readonly TypeRefParser _typeRefParser = new(availablePrograms.FirstOrDefault() ?? new ProgramNode(
        Cx.Compiler.Syntax.Location.Synthetic("<module-visibility>"),
        []));

    private readonly IReadOnlyDictionary<string, ModuleSymbols> _modules = availablePrograms
        .GroupBy(program => program.Module?.Name ?? string.Empty, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => ModuleSymbols.From(group),
            StringComparer.Ordinal);

    public void Analyze(IReadOnlyList<ProgramNode> userPrograms)
    {
        foreach (var group in userPrograms.GroupBy(program => program.Module?.Name ?? string.Empty, StringComparer.Ordinal))
        {
            var module = group.Key;
            var visibility = ModuleVisibility.From(module, group, _modules);
            foreach (var program in group)
            {
                AnalyzeProgram(program, visibility);
            }
        }
    }

    private void AnalyzeProgram(ProgramNode program, ModuleVisibility visibility)
    {
        foreach (var typeAlias in program.TypeAliases)
        {
            AnalyzeType(typeAlias.TargetTypeNode, typeAlias.Location, visibility);
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            AnalyzeType(externFunction.ReturnTypeNode, externFunction.Location, visibility);
            foreach (var parameter in externFunction.Parameters.Where(parameter => !parameter.IsVariadic))
            {
                AnalyzeType(parameter.TypeNode, parameter.Location, visibility);
            }
        }

        foreach (var global in program.GlobalVariables)
        {
            AnalyzeType(global.TypeNode, global.Location, visibility);
            AnalyzeExpression(global.Initializer, visibility);
        }

        foreach (var structNode in program.Structs)
        {
            foreach (var field in structNode.Fields)
            {
                AnalyzeType(field.TypeNode, field.Location, visibility, structNode.TypeParameters);
            }

            foreach (var method in structNode.Methods)
            {
                AnalyzeFunction(method, visibility);
            }
        }

        foreach (var union in program.TaggedUnions)
        {
            foreach (var variant in union.Variants)
            {
                AnalyzeType(variant.TypeNode, variant.Location, visibility);
            }

            foreach (var method in union.Methods)
            {
                AnalyzeFunction(method, visibility);
            }
        }

        foreach (var function in program.Functions)
        {
            AnalyzeFunction(function, visibility);
        }
    }

    private void AnalyzeFunction(FunctionNode function, ModuleVisibility visibility)
    {
        AnalyzeType(function.ReturnTypeNode, function.Location, visibility, function.TypeParameters);
        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            AnalyzeType(parameter.TypeNode, parameter.Location, visibility, function.TypeParameters);
        }

        var locals = new HashSet<string>(function.Parameters.Select(parameter => parameter.Name), StringComparer.Ordinal);
        foreach (var local in CollectLocalNames(function.Body))
        {
            locals.Add(local);
        }

        AnalyzeStatements(function.Body, visibility, locals);
    }

    private void AnalyzeStatements(
        IReadOnlyList<StatementNode> statements,
        ModuleVisibility visibility,
        IReadOnlySet<string> locals)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    AnalyzeType(let.TypeNode, let.Location, visibility);
                    AnalyzeExpression(let.Initializer, visibility, locals);
                    break;
                case ReturnStatement { Expression: not null } returnStatement:
                    AnalyzeExpression(returnStatement.Expression, visibility, locals);
                    break;
                case IfStatement ifStatement:
                    AnalyzeExpression(ifStatement.Condition, visibility, locals);
                    AnalyzeStatements(ifStatement.ThenBody, visibility, locals);
                    if (ifStatement.ElseBranch is not null)
                    {
                        AnalyzeStatements([ifStatement.ElseBranch], visibility, locals);
                    }

                    break;
                case ElseBlockStatement elseBlock:
                    AnalyzeStatements(elseBlock.Body, visibility, locals);
                    break;
                case WhileStatement whileStatement:
                    AnalyzeExpression(whileStatement.Condition, visibility, locals);
                    AnalyzeStatements(whileStatement.Body, visibility, locals);
                    break;
                case ForStatement forStatement:
                    AnalyzeForInitializer(forStatement.Initializer, visibility, locals);
                    AnalyzeExpression(forStatement.Condition, visibility, locals);
                    AnalyzeExpression(forStatement.Increment, visibility, locals);
                    AnalyzeStatements(forStatement.Body, visibility, locals);
                    break;
                case ForeachStatement foreachStatement:
                    AnalyzeExpression(foreachStatement.IterableExpression, visibility, locals);
                    AnalyzeStatements(foreachStatement.Body, visibility, AddForeachLocals(locals, foreachStatement));
                    break;
                case SwitchStatement switchStatement:
                    AnalyzeExpression(switchStatement.Expression, visibility, locals);
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        AnalyzeExpression(switchCase.Pattern, visibility, locals);
                        AnalyzeStatements(switchCase.Body, visibility, locals);
                    }

                    AnalyzeStatements(switchStatement.DefaultBody, visibility, locals);
                    break;
                case MatchStatement matchStatement:
                    AnalyzeExpression(matchStatement.Expression, visibility, locals);
                    foreach (var arm in matchStatement.Arms)
                    {
                        AnalyzeStatements(arm.Body, visibility, locals);
                    }

                    break;
                case CStatement cStatement:
                    AnalyzeExpression(cStatement.Expression, visibility, locals);
                    break;
            }
        }
    }

    private void AnalyzeForInitializer(
        ForInitializerNode initializer,
        ModuleVisibility visibility,
        IReadOnlySet<string> locals)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                AnalyzeType(declaration.TypeNode, declaration.Location, visibility);
                AnalyzeExpression(declaration.Initializer, visibility, locals);
                break;
            case ForExpressionInitializerNode expression:
                AnalyzeExpression(expression.Expression, visibility, locals);
                break;
        }
    }

    private void AnalyzeExpression(
        ExpressionNode? expression,
        ModuleVisibility visibility,
        IReadOnlySet<string>? locals = null)
    {
        if (expression is null)
        {
            return;
        }

        switch (expression)
        {
            case NameExpressionNode name:
                AnalyzeName(name, visibility, locals ?? new HashSet<string>(StringComparer.Ordinal));
                break;
            case ParenthesizedExpressionNode parenthesized:
                AnalyzeExpression(parenthesized.Expression, visibility, locals);
                break;
            case CastExpressionNode cast:
                AnalyzeType(cast.TargetTypeNode, cast.Location, visibility);
                AnalyzeExpression(cast.Expression, visibility, locals);
                break;
            case UnaryExpressionNode unary:
                AnalyzeExpression(unary.Operand, visibility, locals);
                break;
            case PostfixExpressionNode postfix:
                AnalyzeExpression(postfix.Operand, visibility, locals);
                break;
            case SizeOfExpressionNode sizeOf:
                if (sizeOf.TypeOperandNode is not null)
                {
                    AnalyzeType(sizeOf.TypeOperandNode, sizeOf.Location, visibility);
                }

                AnalyzeExpression(sizeOf.ExpressionOperand, visibility, locals);
                break;
            case BinaryExpressionNode binary:
                AnalyzeExpression(binary.Left, visibility, locals);
                AnalyzeExpression(binary.Right, visibility, locals);
                break;
            case ScalarRangeExpressionNode range:
                AnalyzeExpression(range.Start, visibility, locals);
                AnalyzeExpression(range.End, visibility, locals);
                break;
            case ConditionalExpressionNode conditional:
                AnalyzeExpression(conditional.Condition, visibility, locals);
                AnalyzeExpression(conditional.WhenTrue, visibility, locals);
                AnalyzeExpression(conditional.WhenFalse, visibility, locals);
                break;
            case InitializerExpressionNode initializer:
                if (initializer.TypeNameNode is not null)
                {
                    AnalyzeType(initializer.TypeNameNode, initializer.Location, visibility);
                }

                foreach (var field in initializer.Fields)
                {
                    AnalyzeExpression(field.Value, visibility, locals);
                }

                foreach (var value in initializer.Values)
                {
                    AnalyzeExpression(value, visibility, locals);
                }

                break;
            case FunctionExpressionNode function:
                foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
                {
                    AnalyzeType(parameter.TypeNode, parameter.Location, visibility);
                }

                if (function.ReturnTypeNode is not null)
                {
                    AnalyzeType(function.ReturnTypeNode, function.Location, visibility);
                }

                AnalyzeExpression(function.ExpressionBody, visibility, locals);
                break;
            case AssignmentExpressionNode assignment:
                AnalyzeExpression(assignment.Target, visibility, locals);
                AnalyzeExpression(assignment.Value, visibility, locals);
                break;
            case CallExpressionNode call:
                AnalyzeCall(call.Callee, call.Location, visibility, locals ?? new HashSet<string>(StringComparer.Ordinal));
                foreach (var argument in call.Arguments)
                {
                    AnalyzeExpression(argument, visibility, locals);
                }

                break;
            case GenericCallExpressionNode call:
                foreach (var typeArgument in call.TypeArgumentNodes)
                {
                    AnalyzeType(typeArgument, call.Location, visibility);
                }

                AnalyzeCall(call.Callee, call.Location, visibility, locals ?? new HashSet<string>(StringComparer.Ordinal));
                foreach (var argument in call.Arguments)
                {
                    AnalyzeExpression(argument, visibility, locals);
                }

                break;
            case MemberExpressionNode member:
                if (GetQualifiedName(member) is not { } qualifiedName
                    || !visibility.IsVisibleFunction(qualifiedName)
                    && !visibility.IsVisibleValue(qualifiedName)
                    && !visibility.IsVisibleType(qualifiedName))
                {
                    AnalyzeExpression(member.Target, visibility, locals);
                }

                break;
            case IndexExpressionNode index:
                AnalyzeExpression(index.Target, visibility, locals);
                AnalyzeExpression(index.Index, visibility, locals);
                break;
        }
    }

    private void AnalyzeCall(
        ExpressionNode callee,
        Cx.Compiler.Syntax.Location location,
        ModuleVisibility visibility,
        IReadOnlySet<string> locals)
    {
        if (GetQualifiedName(callee) is not { } name || locals.Contains(name))
        {
            AnalyzeExpression(callee, visibility, locals);
            return;
        }

        if (!visibility.SymbolExistsAsFunction(name) || visibility.IsVisibleFunction(name))
        {
            AnalyzeExpression(callee, visibility, locals);
            return;
        }

        diagnostics.Report(location, visibility.BuildFunctionDiagnostic(name));
    }

    private void AnalyzeName(
        NameExpressionNode name,
        ModuleVisibility visibility,
        IReadOnlySet<string> locals)
    {
        if (locals.Contains(name.SourceText)
            || !visibility.SymbolExistsAsValue(name.SourceText)
            || visibility.IsVisibleValue(name.SourceText))
        {
            return;
        }

        diagnostics.Report(name.Location, visibility.BuildValueDiagnostic(name.SourceText));
    }

    private void AnalyzeType(
        TypeNode? typeNode,
        Cx.Compiler.Syntax.Location location,
        ModuleVisibility visibility,
        IReadOnlyList<string>? typeParameters = null) =>
        AnalyzeType(TypeText(typeNode), location, visibility, typeParameters);

    private void AnalyzeType(
        string type,
        Cx.Compiler.Syntax.Location location,
        ModuleVisibility visibility,
        IReadOnlyList<string>? typeParameters = null)
    {
        foreach (var typeName in FindTypeNames(type)
            .Where(typeName => typeParameters is null || !typeParameters.Contains(typeName, StringComparer.Ordinal)))
        {
            if (!visibility.SymbolExistsAsType(typeName) || visibility.IsVisibleType(typeName))
            {
                continue;
            }

            diagnostics.Report(location, visibility.BuildTypeDiagnostic(typeName));
        }
    }

    private static IEnumerable<string> CollectLocalNames(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return let.Name;
                    break;
                case ForStatement { Initializer: ForDeclarationInitializerNode declaration }:
                    yield return declaration.Name;
                    foreach (var local in CollectLocalNames(GetBody(statement)))
                    {
                        yield return local;
                    }

                    break;
                case ForeachStatement foreachStatement:
                    foreach (var local in GetForeachBindingNames(foreachStatement))
                    {
                        yield return local;
                    }

                    foreach (var local in CollectLocalNames(foreachStatement.Body))
                    {
                        yield return local;
                    }

                    break;
                default:
                    foreach (var local in CollectLocalNames(GetBody(statement)))
                    {
                        yield return local;
                    }

                    break;
            }
        }
    }

    private static IReadOnlyList<StatementNode> GetBody(StatementNode statement) => statement switch
    {
        IfStatement ifStatement => ifStatement.ThenBody
            .Concat(ifStatement.ElseBranch is null ? [] : [ifStatement.ElseBranch])
            .ToList(),
        ElseBlockStatement elseBlock => elseBlock.Body,
        WhileStatement whileStatement => whileStatement.Body,
        ForStatement forStatement => forStatement.Body,
        ForeachStatement foreachStatement => foreachStatement.Body,
        SwitchStatement switchStatement => switchStatement.Cases
            .SelectMany(switchCase => switchCase.Body)
            .Concat(switchStatement.DefaultBody)
            .ToList(),
        MatchStatement matchStatement => matchStatement.Arms.SelectMany(arm => arm.Body).ToList(),
        _ => [],
    };

    private static IReadOnlySet<string> AddForeachLocals(IReadOnlySet<string> locals, ForeachStatement foreachStatement)
    {
        var scoped = locals.ToHashSet(StringComparer.Ordinal);
        foreach (var name in GetForeachBindingNames(foreachStatement))
        {
            scoped.Add(name);
        }

        return scoped;
    }

    private static IEnumerable<string> GetForeachBindingNames(ForeachStatement foreachStatement)
    {
        if (foreachStatement.IndexBinding is not null)
        {
            yield return foreachStatement.IndexBinding.Name;
        }

        if (foreachStatement.KeyBinding is not null)
        {
            yield return foreachStatement.KeyBinding.Name;
        }

        yield return foreachStatement.ValueBinding.Name;
    }

    private static string? GetQualifiedName(ExpressionNode expression) => expression switch
    {
        NameExpressionNode name => name.SourceText,
        ParenthesizedExpressionNode parenthesized => GetQualifiedName(parenthesized.Expression),
        MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null,
    };

    private static IReadOnlyList<string> FindTypeNames(string type)
    {
        var names = new List<string>();
        CollectTypeNames(TypeSyntaxParser.Parse(type), names);
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectTypeNames(TypeSyntaxNode? syntax, List<string> names)
    {
        switch (syntax)
        {
            case null:
                break;
            case NamedTypeSyntaxNode named:
                names.Add(NormalizeTypeName(named.Name));
                break;
            case GenericTypeSyntaxNode generic:
                CollectTypeNames(generic.Target, names);
                foreach (var argument in generic.Arguments)
                {
                    CollectTypeNames(argument, names);
                }
                break;
            case PointerTypeSyntaxNode pointer:
                CollectTypeNames(pointer.Element, names);
                break;
            case FixedArrayTypeSyntaxNode fixedArray:
                CollectTypeNames(fixedArray.Element, names);
                break;
            case FunctionTypeSyntaxNode function:
                foreach (var parameter in function.Parameters)
                {
                    CollectTypeNames(parameter, names);
                }
                CollectTypeNames(function.ReturnType, names);
                break;
        }
    }

    private static string NormalizeTypeName(string type)
    {
        type = BuiltinTypes.Normalize(type);
        return BuiltinTypes.IsBuiltin(type) ? string.Empty : type;
    }

    private static string? OwnerType(FunctionNode function)
    {
        var type = function.OwnerTypeNode?.TypeName ?? string.Empty;
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }

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

    private sealed record ModuleVisibility(
        string ModuleName,
        IReadOnlyDictionary<string, ModuleSymbols> Modules,
        IReadOnlySet<string> BareModules,
        IReadOnlyDictionary<string, string> Aliases,
        IReadOnlyDictionary<string, ImportedSymbol> SymbolImports)
    {
        public static ModuleVisibility From(
            string moduleName,
            IEnumerable<ProgramNode> programs,
            IReadOnlyDictionary<string, ModuleSymbols> modules)
        {
            var imports = programs.SelectMany(program => program.Imports).ToList();
            var symbolImports = programs.SelectMany(program => program.SymbolImports).ToList();
            return new ModuleVisibility(
                moduleName,
                modules,
                imports.Where(import => import.Alias is null)
                    .Select(import => import.ModuleName)
                    .Append(moduleName)
                    .Append("std.core")
                    .ToHashSet(StringComparer.Ordinal),
                imports.Where(import => import.Alias is not null)
                    .GroupBy(import => import.Alias!, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().ModuleName, StringComparer.Ordinal),
                symbolImports.SelectMany(import => import.Symbols.Select(symbol =>
                        new ImportedSymbol(symbol.Alias ?? symbol.Name, symbol.Name, import.ModuleName)))
                    .GroupBy(symbol => symbol.VisibleName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal));
        }

        public bool SymbolExistsAsType(string name) => SymbolExists(name, symbols => symbols.Types);

        public bool SymbolExistsAsValue(string name) => SymbolExists(name, symbols => symbols.Values);

        public bool SymbolExistsAsFunction(string name) => SymbolExists(name, symbols => symbols.Functions);

        public bool IsVisibleType(string name) => IsVisible(name, symbols => symbols.Types);

        public bool IsVisibleValue(string name) => IsVisible(name, symbols => symbols.Values);

        public bool IsVisibleFunction(string name) => IsVisible(name, symbols => symbols.Functions);

        public string BuildTypeDiagnostic(string name) => BuildDiagnostic("type", name);

        public string BuildValueDiagnostic(string name) => BuildDiagnostic("symbol", name);

        public string BuildFunctionDiagnostic(string name) => BuildDiagnostic("function", name);

        private bool SymbolExists(string name, Func<ModuleSymbols, IReadOnlySet<string>> selectSymbols)
        {
            var (qualifier, symbol) = SplitQualifiedName(name);
            return qualifier is not null
                ? Aliases.TryGetValue(qualifier, out var moduleName)
                    && Modules.TryGetValue(moduleName, out var moduleSymbols)
                    && selectSymbols(moduleSymbols).Contains(symbol)
                : Modules.Values.Any(module => selectSymbols(module).Contains(symbol));
        }

        private bool IsVisible(string name, Func<ModuleSymbols, IReadOnlySet<string>> selectSymbols)
        {
            var (qualifier, symbol) = SplitQualifiedName(name);
            if (qualifier is not null)
            {
                return Aliases.TryGetValue(qualifier, out var moduleName)
                    && Modules.TryGetValue(moduleName, out var moduleSymbols)
                    && selectSymbols(moduleSymbols).Contains(symbol);
            }

            if (SymbolImports.TryGetValue(symbol, out var imported)
                && Modules.TryGetValue(imported.ModuleName, out var importedModule)
                && selectSymbols(importedModule).Contains(imported.SourceName))
            {
                return true;
            }

            return BareModules.Any(moduleName =>
                Modules.TryGetValue(moduleName, out var module)
                && selectSymbols(module).Contains(symbol));
        }

        private string BuildDiagnostic(string kind, string name)
        {
            var (qualifier, symbol) = SplitQualifiedName(name);
            if (qualifier is null)
            {
                foreach (var alias in Aliases)
                {
                    if (Modules.TryGetValue(alias.Value, out var module)
                        && ModuleContains(module, kind, symbol))
                    {
                        return $"Unknown {kind} '{name}'. Did you mean '{alias.Key}.{symbol}'?";
                    }
                }

                var partiallyImportedModule = FindPartiallyImportedModuleContaining(kind, symbol);
                if (partiallyImportedModule is not null)
                {
                    return $"Unknown {kind} '{name}'. Did you mean 'from {partiallyImportedModule} import {symbol}'?";
                }

                var moduleName = FindModuleContaining(kind, symbol);
                return moduleName is null
                    ? $"Unknown {kind} '{name}'."
                    : $"Unknown {kind} '{name}'. Did you mean to import {moduleName}?";
            }

            return $"Unknown {kind} '{name}'.";
        }

        private string? FindModuleContaining(string kind, string symbol) =>
            Modules
                .Where(item => item.Key.Length > 0)
                .Where(item => !BareModules.Contains(item.Key))
                .Where(item => ModuleContains(item.Value, kind, symbol))
                .Select(item => item.Key)
                .OrderBy(moduleName => moduleName, StringComparer.Ordinal)
                .FirstOrDefault();

        private string? FindPartiallyImportedModuleContaining(string kind, string symbol) =>
            SymbolImports.Values
                .Select(import => import.ModuleName)
                .Distinct(StringComparer.Ordinal)
                .Where(moduleName => Modules.TryGetValue(moduleName, out var module)
                    && ModuleContains(module, kind, symbol))
                .OrderBy(moduleName => moduleName, StringComparer.Ordinal)
                .FirstOrDefault();

        private static bool ModuleContains(ModuleSymbols module, string kind, string symbol) => kind switch
        {
            "type" => module.Types.Contains(symbol),
            "symbol" => module.Values.Contains(symbol),
            "function" => module.Functions.Contains(symbol),
            _ => false,
        };

        private static (string? Qualifier, string Symbol) SplitQualifiedName(string name)
        {
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            return dot < 0
                ? (null, name)
                : (name[..dot], name[(dot + 1)..]);
        }
    }

    private sealed record ImportedSymbol(string VisibleName, string SourceName, string ModuleName);

    private sealed record ModuleSymbols(
        IReadOnlySet<string> Types,
        IReadOnlySet<string> Values,
        IReadOnlySet<string> Functions)
    {
        public static ModuleSymbols From(IEnumerable<ProgramNode> programs)
        {
            var typeNames = new HashSet<string>(StringComparer.Ordinal);
            var valueNames = new HashSet<string>(StringComparer.Ordinal);
            var functionNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var program in programs)
            {
                foreach (var typeAlias in program.TypeAliases)
                {
                    typeNames.Add(typeAlias.Name);
                }

                foreach (var enumNode in program.Enums)
                {
                    typeNames.Add(enumNode.Name);
                    foreach (var member in enumNode.Members)
                    {
                        valueNames.Add(member.Name);
                    }
                }

                foreach (var interfaceNode in program.Interfaces)
                {
                    typeNames.Add(interfaceNode.Name);
                }

                foreach (var structNode in program.Structs)
                {
                    typeNames.Add(structNode.Name);
                    foreach (var method in structNode.Methods.Where(method => method.IsStatic))
                    {
                        functionNames.Add($"{structNode.Name}.{method.Name}");
                    }
                }

                foreach (var union in program.TaggedUnions)
                {
                    typeNames.Add(union.Name);
                    foreach (var variant in union.Variants)
                    {
                        valueNames.Add(variant.Name);
                        functionNames.Add($"{union.Name}.{variant.Name}");
                    }
                }

                foreach (var global in program.GlobalVariables)
                {
                    valueNames.Add(global.Name);
                }

                foreach (var function in program.Functions.Where(function => OwnerType(function) is null))
                {
                    functionNames.Add(function.Name);
                }

                foreach (var externFunction in program.ExternFunctions)
                {
                    functionNames.Add(externFunction.Name);
                }

                foreach (var declaration in program.CDeclarations)
                {
                    foreach (var typeAlias in declaration.TypeAliases)
                    {
                        typeNames.Add(typeAlias.Name);
                    }

                    foreach (var enumNode in declaration.Enums)
                    {
                        typeNames.Add(enumNode.Name);
                        foreach (var member in enumNode.Members)
                        {
                            valueNames.Add(member.Name);
                        }
                    }

                    foreach (var structNode in declaration.Structs)
                    {
                        typeNames.Add(structNode.Name);
                    }

                    foreach (var union in declaration.Unions)
                    {
                        typeNames.Add(union.Name);
                    }

                    foreach (var constant in declaration.Constants)
                    {
                        valueNames.Add(constant.Name);
                    }

                    foreach (var function in declaration.Functions)
                    {
                        functionNames.Add(function.Name);
                    }
                }
            }

            return new ModuleSymbols(typeNames, valueNames, functionNames);
        }
    }
}
