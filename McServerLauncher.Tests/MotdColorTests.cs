using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>Free RGB colours and ramps in the MOTD, and where they are offered.</summary>
/// <remarks>
/// The text is written with the § character itself rather than its escape: these are about what the
/// document means, and what reaches the file is <see cref="MotdDocumentTests"/>'s business.
/// </remarks>
public class MotdColorTests
{
    [Fact]
    public void AnRgbColourIsReadAndWrittenInTheLongForm()
    {
        var doc = MotdDocument.FromProperties("§x§f§f§7§a§0§0Hola");

        Assert.Equal("Hola", doc.GetText(0));
        Assert.Equal(MotdStyle.OfHex(0xFF7A00), doc.StyleAt(0, 0));
        Assert.True(doc.HasHex);
        Assert.Equal("§x§f§f§7§a§0§0Hola", doc.ToCodes());
    }

    [Fact]
    public void AnRgbColourWipesTheFormattingLikeACodeDoes()
    {
        var doc = MotdDocument.FromProperties("§lA§x§0§0§0§0§f§fB");

        Assert.Equal(MotdFormat.Bold, doc.StyleAt(0, 0).Format);
        Assert.Equal(MotdFormat.None, doc.StyleAt(0, 1).Format);
    }

    [Fact]
    public void ACodeAfterAnRgbColourReplacesIt()
    {
        var doc = MotdDocument.FromProperties("ab");
        doc.SetHex(0, 0, 2, 0x123456);
        doc.SetColor(0, 1, 1, '6');

        Assert.Equal("§x§1§2§3§4§5§6a§6b", doc.ToCodes());
        Assert.Equal(-1, doc.StyleAt(0, 1).Rgb);
    }

    [Fact]
    public void ARampWithRgbGivesEachCharacterItsStep()
    {
        var doc = MotdDocument.FromProperties("abc");
        doc.SetGradient(0, 0, 3, 0x000000, 0xFFFFFF, hex: true);

        Assert.Equal(0x000000, doc.StyleAt(0, 0).Rgb);
        Assert.Equal(0x808080, doc.StyleAt(0, 1).Rgb);
        Assert.Equal(0xFFFFFF, doc.StyleAt(0, 2).Rgb);
    }

    [Fact]
    public void ARampWithoutRgbUsesTheNearestOfTheSixteen()
    {
        var doc = MotdDocument.FromProperties("abc");
        doc.SetGradient(0, 0, 3, MotdDocument.RgbOf('c'), MotdDocument.RgbOf('e'), hex: false);

        Assert.False(doc.HasHex);
        Assert.Equal('c', doc.StyleAt(0, 0).Color);
        Assert.Equal('e', doc.StyleAt(0, 2).Color);
    }

    [Theory]
    [InlineData(ServerType.Paper, "1.21.4", true)]
    [InlineData(ServerType.Purpur, "1.16.5", true)]
    [InlineData(ServerType.Paper, "1.15.2", false)]
    [InlineData(ServerType.Paper, "26.2", true)]
    [InlineData(ServerType.Vanilla, "1.21.4", false)]
    [InlineData(ServerType.Fabric, "1.21.4", false)]
    public void RgbIsOfferedWhereTheServerCanShowIt(ServerType type, string version, bool works) =>
        Assert.Equal(works, MotdDocument.HexWorksOn(type, version));

    [Theory]
    [InlineData("#FF7A00", 0xFF7A00)]
    [InlineData("ff7a00", 0xFF7A00)]
    [InlineData("#f70", 0xFF7700)]
    public void HexIsReadTheWaysPeopleWriteIt(string text, int rgb)
    {
        Assert.True(McServerLauncher.Views.ServerSettings.AppearanceSection.TryParseHex(text, out var read));
        Assert.Equal(rgb, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#GG0000")]
    [InlineData("#12345")]
    public void WhatIsNotAColourIsRefused(string text) =>
        Assert.False(McServerLauncher.Views.ServerSettings.AppearanceSection.TryParseHex(text, out _));
}
