using Cx.Compiler.C;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Tests;

public sealed class CTypeLowererTests
{
    [Fact]
    public void LowerType_LowersGenericAndPointerTypes()
    {
        Assert.Equal("Vec_int*", CTypeLowerer.LowerType("Vec<int>*", []));
        Assert.Equal("Box_Vec_int", CTypeLowerer.LowerType("Box<Vec<int>>", []));
    }

    [Fact]
    public void LowerDeclaration_FormatsFunctionPointerTypes()
    {
        var declaration = CTypeLowerer.LowerDeclaration("fn(int, Vec<int>*)->bool", "predicate", []);

        Assert.Equal("bool (*predicate)(int, Vec_int*)", declaration);
    }

    [Fact]
    public void TryParseFixedArrayType_ParsesElementAndLength()
    {
        var parsed = CTypeLowerer.TryParseFixedArrayType("Vec<int>[4]", out var elementType, out var length);

        Assert.True(parsed);
        Assert.Equal("Vec<int>", elementType);
        Assert.Equal("4", length);
    }

    [Fact]
    public void LowerType_SubstitutesSelfThroughSharedTypeRules()
    {
        Assert.Equal("Vec_int*", CTypeLowerer.LowerType("Self*", [], "Vec<int>"));
    }

    [Fact]
    public void ResolveAdapterStorageType_SubstitutesGenericBaseType()
    {
        var adapter = new TypeAdapterNode(
            new Location(new SourceFile("test.cx", string.Empty), 0, 1, 1),
            "Stack",
            ["T"],
            "Vec<T>",
            [],
            [],
            []);

        Assert.Equal("Vec<int>", CTypeLowerer.ResolveAdapterStorageType("Stack<int>", [adapter]));
    }

    [Fact]
    public void LowerType_LowersStructuredGenericAndPointerTypes()
    {
        var type = new TypeRef.Pointer(new TypeRef.Named("Box", [
            new TypeRef.Named("Vec", [new TypeRef.Named("int", [])]),
        ]));

        Assert.Equal("Box_Vec_int*", CTypeLowerer.LowerType(type, []));
    }

    [Fact]
    public void LowerDeclaration_FormatsStructuredFixedArrayTypes()
    {
        var type = new TypeRef.FixedArray(new TypeRef.Named("u8", []), "32");

        Assert.Equal("u8 bytes[32]", CTypeLowerer.LowerDeclaration(type, "bytes", []));
    }

    [Fact]
    public void LowerDeclaration_FormatsStructuredFunctionPointerTypes()
    {
        var type = new TypeRef.Function(
            [
                new TypeRef.Named("int", []),
                new TypeRef.Pointer(new TypeRef.Named("Vec", [new TypeRef.Named("int", [])])),
            ],
            new TypeRef.Named("bool", []));

        Assert.Equal("bool (*predicate)(int, Vec_int*)", CTypeLowerer.LowerDeclaration(type, "predicate", []));
    }

    [Fact]
    public void LowerType_SubstitutesStructuredSelf()
    {
        var type = new TypeRef.Pointer(new TypeRef.Named("Self", []));
        var self = new TypeRef.Named("Vec", [new TypeRef.Named("int", [])]);

        Assert.Equal("Vec_int*", CTypeLowerer.LowerType(type, [], self));
    }

    [Fact]
    public void LowerType_ResolvesStructuredAdapterStorageType()
    {
        var adapter = new TypeAdapterNode(
            new Location(new SourceFile("test.cx", string.Empty), 0, 1, 1),
            "Stack",
            ["T"],
            "Vec<T>",
            [],
            [],
            []);
        var type = new TypeRef.Named("Stack", [new TypeRef.Named("int", [])]);

        Assert.Equal("Vec_int", CTypeLowerer.LowerType(type, [adapter]));
    }

    [Fact]
    public void LowerType_UsesAliasNameForStructuredCTypeNames()
    {
        var type = new TypeRef.Named("Maybe", [
            new TypeRef.Alias("usize", new TypeRef.Named("unsigned long long", [])),
        ]);

        Assert.Equal("Maybe_usize", CTypeLowerer.LowerType(type, []));
    }
}
