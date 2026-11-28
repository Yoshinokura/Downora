using Xunit;
using System.IO;
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
    [InlineData("https://www.douyin.com/video/123")]
    [InlineData("https://www.tiktok.com/@user/video/123")]
    [InlineData("https://www.iqiyi.com/v_123.html")]
    [InlineData("https://v.youku.com/v_show/id_123.html")]
    [InlineData("https://www.mgtv.com/b/123")]
    [InlineData("https://www.acfun.cn/v/ac123")]
    [InlineData("https://www.ixigua.com/123")]
    [InlineData("https://tv.sohu.com/v/123")]
    [InlineData("https://weibo.com/123/video")]
    [InlineData("https://www.douyu.com/123")]
    [InlineData("https://www.huya.com/123")]
    public void RecognizesMorePopularPlatforms(string value)
    {
        Assert.True(VideoDownloadService.IsSupportedPlatformUrl(new Uri(value)));
    }

    [Fact]
    public void FfmpegRuntimeUsesPinnedWindowsBuildAndSha256()
    {
        Assert.Matches("^[0-9a-f]{64}$", VideoDownloadService.FfmpegSha256);
        Assert.Contains("win64-lgpl-shared", VideoDownloadService.FfmpegDownloadUrl);
        Assert.StartsWith("https://github.com/", VideoDownloadService.FfmpegDownloadUrl);
    }

    [Theory]
    [InlineData(true, "继续")]
    [InlineData(false, "Resume")]
    public void RestoredVideoTaskCanBeResumed(bool chinese, string expectedLabel)
    {
        var info = new VideoPlatformInfo("Sample video", "Uploader", 60, null, "YouTube", "abc123");
        var item = new VideoDownloadItem(info, new Uri("https://youtu.be/abc123"),
            Path.Combine(Path.GetTempPath(), "Downora", "Videos", "Sample video"), 1080, chinese);

        item.RestorePaused(chinese);

        Assert.True(item.IsPaused);
        Assert.True(item.CanRetry);
        Assert.False(item.CanCancel);
        Assert.Equal(expectedLabel, item.RetryText);

        item.PrepareRetry();
        Assert.False(item.IsPaused);
        Assert.True(item.CanCancel);
        Assert.False(item.CanRetry);
        item.Token.Dispose();
    }

    [Fact]
    public void AudioOnlyModeExtractsMp3WithoutVideoMerge()
    {
        var args = VideoDownloadService.BuildDownloadModeArguments(audioOnly: true, downloadSubtitles: false, maxHeight: 1080);

        Assert.Contains("--extract-audio", args);
        Assert.Contains("mp3", args);
        Assert.Contains("bestaudio/best", args);
        Assert.DoesNotContain("--merge-output-format", args);
    }

    [Fact]
    public void SubtitleOptionDownloadsManualAndAutomaticSubtitles()
    {
        var args = VideoDownloadService.BuildDownloadModeArguments(audioOnly: false, downloadSubtitles: true, maxHeight: 2160);

        Assert.Contains("--write-subs", args);
        Assert.Contains("--write-auto-subs", args);
        Assert.Contains("--sub-langs", args);
        Assert.Contains("bestvideo[height<=2160]+bestaudio/best[height<=2160]/best", args);
    }

    [Theory]
    [InlineData("zh", "zh,zh-Hans,zh-Hant,zh-CN,zh-TW")]
    [InlineData("en", "en,en-US,en-GB")]
    [InlineData("zh,en", "zh,zh-Hans,zh-Hant,zh-CN,zh-TW,en,en-US,en-GB")]
    [InlineData("all", "all,-live_chat")]
    [InlineData("unsupported", "all,-live_chat")]
    public void SubtitleLanguagePresetMapsToSafeYtDlpLanguageList(string preset, string expected)
    {
        Assert.Equal(expected, VideoDownloadService.NormalizeSubtitleLanguages(preset));
    }

    [Theory]
    [InlineData("Sign in to confirm you're not a bot", false, "requires account access")]
    [InlineData("This is a members-only video", true, "需要账号登录或会员权限")]
    [InlineData("HTTP Error 403: Forbidden", false, "platform denied")]
    public void RestrictedVideoErrorsExplainAnonymousAccessPolicy(string error, bool chinese, string expectedText)
    {
        Assert.Contains(expectedText, VideoDownloadService.FormatDownloadError(error, chinese), StringComparison.OrdinalIgnoreCase);
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
