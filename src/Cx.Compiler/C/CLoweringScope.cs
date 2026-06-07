using Cx.Compiler.Lowering;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.C;

internal sealed class CLoweringScope(
    IReadOnlySet<string> pointerParameters,
    IReadOnlyDictionary<string, string> variables,
    IReadOnlyDictionary<string, TypeRef> variableTypes,
    IReadOnlyDictionary<string, CLoweringScope.ImplicitReferenceLocal> implicitReferenceLocals)
{
    private IReadOnlySet<string> PointerParameters { get; } = pointerParameters;

    private IReadOnlyDictionary<string, string> Variables { get; } = variables;

    private IReadOnlyDictionary<string, TypeRef> VariableTypes { get; } = variableTypes;

    private IReadOnlyDictionary<string, ImplicitReferenceLocal> ImplicitReferenceLocals { get; } = implicitReferenceLocals;

    public static CLoweringScope Create(
        IReadOnlySet<string> pointerParameters,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, TypeRef> variableTypes) =>
        new(
            pointerParameters,
            variables,
            variableTypes,
            new Dictionary<string, ImplicitReferenceLocal>(StringComparer.Ordinal));

    public CLoweringScope ForFunction(FunctionNode function, string? selfType)
    {
        var variables = Variables.ToDictionary(StringComparer.Ordinal);
        var variableTypes = VariableTypes.ToDictionary(StringComparer.Ordinal);
        foreach (var variable in function.Parameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => (parameter.Name, Type: SubstituteSelfType(parameter.Type, selfType)))
            .Concat(CollectLocalVariables(function.Body)
                .Select(statement => (statement.Name, Type: SubstituteSelfType(statement.Type, selfType))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => (group.Key, Type: group.First().Type)))
        {
            variables[variable.Key] = variable.Type;
        }

        foreach (var variable in function.Parameters
            .Where(parameter => !parameter.IsVariadic && parameter.TypeNode?.Semantic.Type is not null)
            .Select(parameter => (parameter.Name, Type: SubstituteSelf(parameter.TypeNode!.Semantic.Type!, selfType)))
            .Concat(CollectLocalVariableTypes(function.Body)
                .Where(local => local.TypeRef is not null)
                .Select(local => (local.Name, Type: SubstituteSelf(local.TypeRef!, selfType))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => (group.Key, Type: group.First().Type)))
        {
            variableTypes[variable.Key] = variable.Type;
        }

        var pointerVariables = variables
            .Where(item => item.Value.EndsWith("*", StringComparison.Ordinal))
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new(pointerVariables, variables, variableTypes, ImplicitReferenceLocals);
    }

    public CLoweringScope WithLocal(string name, string type)
    {
        var variables = Variables.ToDictionary(StringComparer.Ordinal);
        variables[name] = type;

        var variableTypes = VariableTypes.ToDictionary(StringComparer.Ordinal);
        if (GenericTypeSubstitutionBuilder.ParseType(type) is { } typeRef)
        {
            variableTypes[name] = typeRef;
        }

        var pointerParameters = PointerParameters.ToHashSet(StringComparer.Ordinal);
        if (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerParameters.Add(name);
        }

        return new(pointerParameters, variables, variableTypes, ImplicitReferenceLocals);
    }

    public CLoweringScope WithImplicitReferenceLocal(
        string name,
        string valueType,
        string storageType,
        bool isConst)
    {
        var variables = Variables.ToDictionary(StringComparer.Ordinal);
        variables[name] = valueType;

        var variableTypes = VariableTypes.ToDictionary(StringComparer.Ordinal);
        if (GenericTypeSubstitutionBuilder.ParseType(valueType) is { } valueTypeRef)
        {
            variableTypes[name] = valueTypeRef;
        }

        var implicitReferenceLocals = ImplicitReferenceLocals.ToDictionary(StringComparer.Ordinal);
        implicitReferenceLocals[name] = new ImplicitReferenceLocal(valueType, storageType, isConst);

        return new(PointerParameters, variables, variableTypes, implicitReferenceLocals);
    }

    public bool TryGetVariableType(string name, out string type) =>
        Variables.TryGetValue(name, out type!);

    public string? GetVariableTypeOrDefault(string name) =>
        Variables.GetValueOrDefault(name);

    public IEnumerable<(string Name, string Type)> GetVariables() =>
        Variables.Select(variable => (variable.Key, variable.Value));

    public bool TryGetVariableTypeRef(string name, out TypeRef type)
    {
        if (VariableTypes.TryGetValue(name, out type!))
        {
            return true;
        }

        if (Variables.TryGetValue(name, out var textType)
            && GenericTypeSubstitutionBuilder.ParseType(textType) is { } parsed)
        {
            type = parsed;
            return true;
        }

        type = null!;
        return false;
    }

    public bool IsImplicitReferenceLocal(string name) =>
        ImplicitReferenceLocals.ContainsKey(name);

    public IEnumerable<string> GetPointerParametersByDescendingLength() =>
        PointerParameters.OrderByDescending(name => name.Length);

    private static IEnumerable<(string Name, string Type)> CollectLocalVariables(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return (let.Name, let.Type);
                    break;
                case IfStatement ifStatement:
                    foreach (var variable in CollectLocalVariables(ifStatement.ThenBody))
                    {
                        yield return variable;
                    }

                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var variable in CollectLocalVariables([ifStatement.ElseBranch]))
                        {
                            yield return variable;
                        }
                    }

                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var variable in CollectLocalVariables(elseBlock.Body))
                    {
                        yield return variable;
                    }
                    break;
                case WhileStatement whileStatement:
                    foreach (var variable in CollectLocalVariables(whileStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForStatement forStatement:
                    if (forStatement.Initializer is ForDeclarationInitializerNode declaration)
                    {
                        yield return (declaration.Name, declaration.Type);
                    }

                    foreach (var variable in CollectLocalVariables(forStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    foreach (var variable in CollectLocalVariables(foreachStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var variable in CollectLocalVariables(switchCase.Body))
                        {
                            yield return variable;
                        }
                    }

                    foreach (var variable in CollectLocalVariables(switchStatement.DefaultBody))
                    {
                        yield return variable;
                    }
                    break;
                case MatchStatement matchStatement:
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var variable in CollectLocalVariables(arm.Body))
                        {
                            yield return variable;
                        }
                    }
                    break;
            }
        }
    }

    private static IEnumerable<(string Name, string Type, TypeRef? TypeRef)> CollectLocalVariableTypes(IEnumerable<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    yield return (let.Name, let.Type, let.TypeNode?.Semantic.Type);
                    break;
                case IfStatement ifStatement:
                    foreach (var variable in CollectLocalVariableTypes(ifStatement.ThenBody))
                    {
                        yield return variable;
                    }

                    if (ifStatement.ElseBranch is not null)
                    {
                        foreach (var variable in CollectLocalVariableTypes([ifStatement.ElseBranch]))
                        {
                            yield return variable;
                        }
                    }

                    break;
                case ElseBlockStatement elseBlock:
                    foreach (var variable in CollectLocalVariableTypes(elseBlock.Body))
                    {
                        yield return variable;
                    }
                    break;
                case WhileStatement whileStatement:
                    foreach (var variable in CollectLocalVariableTypes(whileStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForStatement forStatement:
                    if (forStatement.Initializer is ForDeclarationInitializerNode declaration)
                    {
                        yield return (declaration.Name, declaration.Type, declaration.TypeNode?.Semantic.Type);
                    }

                    foreach (var variable in CollectLocalVariableTypes(forStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case ForeachStatement foreachStatement:
                    if (foreachStatement.IndexBinding is not null)
                    {
                        yield return (foreachStatement.IndexBinding.Name, foreachStatement.IndexBinding.Type, foreachStatement.IndexBinding.TypeNode?.Semantic.Type);
                    }
                    if (foreachStatement.KeyBinding is not null)
                    {
                        yield return (foreachStatement.KeyBinding.Name, foreachStatement.KeyBinding.Type, foreachStatement.KeyBinding.TypeNode?.Semantic.Type);
                    }
                    yield return (foreachStatement.ValueBinding.Name, foreachStatement.ValueBinding.Type, foreachStatement.ValueBinding.TypeNode?.Semantic.Type);
                    foreach (var variable in CollectLocalVariableTypes(foreachStatement.Body))
                    {
                        yield return variable;
                    }
                    break;
                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var variable in CollectLocalVariableTypes(switchCase.Body))
                        {
                            yield return variable;
                        }
                    }

                    foreach (var variable in CollectLocalVariableTypes(switchStatement.DefaultBody))
                    {
                        yield return variable;
                    }
                    break;
                case MatchStatement matchStatement:
                    foreach (var arm in matchStatement.Arms)
                    {
                        foreach (var variable in CollectLocalVariableTypes(arm.Body))
                        {
                            yield return variable;
                        }
                    }
                    break;
            }
        }
    }

    private static string SubstituteSelfType(string type, string? selfType) =>
        GenericTypeStringRewriter.SubstituteSelf(type, selfType);

    private static TypeRef SubstituteSelf(TypeRef type, string? selfType) =>
        GenericTypeSubstitutionBuilder.ParseType(selfType) is { } selfTypeRef
            ? TypeRefRewriter.SubstituteSelf(type, selfTypeRef)
            : type;

    public sealed record ImplicitReferenceLocal(
        string ValueType,
        string StorageType,
        bool IsConst);
}
