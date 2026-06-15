using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal sealed class ScopeResolver(DiagnosticBag diagnostics, SemanticModel model)
{
    private IReadOnlyList<FunctionNode> _functions = [];
    private ProgramNode? _program;
    private ExpressionTypeResolver? _expressionTypeResolver;
    private TypeRefParser? _typeRefParser;

    public void Resolve(ProgramNode program)
    {
        _program = program;
        _functions = GetAllFunctions(program);
        _expressionTypeResolver = new ExpressionTypeResolver(program);
        _typeRefParser = new TypeRefParser(program);
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
            DeclareTopLevel(typeAlias.Name, SymbolKind.Type, typeAlias.TargetTypeNode, typeAlias.Location, typeAlias);
        }

        foreach (var requirement in program.Requirements)
        {
            DeclareTopLevel(requirement.Name, SymbolKind.Type, (string?)null, requirement.Location, requirement);
        }

        foreach (var enumNode in program.Enums)
        {
            DeclareTopLevel(CreateNamedTypeSymbol(enumNode.Name, enumNode.Location, enumNode));
        }

        foreach (var interfaceNode in program.Interfaces)
        {
            DeclareTopLevel(CreateNamedTypeSymbol(interfaceNode.Name, interfaceNode.Location, interfaceNode));
        }

        foreach (var structNode in program.Structs)
        {
            DeclareTopLevel(CreateNamedTypeSymbol(structNode.Name, structNode.Location, structNode));
        }

        foreach (var adapter in program.TypeAdapters)
        {
            DeclareTopLevel(CreateNamedTypeSymbol(adapter.Name, adapter.Location, adapter));
        }

        foreach (var union in program.TaggedUnions)
        {
            DeclareTopLevel(CreateNamedTypeSymbol(union.Name, union.Location, union));
        }

        foreach (var global in program.GlobalVariables)
        {
            DeclareTopLevel(global.Name, SymbolKind.Global, global.TypeNode, global.Location, global);
        }

        foreach (var externFunction in program.ExternFunctions)
        {
            DeclareTopLevel(externFunction.Name, SymbolKind.Function, externFunction.ReturnTypeNode, externFunction.Location, externFunction);
        }

        foreach (var function in _functions)
        {
            var symbol = CreateSymbol(function.Name, SymbolKind.Function, function.ReturnTypeNode, function.Location, function);
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
            Declare(functionScope, "self", SymbolKind.Parameter, TypeNode.CreateFromText(function.Location, ownerType + "*"), function.Location);
        }

        foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
        {
            Declare(functionScope, parameter.Name, SymbolKind.Parameter, parameter.TypeNode, parameter.Location, parameter);
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
                Declare(scope, let.Name, SymbolKind.Local, let.TypeNode, let.Location, let);
                break;

            case ReturnStatement { Expression: not null } ret:
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
                ResolveOptionalForInitializer(forStatement.CachedRangeEndInitializer, forScope);
                ResolveOptionalForInitializer(forStatement.CounterInitializer, forScope);
                ResolveForInitializer(forStatement.Initializer, forScope);
                ResolveExpression(forStatement.Condition, forScope);
                ResolveExpression(forStatement.Increment, forScope);
                ResolveExpression(forStatement.CounterIncrement, forScope);
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
                        Declare(armScope, arm.BindingName, SymbolKind.MatchBinding, (string?)null, arm.Location, arm);
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
                Declare(scope, declaration.Name, SymbolKind.Local, declaration.TypeNode, declaration.Location, declaration);
                break;
            case ForExpressionInitializerNode expression:
                ResolveExpression(expression.Expression, scope);
                break;
        }
    }

    private void ResolveOptionalForInitializer(ForInitializerNode? initializer, Scope scope)
    {
        if (initializer is not null)
        {
            ResolveForInitializer(initializer, scope);
        }
    }

    private void DeclareForeachBinding(Scope scope, ForeachBinding? binding)
    {
        if (binding is null)
        {
            return;
        }

        Declare(scope, binding.Name, SymbolKind.ForeachBinding, binding.TypeNode, binding.Location, binding);
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
                    Declare(functionScope, parameter.Name, SymbolKind.Parameter, parameter.TypeNode, parameter.Location, parameter);
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

    private Symbol? Declare(Scope scope, string name, SymbolKind kind, TypeNode? typeNode, Location location, SyntaxNode? node = null) =>
        Declare(scope, CreateSymbol(name, kind, typeNode, location, node), location);

    private Symbol? Declare(Scope scope, string name, SymbolKind kind, string? type, Location location, SyntaxNode? node = null) =>
        Declare(scope, CreateSymbol(name, kind, type, location, node), location);

    private Symbol? Declare(Scope scope, Symbol symbol, Location location)
    {
        if (scope.TryDeclare(symbol))
        {
            if (symbol.Node is not null)
            {
                symbol.Node.Semantic.Symbol = symbol;
            }

            return symbol;
        }

        diagnostics.Report(location, $"Duplicate {Describe(symbol.Kind)} '{symbol.Name}' in the same scope.");
        return null;
    }

    private void DeclareTopLevel(string name, SymbolKind kind, TypeNode? typeNode, Location location, SyntaxNode node) =>
        DeclareTopLevel(CreateSymbol(name, kind, typeNode, location, node));

    private void DeclareTopLevel(string name, SymbolKind kind, string? type, Location location, SyntaxNode node) =>
        DeclareTopLevel(CreateSymbol(name, kind, type, location, node));

    private void DeclareTopLevel(Symbol symbol)
    {
        if (model.RootScope.TryDeclare(symbol))
        {
            symbol.Node!.Semantic.Symbol = symbol;
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
        var variables = BuildTypeEnvironment(scope);
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

    private TypeEnvironment BuildTypeEnvironment(Scope scope)
    {
        var environment = new TypeEnvironment();
        for (var current = scope; current is not null; current = current.Parent)
        {
            foreach (var symbol in current.Symbols.Values)
            {
                if (symbol.Kind is SymbolKind.Function or SymbolKind.Type
                    || environment.Types.ContainsKey(symbol.Name))
                {
                    continue;
                }

                if (symbol.TypeRef is not null)
                {
                    environment.Set(symbol.Name, symbol.TypeRef);
                }
                else if (!string.IsNullOrWhiteSpace(symbol.Type))
                {
                    var typeRef = ParseTypeRefOrNull(symbol.Type);
                    if (typeRef is not null)
                    {
                        environment.Set(symbol.Name, typeRef);
                    }
                }
            }
        }

        return environment;
    }

    private Symbol CreateSymbol(string name, SymbolKind kind, TypeNode? typeNode, Location location, SyntaxNode? node)
    {
        var typeRef = TypeRefOrNull(typeNode);
        return Symbol.FromLegacyType(
            name,
            kind,
            TypeTextOrNull(typeNode),
            typeRef,
            location,
            node);
    }

    private Symbol CreateSymbol(string name, SymbolKind kind, string? type, Location location, SyntaxNode? node)
    {
        var typeRef = ParseTypeRefOrNull(type);
        return Symbol.FromLegacyType(
            name,
            kind,
            type,
            typeRef,
            location,
            node);
    }

    private static Symbol CreateNamedTypeSymbol(string name, Location location, SyntaxNode node) =>
        Symbol.FromTypeRef(
            name,
            SymbolKind.Type,
            new TypeRef.Named(name, []),
            location,
            node);

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
        var targetName = ExpressionNameFacts.GetQualifiedName(member.Target);
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
            && FindInstanceFunction(receiverSymbol, member.MemberName, typeArguments) is { } instanceFunction)
        {
            var receiverTypeArguments = ResolveFunctionTypeArguments(instanceFunction, typeArguments, receiverSymbol);
            return new ResolvedFunction(
                instanceFunction,
                receiverTypeArguments,
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
        var receiverTypeRef = ParseTypeRefOrNull(receiverType);
        if (receiverTypeRef is null)
        {
            return null;
        }

        var ownerType = TypeRefFacts.GetBaseName(receiverTypeRef);
        return _functions.FirstOrDefault(function =>
            !function.IsStatic
            && OwnerType(function) is not null
            && string.Equals(OwnerType(function), ownerType, StringComparison.Ordinal)
            && string.Equals(function.Name, name, StringComparison.Ordinal)
            && MatchesTypeArguments(function, typeArguments, receiverTypeRef));
    }

    private FunctionNode? FindInstanceFunction(Symbol receiverSymbol, string name, IReadOnlyList<string> typeArguments)
    {
        if (receiverSymbol.TypeRef is not null)
        {
            var ownerType = TypeRefFacts.GetBaseName(receiverSymbol.TypeRef);
            if (!string.IsNullOrWhiteSpace(ownerType))
            {
                return _functions.FirstOrDefault(function =>
                    !function.IsStatic
                    && OwnerType(function) is not null
                    && string.Equals(OwnerType(function), ownerType, StringComparison.Ordinal)
                    && string.Equals(function.Name, name, StringComparison.Ordinal)
                    && MatchesTypeArguments(function, typeArguments, receiverSymbol.TypeRef));
            }
        }

        var receiverType = receiverSymbol.TypeText;
        return string.IsNullOrWhiteSpace(receiverType)
            ? null
            : FindInstanceFunction(receiverType, name, typeArguments);
    }

    private static bool MatchesTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> typeArguments)
    {
        if (function.TypeParameters.Count == 0)
        {
            return typeArguments.Count == 0;
        }

        if (typeArguments.Count > 0)
        {
            return typeArguments.Count == function.TypeParameters.Count;
        }

        return false;
    }

    private static bool MatchesTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> typeArguments,
        TypeRef? receiverType)
    {
        if (function.TypeParameters.Count == 0)
        {
            return typeArguments.Count == 0;
        }

        if (typeArguments.Count > 0)
        {
            return typeArguments.Count == function.TypeParameters.Count;
        }

        return TypeRefFacts.TryGetGenericArguments(receiverType, out var receiverArguments)
            && receiverArguments.Count == function.TypeParameters.Count;
    }

    private static IReadOnlyList<string> ResolveFunctionTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> explicitTypeArguments)
    {
        if (function.TypeParameters.Count == 0)
        {
            return [];
        }

        if (explicitTypeArguments.Count > 0)
        {
            return explicitTypeArguments;
        }

        return [];
    }

    private IReadOnlyList<string> ResolveFunctionTypeArguments(
        FunctionNode function,
        IReadOnlyList<string> explicitTypeArguments,
        Symbol receiverSymbol)
    {
        if (function.TypeParameters.Count == 0)
        {
            return [];
        }

        if (explicitTypeArguments.Count > 0)
        {
            return explicitTypeArguments;
        }

        if (receiverSymbol.TypeRef is not null
            && TypeRefFacts.TryGetGenericArguments(receiverSymbol.TypeRef, out var receiverArguments)
            && receiverArguments.Count == function.TypeParameters.Count)
        {
            return receiverArguments.Select(TypeRefFormatter.ToCxString).ToList();
        }

        var receiverType = ParseTypeRefOrNull(receiverSymbol.TypeText);
        return TypeRefFacts.TryGetGenericArguments(receiverType, out var fallbackArguments)
            && fallbackArguments.Count == function.TypeParameters.Count
                ? fallbackArguments.Select(TypeRefFormatter.ToCxString).ToList()
                : [];
    }

    private Symbol FunctionSymbol(FunctionNode function)
    {
        if (function.Semantic.Symbol is { Kind: SymbolKind.Function } symbol)
        {
            return symbol;
        }

        symbol = CreateSymbol(function.Name, SymbolKind.Function, function.ReturnTypeNode, function.Location, function);
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

    private string? OwnerType(FunctionNode function) => TypeTextOrNull(function.OwnerTypeNode);

    private IReadOnlyList<string> TypeArguments(IReadOnlyList<TypeNode> nodes) =>
        nodes.Select(TypeText).ToList();

    private TypeRef? TypeRefOrNull(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return null;
        }

        if (_typeRefParser is null)
        {
            throw new InvalidOperationException("Scope resolver has no TypeRef parser.");
        }

        var type = typeNode.ToTypeRef(_typeRefParser);
        return type is TypeRef.Unknown ? null : type;
    }

    private TypeRef? ParseTypeRefOrNull(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (_typeRefParser is null)
        {
            throw new InvalidOperationException("Scope resolver has no TypeRef parser.");
        }

        var parsed = _typeRefParser.Parse(type);
        return parsed is TypeRef.Unknown ? null : parsed;
    }

    private string TypeText(TypeNode? typeNode)
    {
        var type = TypeRefOrNull(typeNode);
        return type is null ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private string? TypeTextOrNull(TypeNode? typeNode)
    {
        var type = TypeText(typeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
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
