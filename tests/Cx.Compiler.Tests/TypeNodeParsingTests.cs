using Cx.Compiler.Syntax.Nodes;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;

namespace Cx.Compiler.Tests;

public sealed class TypeNodeParsingTests
{
    [Fact]
    public void ParseType_AllowsAdjacentNestedGenericClosers()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn use_box(value: Box<Box<int>>*) -> Box<int> {
                return value.value;
            }
            """);

        var function = Assert.Single(program.Functions);
        Assert.Equal("Box<Box<int>>*", Assert.Single(function.Parameters).Type);
        Assert.Equal("Box<int>", function.ReturnType);
    }

    [Fact]
    public void ParseType_StoresTypeNodeBesideCompatibilityString()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn main(value: Box<int>) -> Box<int> {
                let local: Box<int> = value;
                return local;
            }
            """);

        var box = Assert.Single(program.Structs);
        var field = Assert.Single(box.Fields);
        var function = Assert.Single(program.Functions);
        var parameter = Assert.Single(function.Parameters);
        var local = Assert.IsType<LetStatement>(function.Body[0]);

        Assert.Equal(field.Type, field.TypeNode?.TypeName);
        Assert.Equal(parameter.Type, parameter.TypeNode?.TypeName);
        Assert.Equal(function.ReturnType, function.ReturnTypeNode?.TypeName);
        Assert.Equal(local.Type, local.TypeNode?.TypeName);
    }

    [Fact]
    public void ResolveType_StoresResolvedTypeRefOnTypeNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type IntBox = Box<int>;

            struct Box<T> {
                value: T;
            }

            fn main(value: IntBox) -> IntBox {
                let local: IntBox = value;
                return local;
            }
            """);

        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var alias = Assert.Single(program.TypeAliases);
        var box = Assert.Single(program.Structs);
        var field = Assert.Single(box.Fields);
        var function = Assert.Single(program.Functions);
        var parameter = Assert.Single(function.Parameters);
        var local = Assert.IsType<LetStatement>(function.Body[0]);

        Assert.Equal(alias.Semantic.Type, alias.TargetTypeNode?.Semantic.Type);
        Assert.Equal(field.Semantic.Type, field.TypeNode?.Semantic.Type);
        Assert.Equal(function.Semantic.Type, function.ReturnTypeNode?.Semantic.Type);
        Assert.Equal(parameter.Semantic.Type, parameter.TypeNode?.Semantic.Type);
        Assert.Equal(local.Semantic.Type, local.TypeNode?.Semantic.Type);

        var resolvedReturnAlias = Assert.IsType<TypeRef.Alias>(function.ReturnTypeNode?.Semantic.Type);
        Assert.Equal("IntBox", resolvedReturnAlias.Name);
        var resolvedReturn = Assert.IsType<TypeRef.Named>(resolvedReturnAlias.Target);
        Assert.Equal("Box", resolvedReturn.Name);
        Assert.Equal("int", Assert.IsType<TypeRef.Named>(Assert.Single(resolvedReturn.Arguments)).Name);
    }

    [Fact]
    public void ParseType_StoresTypeNodesOnRequirementInterfaceAndUnionDeclarations()
    {
        var program = CompilerTestHelpers.Parse(
            """
            requires Contiguous<T> {
                data: T*;
                fn count(self: Self*) -> usize;
            }

            interface Allocator {
                fn allocate(size: usize, align: usize) -> void*;
            }

            struct Vec<T>: Contiguous<T> {
                data: T*;
                length: usize;
            }

            union Value {
                Number: int;
                Vector: Vec<int>;
            }
            """);

        var requirement = Assert.Single(program.Requirements);
        var field = Assert.IsType<RequirementFieldNode>(requirement.Members[0]);
        var requirementFunction = Assert.IsType<RequirementFunctionNode>(requirement.Members[1]);
        var interfaceMethod = Assert.Single(Assert.Single(program.Interfaces).Methods);
        var structRequirement = Assert.Single(Assert.Single(program.Structs).Requirements);
        var unionVariant = program.TaggedUnions.Single().Variants[1];

        Assert.Equal(field.Type, field.TypeNode?.TypeName);
        Assert.Equal(requirementFunction.ReturnType, requirementFunction.ReturnTypeNode?.TypeName);
        Assert.Equal(interfaceMethod.ReturnType, interfaceMethod.ReturnTypeNode?.TypeName);
        Assert.Equal(structRequirement.TypeArguments, structRequirement.TypeArgumentNodes.Select(node => node.TypeName).ToList());
        Assert.Equal(unionVariant.Type, unionVariant.TypeNode?.TypeName);
    }

    [Fact]
    public void ResolveType_StoresResolvedTypeRefsOnRequirementInterfaceAndUnionTypeNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            requires Contiguous<T> {
                data: T*;
                fn count(self: Self*) -> usize;
            }

            interface Allocator {
                fn allocate(size: usize, align: usize) -> void*;
            }

            struct Vec<T>: Contiguous<Vec<int>> {
                data: T*;
            }

            union Value {
                Vector: Vec<int>;
            }
            """);

        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var requirement = Assert.Single(program.Requirements);
        var field = Assert.IsType<RequirementFieldNode>(requirement.Members[0]);
        var requirementFunction = Assert.IsType<RequirementFunctionNode>(requirement.Members[1]);
        var interfaceMethod = Assert.Single(Assert.Single(program.Interfaces).Methods);
        var structRequirementArgument = Assert.Single(Assert.Single(program.Structs).Requirements.Single().TypeArgumentNodes);
        var unionVariant = Assert.Single(program.TaggedUnions.Single().Variants);

        Assert.Equal(field.Semantic.Type, field.TypeNode?.Semantic.Type);
        Assert.Equal(requirementFunction.Semantic.Type, requirementFunction.ReturnTypeNode?.Semantic.Type);
        Assert.Equal(interfaceMethod.Semantic.Type, interfaceMethod.ReturnTypeNode?.Semantic.Type);
        Assert.IsType<TypeRef.Named>(structRequirementArgument.Semantic.Type);
        Assert.Equal(unionVariant.Semantic.Type, unionVariant.TypeNode?.Semantic.Type);
    }

    [Fact]
    public void ParseType_StoresTypeNodesOnExpressionTypeSyntax()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main(value: void*) -> int {
                let casted: Box<int>* = (Box<int>*)value;
                let bytes: usize = sizeof(Box<int>);
                let box: Box<int> = Box<int> { value: 10 };
                let same = identity<Box<int>>(box);
                let map = fn(value: Box<int>) -> Box<int> => value;
                return 0;
            }
            """);

        var body = program.Functions.Single(function => function.Name == "main").Body;
        var cast = Assert.IsType<CastExpressionNode>(Assert.IsType<LetStatement>(body[0]).Initializer);
        var sizeOf = Assert.IsType<SizeOfExpressionNode>(Assert.IsType<LetStatement>(body[1]).Initializer);
        var initializer = Assert.IsType<InitializerExpressionNode>(Assert.IsType<LetStatement>(body[2]).Initializer);
        var genericCall = Assert.IsType<GenericCallExpressionNode>(Assert.IsType<LetStatement>(body[3]).Initializer);
        var functionExpression = Assert.IsType<FunctionExpressionNode>(Assert.IsType<LetStatement>(body[4]).Initializer);

        Assert.Equal(cast.TargetType, cast.TargetTypeNode?.TypeName);
        Assert.Equal(sizeOf.TypeOperand, sizeOf.TypeOperandNode?.TypeName);
        Assert.Equal(initializer.TypeName, initializer.TypeNameNode?.TypeName);
        Assert.Equal(genericCall.TypeArguments, genericCall.TypeArgumentNodes.Select(node => node.TypeName).ToList());
        Assert.Equal(functionExpression.ReturnType, functionExpression.ReturnTypeNode?.TypeName);
        Assert.Equal(Assert.Single(functionExpression.Parameters).Type, Assert.Single(functionExpression.Parameters).TypeNode?.TypeName);
    }

    [Fact]
    public void ResolveType_StoresResolvedTypeRefsOnExpressionTypeNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main(value: void*) -> int {
                let casted: Box<int>* = (Box<int>*)value;
                let bytes: usize = sizeof(Box<int>);
                let box: Box<int> = Box<int> { value: 10 };
                let same = identity<Box<int>>(box);
                let map = fn(value: Box<int>) -> Box<int> => value;
                return 0;
            }
            """);

        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var body = program.Functions.Single(function => function.Name == "main").Body;
        var cast = Assert.IsType<CastExpressionNode>(Assert.IsType<LetStatement>(body[0]).Initializer);
        var sizeOf = Assert.IsType<SizeOfExpressionNode>(Assert.IsType<LetStatement>(body[1]).Initializer);
        var initializer = Assert.IsType<InitializerExpressionNode>(Assert.IsType<LetStatement>(body[2]).Initializer);
        var genericCall = Assert.IsType<GenericCallExpressionNode>(Assert.IsType<LetStatement>(body[3]).Initializer);
        var functionExpression = Assert.IsType<FunctionExpressionNode>(Assert.IsType<LetStatement>(body[4]).Initializer);

        Assert.Equal(cast.Semantic.Type, cast.TargetTypeNode?.Semantic.Type);
        Assert.Equal(sizeOf.Semantic.Type, sizeOf.TypeOperandNode?.Semantic.Type);
        Assert.Equal(initializer.Semantic.Type, initializer.TypeNameNode?.Semantic.Type);
        Assert.IsType<TypeRef.Named>(Assert.Single(genericCall.TypeArgumentNodes).Semantic.Type);
        Assert.Equal(functionExpression.Semantic.Type, functionExpression.ReturnTypeNode?.Semantic.Type);
        Assert.Equal(Assert.Single(functionExpression.Parameters).Semantic.Type, Assert.Single(functionExpression.Parameters).TypeNode?.Semantic.Type);
    }
}
