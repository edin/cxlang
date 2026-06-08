using System.Text.RegularExpressions;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal static class GenericStructSpecializer
{
    public static IReadOnlyList<StructNode> Specialize(
        ProgramNode program,
        IEnumerable<FunctionNode> specializedFunctions)
    {
        var genericDefinitions = program.Structs
            .Where(structNode => !structNode.IsHeaderDeclaration)
            .Where(structNode => structNode.TypeParameters.Count > 0)
            .ToDictionary(structNode => structNode.Name, StringComparer.Ordinal);
        var openTypeParameterNames = GetOpenTypeParameterNames(program);
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

            foreach (var use in GenericTypeRewriter.FindGenericStructUses(type))
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
            var concreteName = GenericTypeRewriter.LowerGenericTypeName(use.Name, use.Arguments);
            if (ContainsOpenTypeParameter(use.Arguments, openTypeParameterNames)
                || !emitted.Add(concreteName)
                || !genericDefinitions.TryGetValue(use.Name, out var definition)
                || definition.TypeParameters.Count != use.Arguments.Count)
            {
                continue;
            }

            var specialized = SpecializeDefinition(definition, concreteName, use.Arguments, CollectFromType);
            result.Add(specialized);
        }

        return result;
    }

    private static StructNode SpecializeDefinition(
        StructNode definition,
        string concreteName,
        IReadOnlyList<string> arguments,
        Action<string?> collectFromType)
    {
        var substitutions = definition.TypeParameters
            .Zip(arguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var typeSubstitutions = GenericTypeSubstitutionBuilder.Build(substitutions);
        var fields = definition.Fields
            .Select(field =>
            {
                var fieldType = GenericTypeStringRewriter.Substitute(field.Type, substitutions);
                collectFromType(fieldType);
                return CopySemantic(field, field with
                {
                    TypeNode = SubstituteTypeNode(field.TypeNode, substitutions, typeSubstitutions),
                });
            })
            .ToList();
        var requirements = definition.Requirements
            .Select(requirement => CopySemantic(requirement, requirement with
            {
                TypeArgumentNodes = requirement.TypeArgumentNodes
                    .Select(typeNode => SubstituteTypeNode(typeNode, substitutions, typeSubstitutions)!)
                    .ToList(),
            }))
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
        return specialized;
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

    private static TypeNode? SubstituteTypeNode(
        TypeNode? typeNode,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, TypeRef> typeSubstitutions) =>
        TypeNodeRewriter.Rewrite(
            typeNode,
            typeName => GenericTypeStringRewriter.Substitute(typeName, substitutions),
            typeSubstitutions);

    private static IReadOnlySet<string> GetOpenTypeParameterNames(ProgramNode program) =>
        program.Structs.SelectMany(structNode => structNode.TypeParameters)
            .Concat(program.Functions.SelectMany(function => function.TypeParameters))
            .Concat(program.TypeAdapters.SelectMany(adapter => adapter.TypeParameters))
            .Concat(program.Extensions.SelectMany(extension => extension.TypeParameters))
            .Concat(program.Requirements.SelectMany(requirement => requirement.TypeParameters))
            .Concat(program.ExternFunctions.SelectMany(function => function.TypeParameters))
            .ToHashSet(StringComparer.Ordinal);

    private static bool ContainsOpenTypeParameter(
        IReadOnlyList<string> typeArguments,
        IReadOnlySet<string> openTypeParameterNames) =>
        typeArguments.Any(argument => openTypeParameterNames.Any(parameter =>
            Regex.IsMatch(argument, $@"\b{Regex.Escape(parameter)}\b")));

    private static T CopySemantic<T>(SyntaxNode source, T target)
        where T : SyntaxNode
        => SyntaxNode.CloneSemantic(source, target);
}
