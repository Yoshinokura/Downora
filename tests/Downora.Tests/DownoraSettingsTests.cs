using Downora;
using Xunit;

namespace Downora.Tests;

public sealed class DownoraSettingsTests
{
    [Fact]
    public void SettingsRoundTripPersistsDownloadDirectoryAndLanguage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DownoraTests", Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(directory, "settings.json");
        var expectedFolder = Path.Combine(directory, "custom-downloads");

        try
        {
            var settings = new DownoraSettings
            {
                DownloadDirectory = expectedFolder,
                ChineseLanguage = true,
                MaxConcurrentDownloads = 6
            };
            settings.Save(settingsFile);

            var loaded = DownoraSettings.Load(settingsFile);

            Assert.Equal(Path.GetFullPath(expectedFolder), loaded.DownloadDirectory);
            Assert.True(loaded.ChineseLanguage);
            Assert.Equal(6, loaded.MaxConcurrentDownloads);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingOrInvalidSettingsFallBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DownoraTests", Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(directory, "settings.json");

        try
        {
            Assert.False(DownoraSettings.Load(settingsFile).ChineseLanguage);
            Directory.CreateDirectory(directory);
            File.WriteAllText(settingsFile, "{ invalid json");

            var loaded = DownoraSettings.Load(settingsFile);

            Assert.False(loaded.ChineseLanguage);
            Assert.False(string.IsNullOrWhiteSpace(loaded.DownloadDirectory));
            Assert.Equal(3, loaded.MaxConcurrentDownloads);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(100, 10)]
    public void LoadClampsConcurrencyToSupportedRange(int configured, int expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DownoraTests", Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(settingsFile,
                $$"""{"DownloadDirectory":"{{directory}}","MaxConcurrentDownloads":{{configured}}}""");

            var loaded = DownoraSettings.Load(settingsFile);

            Assert.Equal(expected, loaded.MaxConcurrentDownloads);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"DownloadDirectory":null,"MaxConcurrentDownloads":3}""")]
    [InlineData("""{"DownloadDirectory":"   ","MaxConcurrentDownloads":3}""")]
    public void LoadReplacesMissingOrBlankDownloadDirectory(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DownoraTests", Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(settingsFile, json);

            var loaded = DownoraSettings.Load(settingsFile);

            Assert.False(string.IsNullOrWhiteSpace(loaded.DownloadDirectory));
            Assert.True(Path.IsPathFullyQualified(loaded.DownloadDirectory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
