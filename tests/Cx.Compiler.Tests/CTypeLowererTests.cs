using Cx.Compiler.C;

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
}
