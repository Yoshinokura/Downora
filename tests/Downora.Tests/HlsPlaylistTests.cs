using System.IO;
using Xunit;
using Downora;

namespace Downora.Tests;

public sealed class HlsPlaylistTests
{
    private static readonly Uri PlaylistUri = new("https://media.example.test/path/index.m3u8");

    [Fact]
    public void ParsesRelativeSegmentsAndDurations()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXTINF:5.5,
            segment-001.ts
            #EXTINF:4.25,
            /video/segment-002.ts
            #EXT-X-ENDLIST
            """;

        var parsed = HlsPlaylist.Parse(playlist, PlaylistUri);

        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal("https://media.example.test/path/segment-001.ts", parsed.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://media.example.test/video/segment-002.ts", parsed.Segments[1].Uri.AbsoluteUri);
        Assert.Equal(9.75, parsed.TotalDurationSeconds, 3);
    }

    [Fact]
    public void RejectsInvalidPlaylistHeader()
    {
        Assert.Throws<InvalidDataException>(() => HlsPlaylist.Parse("#EXTINF:3,\na.ts\n#EXT-X-ENDLIST", PlaylistUri));
    }

    [Fact]
    public void RejectsLivePlaylist()
    {
        const string playlist = "#EXTM3U\n#EXTINF:3,\na.ts";
        Assert.Throws<NotSupportedException>(() => HlsPlaylist.Parse(playlist, PlaylistUri));
    }

    [Fact]
    public void RejectsEncryptedPlaylist()
    {
        const string playlist = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:3,\na.ts\n#EXT-X-ENDLIST";
        Assert.Throws<NotSupportedException>(() => HlsPlaylist.Parse(playlist, PlaylistUri));
    }

    [Fact]
    public void RejectsMasterPlaylist()
    {
        const string playlist = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1280000\nvariant.m3u8\n#EXT-X-ENDLIST";
        Assert.Throws<NotSupportedException>(() => HlsPlaylist.Parse(playlist, PlaylistUri));
    }
}
