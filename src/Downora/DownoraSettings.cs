using System.IO;
using System.Text.Json;

namespace Downora;

public sealed class DownoraSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Downora");
    public bool ChineseLanguage { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 3;

    public static DownoraSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new DownoraSettings();
            var settings = JsonSerializer.Deserialize<DownoraSettings>(File.ReadAllText(path));
            return Normalize(settings);
        }
        catch
        {
            return new DownoraSettings();
        }
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Normalize(this);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static DownoraSettings Normalize(DownoraSettings? settings)
    {
        var normalized = settings ?? new DownoraSettings();
        if (string.IsNullOrWhiteSpace(normalized.DownloadDirectory))
        {
            normalized.DownloadDirectory = new DownoraSettings().DownloadDirectory;
        }
        else
        {
            try
            {
                normalized.DownloadDirectory = Path.GetFullPath(normalized.DownloadDirectory.Trim());
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                normalized.DownloadDirectory = new DownoraSettings().DownloadDirectory;
            }
        }

        normalized.MaxConcurrentDownloads = Math.Clamp(normalized.MaxConcurrentDownloads, 1, 10);
        return normalized;
    }
}
