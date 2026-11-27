using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Downora;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _items = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
    private bool _chinese;
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Downora");

    public MainWindow()
    {
        InitializeComponent();
        DownloadsList.ItemsSource = _items;
        Directory.CreateDirectory(_root);
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        _chinese = !_chinese;
        AppTitle.Text = "Downora";
        Subtitle.Text = _chinese ? "更简单的下载方式" : "A simpler way to download";
        UrlBox.ToolTip = _chinese ? "粘贴 HTTP 或 HTTPS 下载链接" : "Paste an HTTP or HTTPS link";
        QueueTitle.Text = _chinese ? "下载任务" : "Downloads";
        FooterText.Text = _chinese ? "就绪" : "Ready";
        EngineText.Text = _chinese ? "HTTP / HTTPS 下载引擎" : "HTTP / HTTPS engine";
        SettingsButton.Content = _chinese ? "设置" : "Settings";
        foreach (var item in _items) item.RefreshLanguage(_chinese);
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddDownload();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddDownload();

    private void AddDownload()
    {
        var raw = UrlBox.Text.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(_chinese ? "请输入有效的 HTTP 或 HTTPS 链接。" : "Enter a valid HTTP or HTTPS URL.",
                "Downora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "download-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        name = Sanitize(name);
        var folder = GetCategoryFolder(name);
        Directory.CreateDirectory(folder);
        var target = UniquePath(Path.Combine(folder, name));
        var item = new DownloadItem(uri, target, _chinese);
        _items.Insert(0, item);
        QueueCount.Text = $"  ·  {_items.Count} {(_chinese ? "个任务" : "items")}";
        UrlBox.Clear();
        _ = RunDownloadAsync(item);
    }

    private async Task RunDownloadAsync(DownloadItem item)
    {
        item.Status = _chinese ? "正在连接…" : "Connecting…";
        try
        {
            using var response = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, item.Token.Token);
            response.EnsureSuccessStatusCode();
            item.Status = _chinese ? "正在下载" : "Downloading";
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(item.Token.Token);
            await using var output = new FileStream(item.TargetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long received = 0;
            var started = DateTime.UtcNow;
            int read;
            while ((read = await input.ReadAsync(buffer, item.Token.Token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), item.Token.Token);
                received += read;
                item.Progress = total is > 0 ? Math.Min(100, received * 100d / total.Value) : 0;
                var seconds = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
                item.SpeedText = FormatSize(received / seconds) + "/s";
                item.ProgressText = total is > 0 ? $"{FormatSize(received)} / {FormatSize(total.Value)}" : FormatSize(received);
            }
            item.Progress = 100;
            item.Status = _chinese ? "已完成" : "Completed";
            item.SpeedText = "";
            FooterText.Text = _chinese ? "下载完成" : "Download complete";
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
        catch (OperationCanceledException)
        {
            item.Status = _chinese ? "已取消" : "Cancelled";
            TryDelete(item.TargetPath);
        }
        catch (Exception ex)
        {
            item.Status = (_chinese ? "失败：" : "Failed: ") + ex.Message;
            TryDelete(item.TargetPath);
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem item)
        {
            item.Status = _chinese ? "暂停（原型中请取消后重试）" : "Pause is not available in this prototype";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem item) item.Token.Cancel();
    }

    private string GetCategoryFolder(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var category = ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".mp3" or ".wav" => "Videos",
            ".pdf" or ".doc" or ".docx" or ".txt" or ".xlsx" or ".pptx" => "Documents",
            ".zip" or ".7z" or ".rar" or ".gz" => "Archives",
            _ => "Other"
        };
        return Path.Combine(_root, category);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidate = path;
        var i = 1;
        while (File.Exists(candidate)) candidate = Path.Combine(dir, $"{name} ({i++}){ext}");
        return candidate;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static string FormatSize(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.##} {units[index]}";
    }
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    public Uri Url { get; }
    public string TargetPath { get; }
    public CancellationTokenSource Token { get; } = new();
    public string FileName => Path.GetFileName(TargetPath);
    private string _status;
    private double _progress;
    private string _progressText = "0%";
    private string _speedText = "";
    public string Status { get => _status; set { _status = value; OnChanged(); } }
    public double Progress { get => _progress; set { _progress = value; ProgressText = $"{value:0}%"; OnChanged(); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnChanged(); } }
    public string SpeedText { get => _speedText; set { _speedText = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public DownloadItem(Uri url, string path, bool chinese) { Url = url; TargetPath = path; _status = chinese ? "等待中" : "Queued"; }
    public void RefreshLanguage(bool chinese) { if (Status == "Queued" || Status == "等待中") Status = chinese ? "等待中" : "Queued"; }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
