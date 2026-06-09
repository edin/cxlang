using Cx.Compiler.Syntax.Nodes;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cx.Compiler.Tests;

public sealed class TypeNodeParsingTests
{
    [Fact]
    public void ParseType_AllRealTypeNodesHaveSyntax()
    {
        var program = CompilerTestHelpers.Parse(
            """
            module app.main;

            type Callback = fn(int, char*, ...) -> double;
            type IntVec = Vec<int>;

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

                static fn create(capacity: usize) -> Vec<T> {
                    return Vec<T> { data: null, length: 0 };
                }
            }

            extension Vec<T> {
                fn set(index: usize, value: T) -> void {
                    self.data[index] = value;
                }
            }

            union Value {
                Number: int;
                Vector: Vec<int>;
            }

            let global: IntVec = Vec<int> { data: null, length: 0 };

            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main(value: void*) -> int {
                let casted: Vec<int>* = (Vec<int>*)value;
                let bytes: usize = sizeof(Vec<int>);
                let box: Vec<int> = Vec<int> { data: null, length: 0 };
                let same = identity<Vec<int>>(box);
                let map = fn(item: Vec<int>) -> Vec<int> => item;
                for (let i: int = 0; i < 1; i = i + 1) {
                    let local: Callback = null;
                }
                return 0;
            }
            """);

        var missingSyntax = CollectTypeNodes(program)
            .Where(typeNode => !string.IsNullOrWhiteSpace(typeNode.TypeName) && typeNode.Syntax is null)
            .Select(typeNode => typeNode.TypeName)
            .ToList();

        Assert.Empty(missingSyntax);
    }

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
        Assert.Equal("Box<Box<int>>*", Assert.Single(function.Parameters).TypeNode.ToTypeName());
        Assert.Equal("Box<int>", function.ReturnTypeNode.ToTypeName());
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

