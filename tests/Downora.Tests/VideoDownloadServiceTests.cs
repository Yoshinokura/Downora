using Downora;

namespace Downora.Tests;

public sealed class VideoDownloadServiceTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123")]
    [InlineData("https://youtu.be/abc123")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD")]
    [InlineData("https://b23.tv/abc123")]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("https://www.dailymotion.com/video/abc123")]
    [InlineData("https://www.twitch.tv/videos/123456")]
    public void RecognizesSupportedVideoPlatformHosts(string value)
    {
        Assert.True(VideoDownloadService.IsSupportedPlatformUrl(new Uri(value)));
    }

    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("file:///C:/video.mp4")]
    [InlineData("ftp://youtube.com/video")]
    public void RejectsUnsupportedHostsAndSchemes(string value)
    {
        Assert.False(VideoDownloadService.IsSupportedPlatformUrl(new Uri(value)));
    }
}
