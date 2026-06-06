using Cx.Compiler.Semantic;

namespace Cx.Compiler.Tests;

public sealed class GenericTypeStringRewriterTests
{
    [Fact]
    public void Substitute_ReplacesWholeTypeParameterNamesOnly()
    {
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["T"] = "int",
        };

        var rewritten = GenericTypeStringRewriter.Substitute("Pair<T, TValue>*", substitutions);

        Assert.Equal("Pair<int, TValue>*", rewritten);
    }

    [Fact]
    public void Substitute_UsesLongestNamesFirst()
    {
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["T"] = "int",
            ["TValue"] = "StringView",
        };

        var rewritten = GenericTypeStringRewriter.Substitute("Pair<T, TValue>", substitutions);

        Assert.Equal("Pair<int, StringView>", rewritten);
    }

    [Fact]
    public void SubstituteSelf_ReplacesSelfWholeWordOnly()
    {
        var rewritten = GenericTypeStringRewriter.SubstituteSelf("Self* Selfish", "Vec<int>");

        Assert.Equal("Vec<int>* Selfish", rewritten);
    }

    [Fact]
    public void SubstituteAndSelf_AppliesGenericThenSelf()
    {
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["T"] = "float",
        };

        var rewritten = GenericTypeStringRewriter.SubstituteAndSelf("fn(Self*, T)->Self", substitutions, "Vec<float>");

        Assert.Equal("fn(Vec<float>*, float)->Vec<float>", rewritten);
    }
}
