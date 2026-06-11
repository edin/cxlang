using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

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
    public void ToCxString_FormatsVariadicFunctionTypes()
    {
        var type = new TypeRef.Function(
            [new TypeRef.Named("const char", [])],
            new TypeRef.Named("int", []),
            IsVariadic: true);

        Assert.Equal("fn(const char,...)->int", TypeRefFormatter.ToCxString(type));
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

    [Fact]
    public void TypeRefParser_ParsesNestedGenericTypeTextThroughTypeSyntaxParser()
    {
        var parser = new TypeRefParser(new ProgramNode(Location.Synthetic("<test>"), []));

        var type = parser.Parse("HashMap<StringView, Vec<int>>");

        Assert.Equal("HashMap<StringView,Vec<int>>", TypeRefFormatter.ToCxString(type));
    }

    [Fact]
    public void TypeRefParser_ParsesFixedArrayTypeTextThroughTypeSyntaxParser()
    {
        var parser = new TypeRefParser(new ProgramNode(Location.Synthetic("<test>"), []));

        var type = parser.Parse("Vec<int>[4]");

        Assert.Equal("Vec<int>[4]", TypeRefFormatter.ToCxString(type));
    }
}
