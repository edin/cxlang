using Cx.Compiler.Lexer;

namespace Cx.Compiler.Tests;

public sealed class TokenMetadataTests
{
    [Fact]
    public void TokenMetadata_CoversEveryTokenType()
    {
        var metadata = TokenMetadataProvider.All;

        Assert.Equal(Enum.GetValues<TokenType>().Length, metadata.Count);
    }

    [Fact]
    public void TokenMetadata_KeywordTextMatchesExistingKeywordTable()
    {
        Assert.Equal(KeywordDefinitions.TokenTypes, TokenMetadataProvider.KeywordTypes);
    }

    [Fact]
    public void TokenMetadata_FixedTextTokensAreUnique()
    {
        var duplicate = TokenMetadataProvider.All
            .Where(metadata => metadata.Text is not null)
            .GroupBy(metadata => metadata.Text, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        Assert.Null(duplicate);
    }

    [Fact]
    public void TokenMetadata_SymbolsAreOrderedByLongestTextFirst()
    {
        var symbolLengths = TokenMetadataProvider.SymbolsByLength
            .Select(metadata => metadata.Text!.Length)
            .ToArray();

        Assert.Equal(symbolLengths.OrderByDescending(length => length), symbolLengths);
    }
}
