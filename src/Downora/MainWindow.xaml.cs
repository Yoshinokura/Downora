using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Downora;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _items = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
    private readonly SemaphoreSlim _slots = new(3, 3);
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
        name = Sanitize(Uri.UnescapeDataString(name));
        var folder = GetCategoryFolder(name);
        Directory.CreateDirectory(folder);
        var target = UniquePath(Path.Combine(folder, name));
        var item = new DownloadItem(uri, target, _chinese);
        _items.Insert(0, item);
        UpdateQueueCount();
        UrlBox.Clear();
        _ = RunDownloadAsync(item);
    }

    private async Task RunDownloadAsync(DownloadItem item)
    {
        var token = item.Token.Token;
        var partPath = item.TargetPath + ".part";
        var enteredSlot = false;
        try
        {
            item.Status = _chinese ? "等待下载槽位" : "Queued";
            await _slots.WaitAsync(token);
            enteredSlot = true;

            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

            item.Status = _chinese ? "正在连接…" : "Connecting…";
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) offset = 0;
            var total = response.Content.Headers.ContentRange?.Length
                        ?? (response.Content.Headers.ContentLength is long length ? length + offset : null);

            item.Status = _chinese ? "正在下载" : "Downloading";
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long received = offset;
            var started = DateTime.UtcNow;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;
                item.Progress = total is > 0 ? Math.Min(100, received * 100d / total.Value) : 0;
                var seconds = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
                item.SpeedText = FormatSize((received - offset) / seconds) + "/s";
                item.ProgressText = total is > 0 ? $"{FormatSize(received)} / {FormatSize(total.Value)}" : FormatSize(received);
            }

            await output.FlushAsync(token);
            if (File.Exists(item.TargetPath)) File.Delete(item.TargetPath);
            File.Move(partPath, item.TargetPath);
            item.Progress = 100;
            item.IsPaused = false;
            item.IsCancelled = false;
            item.Status = _chinese ? "已完成" : "Completed";
            item.SpeedText = "";
            FooterText.Text = _chinese ? "下载完成" : "Download complete";
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
        catch (OperationCanceledException)
        {
            item.SpeedText = "";
            if (item.IsCancelled)
            {
                item.Status = _chinese ? "已取消" : "Cancelled";
                TryDelete(partPath);
            }
            else
            {
                item.IsPaused = true;
                item.Status = _chinese ? "已暂停（可继续）" : "Paused — ready to resume";
            }
        }
        catch (Exception ex)
        {
            item.Status = (_chinese ? "失败：" : "Failed: ") + ex.Message;
            item.SpeedText = "";
        }
        finally
        {
            if (enteredSlot) _slots.Release();
            UpdateQueueCount();
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadItem item) return;
        if (item.Status == "Completed" || item.Status == "已完成" || item.IsCancelled) return;

        if (item.IsPaused)
        {
            if (!(item.Status.StartsWith("Paused", StringComparison.Ordinal) || item.Status.StartsWith("已暂停", StringComparison.Ordinal)))
                return;
            item.IsPaused = false;
            item.IsCancelled = false;
            item.ResetToken();
            item.Status = _chinese ? "等待继续…" : "Resuming…";
            _ = RunDownloadAsync(item);
        }
        else
        {
            item.IsPaused = true;
            item.Token.Cancel();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem item)
        {
            item.IsCancelled = true;
            item.IsPaused = false;
            item.Token.Cancel();
            TryDelete(item.TargetPath + ".part");
            if (item.IsPaused)
                item.Status = _chinese ? "已取消" : "Cancelled";
            else
                item.Status = _chinese ? "正在取消…" : "Cancelling…";
        }
    }

    private void UpdateQueueCount() =>
        QueueCount.Text = $"  ·  {_items.Count} {(_chinese ? "个任务" : "items")}";

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
        return string.IsNullOrWhiteSpace(name) ? "download" : name.Trim();
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidate = path;
        var i = 1;
        while (File.Exists(candidate) || File.Exists(candidate + ".part"))
            candidate = Path.Combine(dir, $"{name} ({i++}){ext}");
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
    public CancellationTokenSource Token { get; private set; } = new();
    public string FileName => Path.GetFileName(TargetPath);
    public bool IsPaused { get; set; }
    public bool IsCancelled { get; set; }
    private bool _chinese;
    private string _status;
    private double _progress;
    private string _progressText = "0%";
    private string _speedText = "";
    private string _actionText = "Pause";
    public string Status { get => _status; set { _status = value; OnChanged(); ActionText = IsPaused && (value.StartsWith("Paused", StringComparison.Ordinal) || value.StartsWith("已暂停", StringComparison.Ordinal)) ? (_chinese ? "继续" : "Resume") : (_chinese ? "暂停" : "Pause"); } }
    public double Progress { get => _progress; set { _progress = value; ProgressText = $"{value:0}%"; OnChanged(); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnChanged(); } }
    public string SpeedText { get => _speedText; set { _speedText = value; OnChanged(); } }
    public string ActionText { get => _actionText; set { _actionText = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadItem(Uri url, string path, bool chinese)
    {
        Url = url;
        TargetPath = path;
        _status = chinese ? "等待中" : "Queued";
        _chinese = chinese;
        RefreshLanguage(chinese);
    }

    public void ResetToken()
    {
        Token.Dispose();
        Token = new CancellationTokenSource();
    }

    public void RefreshLanguage(bool chinese)
    {
        _chinese = chinese;
        ActionText = IsPaused && (Status.StartsWith("Paused", StringComparison.Ordinal) || Status.StartsWith("已暂停", StringComparison.Ordinal))
            ? (chinese ? "继续" : "Resume") : (chinese ? "暂停" : "Pause");
        if (Status == "Queued" || Status == "等待中")
            Status = chinese ? "等待中" : "Queued";
        else if (Status.StartsWith("Paused", StringComparison.Ordinal) || Status.StartsWith("已暂停", StringComparison.Ordinal))
            Status = chinese ? "已暂停（可继续）" : "Paused — ready to resume";
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
