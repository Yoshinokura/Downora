using Downora;

namespace Downora.Tests;

public sealed class Ed2kFileLinkTests
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ParsesCanonicalFileLink()
    {
        var parsed = Ed2kFileLink.TryParse(
            $"ed2k://|file|example%20file.zip|12345|{ValidHash}|/",
            out var link, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(link);
        Assert.Equal("example file.zip", link.FileName);
        Assert.Equal(12345, link.Size);
        Assert.Equal(ValidHash, link.Hash);
    }

    [Fact]
    public void NormalizesHashToLowercase()
    {
        var parsed = Ed2kFileLink.TryParse(
            $"ed2k://|file|archive.bin|42|{ValidHash.ToUpperInvariant()}|/",
            out var link, out var error);

        Assert.True(parsed, error);
        Assert.Equal(ValidHash, link!.Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/file.zip")]
    [InlineData("ed2k://|file|archive.zip|0|0123456789abcdef0123456789abcdef|/")]
    [InlineData("ed2k://|file|archive.zip|-1|0123456789abcdef0123456789abcdef|/")]
    [InlineData("ed2k://|file|archive.zip|12|not-a-hash|/")]
    [InlineData("ed2k://|file||12|0123456789abcdef0123456789abcdef|/")]
    public void RejectsMalformedLinks(string input)
    {
        Assert.False(Ed2kFileLink.TryParse(input, out var link, out _));
        Assert.Null(link);
    }

    [Fact]
    public void RejectsMissingTerminator()
    {
        Assert.False(Ed2kFileLink.TryParse(
            $"ed2k://|file|archive.zip|12|{ValidHash}|",
            out _, out _));
    }
}
