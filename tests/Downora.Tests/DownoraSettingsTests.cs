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
                ChineseLanguage = true
            };
            settings.Save(settingsFile);

            var loaded = DownoraSettings.Load(settingsFile);

            Assert.Equal(expectedFolder, loaded.DownloadDirectory);
            Assert.True(loaded.ChineseLanguage);
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
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
