using GameHub.Core.Services;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Unit tests for slug generation — the core matching logic that determines
/// whether two games from different platforms are "the same game."
///
/// These are pure static functions with no dependencies, making them
/// the easiest and most valuable tests in the project.
/// </summary>
public class SlugGenerationTests
{
    // =========================================================================
    // GenerateSlug — full pipeline tests
    // =========================================================================

    [Theory]
    [InlineData("The Witcher 3: Wild Hunt", "the-witcher-3-wild-hunt")]
    [InlineData("ELDEN RING", "elden-ring")]
    [InlineData("Red Dead Redemption 2", "red-dead-redemption-2")]
    [InlineData("1942", "1942")]
    public void GenerateSlug_BasicTitles_ProducesCleanSlugs(string title, string expected)
    {
        Assert.Equal(expected, UnificationService.GenerateSlug(title));
    }

    [Theory]
    [InlineData("The Witcher 3: Wild Hunt - GOTY Edition", "the-witcher-3-wild-hunt")]
    [InlineData("The Witcher 3: Wild Hunt - Game of the Year", "the-witcher-3-wild-hunt")]
    [InlineData("The Witcher 3: Wild Hunt - Game of the Year Edition", "the-witcher-3-wild-hunt")]
    [InlineData("Assassin's Creed Valhalla - Deluxe Edition", "assassins-creed-valhalla")]
    [InlineData("Cyberpunk 2077 - Ultimate Edition", "cyberpunk-2077")]
    [InlineData("Skyrim - Special Edition", "skyrim")]
    [InlineData("Death Stranding Director's Cut", "death-stranding")]
    [InlineData("The Last of Us Part I Remastered", "the-last-of-us-part-i")]
    [InlineData("Horizon Zero Dawn Complete Edition", "horizon-zero-dawn")]
    [InlineData("Borderlands 3 - Digital Deluxe Edition", "borderlands-3")]
    public void GenerateSlug_EditionSuffixes_AreStripped(string title, string expected)
    {
        Assert.Equal(expected, UnificationService.GenerateSlug(title));
    }

    [Theory]
    [InlineData("DOOM®", "doom")]
    [InlineData("DOOM® (2016)", "doom-2016")]
    [InlineData("Assassin's Creed™ Valhalla", "assassins-creed-valhalla")]
    [InlineData("Far Cry© 6", "far-cry-6")]
    public void GenerateSlug_TrademarkSymbols_AreRemoved(string title, string expected)
    {
        Assert.Equal(expected, UnificationService.GenerateSlug(title));
    }

    [Fact]
    public void GenerateSlug_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", UnificationService.GenerateSlug(""));
    }

    [Fact]
    public void GenerateSlug_OnlySymbols_ReturnsEmpty()
    {
        Assert.Equal("", UnificationService.GenerateSlug("™®©"));
    }

    [Fact]
    public void GenerateSlug_MultipleSpacesAndHyphens_AreCollapsed()
    {
        Assert.Equal("red-dead-redemption", UnificationService.GenerateSlug("Red   Dead   Redemption"));
    }

    // =========================================================================
    // Cross-platform matching — the real value proposition
    // =========================================================================

    [Fact]
    public void GenerateSlug_SameGameDifferentEditions_ProduceSameSlug()
    {
        // These are all the same game across different storefronts
        var slug1 = UnificationService.GenerateSlug("The Witcher 3: Wild Hunt");
        var slug2 = UnificationService.GenerateSlug("The Witcher 3: Wild Hunt - GOTY Edition");
        var slug3 = UnificationService.GenerateSlug("The Witcher 3: Wild Hunt - Game of the Year Edition");

        Assert.Equal(slug1, slug2);
        Assert.Equal(slug1, slug3);
    }

    [Fact]
    public void GenerateSlug_DifferentGames_ProduceDifferentSlugs()
    {
        var doom = UnificationService.GenerateSlug("DOOM");
        var doomEternal = UnificationService.GenerateSlug("DOOM Eternal");

        Assert.NotEqual(doom, doomEternal);
    }

    // =========================================================================
    // NormalizeTitle — testing the normalization step in isolation
    // =========================================================================

    [Fact]
    public void NormalizeTitle_StripsTrailingSeparatorsAfterSuffixRemoval()
    {
        // After removing "Deluxe Edition", the trailing " - " should also be cleaned up
        var result = UnificationService.NormalizeTitle("Some Game - Deluxe Edition");
        Assert.Equal("Some Game", result);
    }

    [Fact]
    public void NormalizeTitle_PreservesBaseTitle()
    {
        var result = UnificationService.NormalizeTitle("The Witcher 3: Wild Hunt");
        Assert.Equal("The Witcher 3: Wild Hunt", result);
    }
}
