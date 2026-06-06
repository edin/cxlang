using Cx.Compiler.Semantic;

namespace Cx.Compiler.Tests;

public sealed class TypeRefFormatterTests
{
    [Fact]
    public void ToCxString_FormatsNamedGenericTypes()
    {
        var type = new TypeRef.Named("Vec", [new TypeRef.Named("int", [])]);

        Assert.Equal("Vec<int>", TypeRefFormatter.ToCxString(type));
    }

    [Fact]
    public void ToCxString_FormatsPointerTypes()
    {
        var type = new TypeRef.Pointer(new TypeRef.Named("Vec", [new TypeRef.Named("int", [])]));

        Assert.Equal("Vec<int>*", TypeRefFormatter.ToCxString(type));
    }

    [Fact]
    public void ToCxString_FormatsFunctionTypes()
    {
        var type = new TypeRef.Function(
            [
                new TypeRef.Named("int", []),
                new TypeRef.Pointer(new TypeRef.Named("Vec", [new TypeRef.Named("int", [])])),
            ],
            new TypeRef.Named("bool", []));

        Assert.Equal("fn(int,Vec<int>*)->bool", TypeRefFormatter.ToCxString(type));
    }

    [Fact]
    public void ToCxString_FormatsFixedArrayTypes()
    {
        var type = new TypeRef.FixedArray(new TypeRef.Named("u8", []), "32");

        Assert.Equal("u8[32]", TypeRefFormatter.ToCxString(type));
    }

    [Fact]
    public void ToCxString_FormatsAliasTypesUsingAliasName()
    {
        var type = new TypeRef.Alias("usize", new TypeRef.Named("unsigned long long", []));

        Assert.Equal("usize", TypeRefFormatter.ToCxString(type));
    }
}
