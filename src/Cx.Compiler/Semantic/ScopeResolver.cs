using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ScopeResolver(DiagnosticBag diagnostics, SemanticModel model)
{
    private IReadOnlyList<FunctionNode> _functions = [];
    private ProgramNode? _program;
    private ExpressionTypeResolver? _expressionTypeResolver;

    public void Resolve(ProgramNode program)
    {
        _program = program;
        _functions = GetAllFunctions(program);
        _expressionTypeResolver = new ExpressionTypeResolver(program);
        DeclareTopLevelSymbols(program);

        foreach (var function in program.Functions)
        {
            ResolveFunction(function);
        }

        foreach (var structNode in program.Structs)
        {
            foreach (var method in structNode.Methods)
            {
                ResolveFunction(method);
            }
        }

        foreach (var union in program.TaggedUnions)
        {
            foreach (var method in union.Methods)
            {
                ResolveFunction(method);
            }
        }
    }

    private void DeclareTopLevelSymbols(ProgramNode program)
    {
        foreach (var typeAlias in program.TypeAliases)
        {
            DeclareTopLevel(typeAlias.Name, SymbolKind.Type, TypeText(typeAlias.TargetTypeNode), typeAlias.Location, typeAlias);
        }

        foreach (var requirement in program.Requirements)
        {
            DeclareTopLevel(requirement.Name, SymbolKind.Type, null, requirement.Location, requirement);
        }

        foreach (var enumNode in program.Enums)
        {
            DeclareTopLevel(enumNode.Name, SymbolKind.Type, enumNode.Name, enumNode.Location, enumNode);
        }

        foreach (var interfaceNode in program.Interfaces)
        {
            DeclareTopLevel(interfaceNode.Name, SymbolKind.Type, interfaceNode.Name, interfaceNode.Location, interfaceNode);
        }

        foreach (var structNode in program.Structs)
        {
            DeclareTopLevel(structNode.Name, SymbolKind.Type, structNode.Name, structNode.Location, structNode);
        }

        foreach (var adapter in program.TypeAdapters)
        {
            DeclareTopLevel(adapter.Name, SymbolKind.Type, adapter.Name, adapter.Location, adapter);
        }

        foreach (var union in program.TaggedUnions)
        {
            DeclareTopLevel(union.Name, SymbolKind.Type, union.Name, union.Location, union);
        }

        foreach (var global in program.GlobalVariables)
        {
            DeclareTopLevel(global.Name, SymbolKind.Global, TypeText(global.TypeNode), global.Location, global);
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            DeclareTopLevel(externFunction.Name, SymbolKind.Function, TypeText(externFunction.ReturnTypeNode), externFunction.Location, externFunction);
        }

        foreach (var function in _functions)
        {
            var symbol = new Symbol(function.Name, SymbolKind.Function, TypeText(function.ReturnTypeNode), function.Location, function);
            function.Semantic.Symbol = symbol;
            if (OwnerType(function) is null)
            {
                model.RootScope.TryDeclare(symbol);
            }
        }
    }

    private void ResolveFunction(FunctionNode function)
    {
        var functionScope = model.RootScope.CreateChild();
        var ownerType = OwnerType(function);
        if (!function.IsStatic
            && ownerType is not null
            && !function.Parameters.Any(parameter => string.Equals(parameter.Name, "self", StringComparison.Ordinal)))
        {
            Declare(functionScope, "self", SymbolKind.Parameter, ownerType + "*", function.Location);
        }

        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            Declare(functionScope, parameter.Name, SymbolKind.Parameter, TypeText(parameter.TypeNode), parameter.Location, parameter);
        }

        ResolveStatements(function.Body, functionScope);
    }

    private void ResolveStatements(IReadOnlyList<StatementNode> statements, Scope scope)
    {
        foreach (var statement in statements)
        {
            ResolveStatement(statement, scope);
        }
    }

    private void ResolveStatement(StatementNode statement, Scope scope)
    {
        switch (statement)
        {
            case LetStatement let:
                ResolveExpression(let.Initializer, scope);
                Declare(scope, let.Name, SymbolKind.Local, TypeTextOrNull(let.TypeNode), let.Location, let);
                break;

            case ReturnStatement ret:
                ResolveExpression(ret.Expression, scope);
                break;

            case CStatement c:
                ResolveExpression(c.Expression, scope);
                break;

            case IfStatement ifStatement:
                ResolveExpression(ifStatement.Condition, scope);
                ResolveStatements(ifStatement.ThenBody, scope.CreateChild());
                if (ifStatement.ElseBranch is not null)
                {
                    ResolveStatement(ifStatement.ElseBranch, scope.CreateChild());
                }

                break;

            case ElseBlockStatement elseBlock:
                ResolveStatements(elseBlock.Body, scope);
                break;

            case WhileStatement whileStatement:
                ResolveExpression(whileStatement.Condition, scope);
                ResolveStatements(whileStatement.Body, scope.CreateChild());
                break;

            case ForStatement forStatement:
                var forScope = scope.CreateChild();
                ResolveForInitializer(forStatement.Initializer, forScope);
                ResolveExpression(forStatement.Condition, forScope);
                ResolveExpression(forStatement.Increment, forScope);
                ResolveStatements(forStatement.Body, forScope.CreateChild());
                break;

            case ForeachStatement foreachStatement:
                ResolveExpression(foreachStatement.IterableExpression, scope);
                var foreachScope = scope.CreateChild();
                DeclareForeachBinding(foreachScope, foreachStatement.IndexBinding);
                DeclareForeachBinding(foreachScope, foreachStatement.KeyBinding);
                DeclareForeachBinding(foreachScope, foreachStatement.ValueBinding);
                ResolveStatements(foreachStatement.Body, foreachScope);
                break;

            case SwitchStatement switchStatement:
                ResolveExpression(switchStatement.Expression, scope);
                foreach (var switchCase in switchStatement.Cases)
                {
                    ResolveExpression(switchCase.Pattern, scope);
                    ResolveStatements(switchCase.Body, scope.CreateChild());
                }

                ResolveStatements(switchStatement.DefaultBody, scope.CreateChild());
                break;

            case MatchStatement matchStatement:
                ResolveExpression(matchStatement.Expression, scope);
                foreach (var arm in matchStatement.Arms)
                {
                    var armScope = scope.CreateChild();
                    if (arm.BindingName is not null)
                    {
                        Declare(armScope, arm.BindingName, SymbolKind.MatchBinding, null, arm.Location, arm);
                    }

                    ResolveStatements(arm.Body, armScope);
                }

                break;
        }
    }

    private void ResolveForInitializer(ForInitializerNode initializer, Scope scope)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                ResolveExpression(declaration.Initializer, scope);
                Declare(scope, declaration.Name, SymbolKind.Local, TypeText(declaration.TypeNode), declaration.Location, declaration);
                break;
            case ForExpressionInitializerNode expression:
                ResolveExpression(expression.Expression, scope);
                break;
        }
    }

    private void DeclareForeachBinding(Scope scope, ForeachBinding? binding)
    {
        if (binding is null)
        {
            return;
        }

        Declare(scope, binding.Name, SymbolKind.ForeachBinding, TypeTextOrNull(binding.TypeNode), binding.Location, binding);
    }

    private void ResolveExpression(ExpressionNode? expression, Scope scope)
    {
        switch (expression)
        {
            case null:
                return;
            case NameExpressionNode name:
                if (scope.TryResolve(name.SourceText, out var symbol))
                {
                    name.Semantic.Symbol = symbol;
                }

                break;
            case ParenthesizedExpressionNode parenthesized:
                ResolveExpression(parenthesized.Expression, scope);
                break;
            case CastExpressionNode cast:
                ResolveExpression(cast.Expression, scope);
                break;
            case UnaryExpressionNode unary:
                ResolveExpression(unary.Operand, scope);
                break;
            case PostfixExpressionNode postfix:
                ResolveExpression(postfix.Operand, scope);
                break;
            case SizeOfExpressionNode sizeOf:
                ResolveExpression(sizeOf.ExpressionOperand, scope);
                break;
            case BinaryExpressionNode binary:
                ResolveExpression(binary.Left, scope);
                ResolveExpression(binary.Right, scope);
                break;
            case ConditionalExpressionNode conditional:
                ResolveExpression(conditional.Condition, scope);
                ResolveExpression(conditional.WhenTrue, scope);
                ResolveExpression(conditional.WhenFalse, scope);
                break;
            case ScalarRangeExpressionNode range:
                ResolveExpression(range.Start, scope);
                ResolveExpression(range.End, scope);
                break;
            case InitializerExpressionNode initializer:
                foreach (var field in initializer.Fields)
                {
                    ResolveExpression(field.Value, scope);
                }

                foreach (var value in initializer.Values)
                {
                    ResolveExpression(value, scope);
                }

                break;
            case FunctionExpressionNode function:
                var functionScope = scope.CreateChild();
                foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
                {
                    Declare(functionScope, parameter.Name, SymbolKind.Parameter, TypeText(parameter.TypeNode), parameter.Location, parameter);
                }

                ResolveExpression(function.ExpressionBody, functionScope);
                if (function.BlockBody is not null)
                {
                    ResolveStatements(function.BlockBody, functionScope);
                }

                break;
            case AssignmentExpressionNode assignment:
                ResolveExpression(assignment.Target, scope);
                ResolveExpression(assignment.Value, scope);
                break;
            case CallExpressionNode call:
                ResolveExpression(call.Callee, scope);
                foreach (var argument in call.Arguments)
                {
                    ResolveExpression(argument, scope);
                }

                BindCall(call, scope);
                break;
            case GenericCallExpressionNode call:
                ResolveExpression(call.Callee, scope);
                foreach (var argument in call.Arguments)
                {
                    ResolveExpression(argument, scope);
                }

                BindGenericCall(call, scope);
                break;
            case MemberExpressionNode member:
                ResolveExpression(member.Target, scope);
                BindMemberReference(member, scope);
                break;
            case IndexExpressionNode index:
                ResolveExpression(index.Target, scope);
                ResolveExpression(index.Index, scope);
                break;
        }
    }

    private Symbol? Declare(Scope scope, string name, SymbolKind kind, string? type, Location location, SyntaxNode? node = null)
    {
        var symbol = new Symbol(name, kind, type, location, node);
        if (scope.TryDeclare(symbol))
        {
            if (node is not null)
            {
                node.Semantic.Symbol = symbol;
            }

            return symbol;
        }

        diagnostics.Report(location, $"Duplicate {Describe(kind)} '{name}' in the same scope.");
        return null;
    }

    private void DeclareTopLevel(string name, SymbolKind kind, string? type, Location location, SyntaxNode node)
    {
        var symbol = new Symbol(name, kind, type, location, node);
        if (model.RootScope.TryDeclare(symbol))
        {
            node.Semantic.Symbol = symbol;
        }
    }

    private void BindCall(CallExpressionNode call, Scope scope)
    {
        if (TryBindResolvedCall(call, call.Callee, [], call.Arguments, scope))
        {
            return;
        }

        if (call.Callee is NameExpressionNode name
            && scope.TryResolve(name.SourceText, out var symbol)
            && symbol.Kind == SymbolKind.Function)
        {
            call.Semantic.Symbol = symbol;
            name.Semantic.Symbol = symbol;
            if (symbol.Node is FunctionNode function)
            {
                call.Semantic.ResolvedCall = new ResolvedCallInfo(function, [], IsInstance: false);
            }

            return;
        }

        if (call.Callee is MemberExpressionNode member
            && ResolveMemberFunction(member, scope, typeArguments: []) is { } resolved)
        {
            var functionSymbol = FunctionSymbol(resolved.Function);
            call.Semantic.Symbol = functionSymbol;
            call.Semantic.ResolvedCall = new ResolvedCallInfo(resolved.Function, resolved.TypeArguments, resolved.IsInstance);
            member.Semantic.Symbol = functionSymbol;
            member.Semantic.ResolvedCall = call.Semantic.ResolvedCall;
        }
    }

    private void BindGenericCall(GenericCallExpressionNode call, Scope scope)
    {
        var typeArguments = TypeArguments(call.TypeArgumentNodes);
        if (TryBindResolvedCall(call, call.Callee, typeArguments, call.Arguments, scope))
        {
            return;
        }

        if (call.Callee is NameExpressionNode name
            && FindFreeFunction(name.SourceText, typeArguments) is { } function)
        {
            var symbol = FunctionSymbol(function);
            call.Semantic.Symbol = symbol;
            call.Semantic.ResolvedCall = new ResolvedCallInfo(function, typeArguments, IsInstance: false);
            name.Semantic.Symbol = symbol;
            return;
        }

        if (call.Callee is MemberExpressionNode member
            && ResolveMemberFunction(member, scope, typeArguments) is { } resolved)
        {
            var functionSymbol = FunctionSymbol(resolved.Function);
            call.Semantic.Symbol = functionSymbol;
            call.Semantic.ResolvedCall = new ResolvedCallInfo(resolved.Function, resolved.TypeArguments, resolved.IsInstance);
            member.Semantic.Symbol = functionSymbol;
            member.Semantic.ResolvedCall = call.Semantic.ResolvedCall;
        }
    }

    private bool TryBindResolvedCall(
        ExpressionNode callExpression,
        ExpressionNode callee,
        IReadOnlyList<string> typeArguments,
        IReadOnlyList<ExpressionNode> arguments,
        Scope scope)
    {
        if (_program is null || _expressionTypeResolver is null)
        {
            return false;
        }

        var resolver = new CallResolver(_program, _expressionTypeResolver.Resolve);
        var variables = BuildVariableMap(scope);
        var resolved = resolver.Resolve(callee, typeArguments, arguments, variables);
        if (resolved?.Function is null)
        {
            return false;
        }

        var functionSymbol = FunctionSymbol(resolved.Function);
        callExpression.Semantic.Symbol = functionSymbol;
        callExpression.Semantic.ResolvedCall = new ResolvedCallInfo(
            resolved.Function,
            resolved.TypeArguments ?? [],
            resolved.IsInstance);

        switch (callee)
        {
            case NameExpressionNode name:
                name.Semantic.Symbol = functionSymbol;
                break;
            case MemberExpressionNode member:
                member.Semantic.Symbol = functionSymbol;
                member.Semantic.ResolvedCall = callExpression.Semantic.ResolvedCall;
                break;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildVariableMap(Scope scope)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var current = scope; current is not null; current = current.Parent)
        {
            foreach (var symbol in current.Symbols.Values)
            {
                if (symbol.Kind is SymbolKind.Function or SymbolKind.Type
                    || string.IsNullOrWhiteSpace(symbol.Type)
                    || variables.ContainsKey(symbol.Name))
                {
                    continue;
                }

                variables[symbol.Name] = symbol.Type;
            }
        }

        return variables;
    }

    private void BindMemberReference(MemberExpressionNode member, Scope scope)
    {
        if (ResolveMemberFunction(member, scope, typeArguments: []) is { } resolved)
        {
            var functionSymbol = FunctionSymbol(resolved.Function);
            member.Semantic.Symbol = functionSymbol;
            member.Semantic.ResolvedCall = new ResolvedCallInfo(resolved.Function, resolved.TypeArguments, resolved.IsInstance);
        }
    }

    private ResolvedFunction? ResolveMemberFunction(
        MemberExpressionNode member,
        Scope scope,
        IReadOnlyList<string> typeArguments)
    {
        var targetName = GetQualifiedName(member.Target);
        if (targetName is not null
            && FindModuleFunction(targetName, member.MemberName, typeArguments) is { } moduleFunction)
        {
            return new ResolvedFunction(moduleFunction, ResolveFunctionTypeArguments(moduleFunction, typeArguments), IsInstance: false);
        }

        if (targetName is not null
            && FindStaticFunction(targetName, member.MemberName, typeArguments) is { } staticFunction)
        {
            return new ResolvedFunction(staticFunction, ResolveFunctionTypeArguments(staticFunction, typeArguments), IsInstance: false);
        }

        if (member.Target is NameExpressionNode receiver
            && scope.TryResolve(receiver.SourceText, out var receiverSymbol)
            && !string.IsNullOrWhiteSpace(receiverSymbol.Type)
            && FindInstanceFunction(receiverSymbol.Type, member.MemberName, typeArguments) is { } instanceFunction)
        {
            return new ResolvedFunction(
                instanceFunction,
                ResolveFunctionTypeArguments(instanceFunction, typeArguments, receiverSymbol.Type),
                IsInstance: true);
        }

        return null;
    }

    private FunctionNode? FindFreeFunction(string name, IReadOnlyList<string> typeArguments) =>
        _functions.FirstOrDefault(function =>
            OwnerType(function) is null
            && string.Equals(function.Name, name, StringComparison.Ordinal)
            && MatchesTypeArguments(function, typeArguments));

    private FunctionNode? FindStaticFunction(string ownerType, string name, IReadOnlyList<string> typeArguments) =>
        _functions.FirstOrDefault(function =>
            function.IsStatic
            && OwnerType(function) is not null
            && string.Equals(OwnerType(function), ownerType, StringComparison.Ordinal)
            && string.Equals(function.Name, name, StringComparison.Ordinal)
            && MatchesTypeArguments(function, typeArguments));

    private FunctionNode? FindModuleFunction(string moduleName, string name, IReadOnlyList<string> typeArguments) =>
        _functions.FirstOrDefault(function =>
            OwnerType(function) is null
            && string.Equals(function.Semantic.ModuleName, moduleName, StringComparison.Ordinal)
            && string.Equals(function.Name, name, StringComparison.Ordinal)
            && MatchesTypeArguments(function, typeArguments));

    private FunctionNode? FindInstanceFunction(string receiverType, string name, IReadOnlyList<string> typeArguments)
    {
        var ownerType = GetGenericBaseName(StripPointer(receiverType));
        return _functions.FirstOrDefault(function =>
            !function.IsStatic
            && OwnerType(function) is not null
            && string.Equals(OwnerType(function), ownerType, StringComparison.Ordinal)
            && string.Equals(function.Name, name, StringComparison.Ordinal)
            && MatchesTypeArguments(function, typeArguments, receiverType));
    }

    private static bool MatchesTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> typeArguments,
        string? receiverType = null)
    {
        if (function.TypeParameters.Count == 0)
        {
            return typeArguments.Count == 0;
        }

        if (typeArguments.Count > 0)
        {
            return typeArguments.Count == function.TypeParameters.Count;
        }

        return receiverType is not null
            && TryParseGenericUse(StripPointer(receiverType), out _, out var receiverArguments)
            && receiverArguments.Count == function.TypeParameters.Count;
    }

    private static IReadOnlyList<string> ResolveFunctionTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> explicitTypeArguments,
        string? receiverType = null)
    {
        if (function.TypeParameters.Count == 0)
        {
            return [];
        }

        if (explicitTypeArguments.Count > 0)
        {
            return explicitTypeArguments;
        }

        return receiverType is not null
            && TryParseGenericUse(StripPointer(receiverType), out _, out var receiverArguments)
            && receiverArguments.Count == function.TypeParameters.Count
                ? receiverArguments
                : [];
    }

    private static Symbol FunctionSymbol(FunctionNode function)
    {
        if (function.Semantic.Symbol is { Kind: SymbolKind.Function } symbol)
        {
            return symbol;
        }

        symbol = new Symbol(function.Name, SymbolKind.Function, TypeText(function.ReturnTypeNode), function.Location, function);
        function.Semantic.Symbol = symbol;
        return symbol;
    }

    private static IReadOnlyList<FunctionNode> GetAllFunctions(ProgramNode program) =>
        program.Functions
            .Concat(program.Structs.SelectMany(structNode => structNode.Methods))
            .Concat(program.TaggedUnions.SelectMany(union => union.Methods))
            .Distinct()
            .ToList();

    private sealed record ResolvedFunction(
        FunctionNode Function,
        IReadOnlyList<string> TypeArguments,
        bool IsInstance);

    private static string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

    private static IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> nodes) =>
        nodes.Select(TypeText).ToList();

    private static string TypeText(TypeNode? typeNode) => typeNode?.TypeName ?? string.Empty;

    private static string? TypeTextOrNull(TypeNode? typeNode)
    {
        var type = TypeText(typeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
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

    private static string Describe(SymbolKind kind) =>
        kind switch
        {
            SymbolKind.Type => "type",
            SymbolKind.Function => "function",
            SymbolKind.Global => "global",
            SymbolKind.Parameter => "parameter",
            SymbolKind.Local => "local",
            SymbolKind.ForeachBinding => "foreach binding",
            SymbolKind.MatchBinding => "match binding",
            _ => "symbol"
        };
}
