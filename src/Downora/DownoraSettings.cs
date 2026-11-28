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
            return settings ?? new DownoraSettings();
        }
        catch
        {
            return new DownoraSettings();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