        Assert.Equal("T", field.TypeNode?.TypeName);
        Assert.Equal("Box<int>", parameter.TypeNode?.TypeName);
        Assert.Equal("Box<int>", function.ReturnTypeNode?.TypeName);
        Assert.Equal("Box<int>", local.TypeNode?.TypeName);
    }

    [Fact]
    public void ParseType_StoresPassiveTypeSyntaxTree()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type Callback = fn(int, char*, ...) -> double;

            struct Box<T> {
                values: T*[4];
            }

            fn main(value: Box<int>*) -> int {
                return 0;
            }
            """);

        var alias = Assert.Single(program.TypeAliases);
        var functionSyntax = Assert.IsType<FunctionTypeSyntaxNode>(alias.TargetTypeNode?.Syntax);
        Assert.True(functionSyntax.IsVariadic);
        Assert.Equal(2, functionSyntax.Parameters.Count);
        Assert.IsType<PointerTypeSyntaxNode>(functionSyntax.Parameters[1]);
        Assert.IsType<NamedTypeSyntaxNode>(functionSyntax.ReturnType);

        var field = Assert.Single(Assert.Single(program.Structs).Fields);
        var arraySyntax = Assert.IsType<FixedArrayTypeSyntaxNode>(field.TypeNode?.Syntax);
        Assert.Equal("4", arraySyntax.Length);
        var pointerSyntax = Assert.IsType<PointerTypeSyntaxNode>(arraySyntax.Element);
        Assert.Equal("T", Assert.IsType<NamedTypeSyntaxNode>(pointerSyntax.Element).Name);

        var parameter = Assert.Single(Assert.Single(program.Functions).Parameters);
        var parameterPointer = Assert.IsType<PointerTypeSyntaxNode>(parameter.TypeNode?.Syntax);
        var genericSyntax = Assert.IsType<GenericTypeSyntaxNode>(parameterPointer.Element);
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntaxNode>(genericSyntax.Target).Name);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntaxNode>(Assert.Single(genericSyntax.Arguments)).Name);
    }

    [Fact]
    public void TypeSyntaxFormatter_RoundTripsCanonicalTypeText()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type Callback = fn(int, char*, ...) -> Vec<int>*[2];
            """);

        var syntax = Assert.Single(program.TypeAliases).TargetTypeNode?.Syntax;

        Assert.NotNull(syntax);
        Assert.Equal("fn(int,char*,...)->Vec<int>*[2]", TypeSyntaxFormatter.ToCxString(syntax!));
    }

    [Fact]
    public void TypeSyntaxTypeRefConverter_ConvertsSyntaxToTypeRef()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type IntVec = Vec<int>;
            type Callback = fn(IntVec*, ...) -> IntVec[4];

            struct Vec<T> {
                value: T;
            }
            """);
        var converter = new TypeSyntaxTypeRefConverter(program);

        var intVec = converter.Convert(program.TypeAliases[0].TargetTypeNode);
        var callback = converter.Convert(program.TypeAliases[1].TargetTypeNode);

        Assert.Equal("Vec<int>", TypeRefFormatter.ToCxString(intVec));
        var function = Assert.IsType<TypeRef.Function>(callback);
        var parameter = Assert.Single(function.Parameters);
        var parameterPointer = Assert.IsType<TypeRef.Pointer>(parameter);
        Assert.Equal("IntVec", Assert.IsType<TypeRef.Alias>(parameterPointer.Element).Name);
        var returnArray = Assert.IsType<TypeRef.FixedArray>(function.ReturnType);
        Assert.Equal("4", returnArray.Length);
        Assert.Equal("IntVec", Assert.IsType<TypeRef.Alias>(returnArray.Element).Name);
    }

    [Fact]
    public void ResolveType_PrefersTypeSyntaxOverCompatibilityText()
    {
        var location = new Location(new SourceFile("test.cx", string.Empty), Position: 0, Line: 1, Column: 1);
        var typeNode = new TypeNode(location, "NotARealType", new NamedTypeSyntaxNode("int"));
        var global = new GlobalVariableNode(
            location,
            IsConst: false,
            Name: "value",
            Initializer: null,
            Attributes: [],
            TypeNode: typeNode);
        var program = new ProgramNode(
            location,
            Module: null,
            Imports: [],
            SymbolImports: [],
            Includes: [],
            CDeclarations: [],
            ExternFunctions: [],
            AttributeDeclarations: [],
            TypeAliases: [],
            Requirements: [],
            Enums: [],
            Interfaces: [],
            Structs: [],
            TypeAdapters: [],
            Extensions: [],
            TaggedUnions: [],
            GlobalVariables: [global],
            Functions: []);
        var diagnostics = new DiagnosticBag();

        new TypeResolutionPass(diagnostics).Resolve(program);

        CompilerTestHelpers.AssertNoErrors(diagnostics);
        Assert.Equal("int", TypeRefFormatter.ToCxString(global.Semantic.Type!));
        Assert.Same(global.Semantic.Type, typeNode.Semantic.Type);
    }

    [Fact]
    public void ToTypeRef_ThrowsWhenTypeNodeHasNoSyntax()
    {
        var location = new Location(new SourceFile("test.cx", string.Empty), Position: 0, Line: 1, Column: 1);
        var typeNode = new TypeNode(location, "MissingSyntax");
        var parser = new TypeRefParser(new ProgramNode(location, []));

        var exception = Assert.Throws<InvalidOperationException>(() => typeNode.ToTypeRef(parser));

        Assert.Contains("has no parsed type syntax", exception.Message);
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

        Assert.Equal("T*", field.TypeNode?.TypeName);
        Assert.Equal("usize", requirementFunction.ReturnTypeNode?.TypeName);
        Assert.Equal("void*", interfaceMethod.ReturnTypeNode?.TypeName);
        Assert.Equal(["T"], structRequirement.TypeArgumentNodes.Select(node => node.TypeName).ToList());
        Assert.Equal("Vec<int>", unionVariant.TypeNode?.TypeName);
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

        Assert.Equal("Box<int>*", cast.TargetTypeNode?.TypeName);
        Assert.Equal("Box<int>", sizeOf.TypeOperandNode?.TypeName);
        Assert.Equal("Box<int>", initializer.TypeNameNode?.TypeName);
        Assert.Equal(["Box<int>"], genericCall.TypeArgumentNodes.Select(node => node.TypeName).ToList());
        Assert.Equal("Box<int>", functionExpression.ReturnTypeNode?.TypeName);
        Assert.Equal("Box<int>", Assert.Single(functionExpression.Parameters).TypeNode?.TypeName);
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

    private static IEnumerable<TypeNode> CollectTypeNodes(object? value)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Collect(value, visited);
    }

    private static IEnumerable<TypeNode> Collect(object? value, HashSet<object> visited)
    {
        if (value is null or string)
        {
            yield break;
        }

        if (value is TypeNode currentTypeNode)
        {
            yield return currentTypeNode;
            yield break;
        }

        if (value is TypeSyntaxNode)
        {
            yield break;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                foreach (var nestedTypeNode in Collect(item, visited))
                {
                    yield return nestedTypeNode;
                }
            }

            yield break;
        }

        if (value is not SyntaxNode)
        {
            yield break;
        }

        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            foreach (var nestedTypeNode in Collect(property.GetValue(value), visited))
            {
                yield return nestedTypeNode;
            }
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
