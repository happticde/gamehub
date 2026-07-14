using GameHub.Core.Services;
using Xunit;

namespace GameHub.Tests;

/// <summary>Unit coverage for the single server-side GTIN normalization boundary.</summary>
public sealed class GtinNormalizerTests
{
    [Theory]
    [InlineData("012345000058", "00012345000058")]
    [InlineData("4006381333931", "04006381333931")]
    [InlineData("10012345678902", "10012345678902")]
    [InlineData("400-6381 333931", "04006381333931")]
    public void Normalize_SupportedValidInput_ReturnsCanonicalGtin14(
        string input,
        string expected)
    {
        var result = GtinNormalizer.Normalize(input);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.NormalizedGtin);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("012345000059")]
    [InlineData("4006381333932")]
    [InlineData("10012345678903")]
    public void Normalize_InvalidCheckDigit_IsRejected(string input)
    {
        var result = GtinNormalizer.Normalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("The GTIN check digit is invalid.", result.Error);
        Assert.Null(result.NormalizedGtin);
    }

    [Theory]
    [InlineData("01234558")]
    [InlineData("1234567")]
    [InlineData("1234567890")]
    [InlineData("123456789012345")]
    public void Normalize_UnsupportedLength_IsRejected(string input)
    {
        var result = GtinNormalizer.Normalize(input);

        Assert.False(result.IsValid);
        Assert.Null(result.NormalizedGtin);
    }

    [Fact]
    public void Normalize_UpcEValue_IsNotMisinterpretedAsGtin8()
    {
        var result = GtinNormalizer.Normalize("01234558");

        Assert.False(result.IsValid);
        Assert.Contains("UPC-E", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("４006381333931")]
    [InlineData("400638133393A")]
    [InlineData("400.6381333931")]
    [InlineData(" 4006381333931")]
    [InlineData("4006381333931 ")]
    [InlineData("400--6381333931")]
    [InlineData("400  6381333931")]
    public void Normalize_InvalidCharactersOrSeparatorPlacement_IsRejected(string input)
    {
        var result = GtinNormalizer.Normalize(input);

        Assert.False(result.IsValid);
        Assert.Null(result.NormalizedGtin);
    }

    [Fact]
    public void Normalize_LeadingZeroes_ArePreserved()
    {
        var result = GtinNormalizer.Normalize("012345000058");

        Assert.Equal("00012345000058", result.NormalizedGtin);
        Assert.Equal(14, result.NormalizedGtin!.Length);
    }
}
