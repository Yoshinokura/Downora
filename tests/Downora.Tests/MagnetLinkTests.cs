using Downora;
using Xunit;

namespace Downora.Tests;

public sealed class MagnetLinkTests
{
    private const string HexHash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ParsesHexInfoHashAndDisplayName()
    {
        var parsed = MagnetLink.TryParse(
            $"magnet:?xt=urn:btih:{HexHash}&dn=Example+Video.mkv",
            out var magnet, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(magnet);
        Assert.Equal(HexHash, magnet.InfoHash);
        Assert.Equal("Example Video.mkv", magnet.DisplayName);
    }

    [Fact]
    public void ParsesBase32InfoHash()
    {
        var parsed = MagnetLink.TryParse(
            "magnet:?xt=urn:btih=AEBAGBAFAYDQQCIKBMGA2DQPCAIREEYU",
            out var magnet, out var error);

        Assert.True(parsed, error);
        Assert.Equal(HexHash, magnet!.InfoHash);
    }

    [Fact]
    public void KeepsValidTrackersAndIgnoresInvalidOnes()
    {
        var parsed = MagnetLink.TryParse(
            $"magnet:?xt=urn:btih:{HexHash}&tr=https%3A%2F%2Ftracker.example%2Fannounce&tr=file%3A%2F%2Fbad",
            out var magnet, out var error);

        Assert.True(parsed, error);
        Assert.Single(magnet!.Trackers);
        Assert.Equal("https", magnet.Trackers[0].Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/file.torrent")]
    [InlineData("magnet:?dn=missing-hash")]
    [InlineData("magnet:?xt=urn:btih=not-a-hash")]
    [InlineData("magnet:?xt=urn:btih:1234")]
    public void RejectsInvalidMagnetLinks(string input)
    {
        Assert.False(MagnetLink.TryParse(input, out var magnet, out _));
        Assert.Null(magnet);
    }
}
