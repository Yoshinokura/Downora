using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Threading;

namespace Downora;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _items = new();
    private readonly ObservableCollection<Ed2kDownloadItem> _ed2kItems = new();
    private readonly ObservableCollection<BitTorrentDownloadItem> _bitTorrentItems = new();
    private readonly ObservableCollection<VideoDownloadItem> _videoItems = new();
    private readonly VideoDownloadService _videoService;
    private readonly SemaphoreSlim _videoSlots = new(2, 2);
    private readonly List<Task> _videoTasks = new();
    private readonly BitTorrentEngine _bitTorrentEngine;
    private readonly DispatcherTimer _bitTorrentRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _bitTorrentPollInProgress;
    private readonly DispatcherTimer _ed2kRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _ed2kPollInProgress;
    // Keep response bytes untouched: transparent decompression can invalidate byte offsets used by Range resume.
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly List<Task> _downloadTasks = new();
    private readonly string _queueFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downora", "downloads.json");
    private bool _allowClose;
    private bool _chinese;
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Downora");

    public MainWindow()
    {
        InitializeComponent();
        DownloadsList.ItemsSource = _items;
        Ed2kDownloadsList.ItemsSource = _ed2kItems;
        BitTorrentDownloadsList.ItemsSource = _bitTorrentItems;
        VideoDownloadsList.ItemsSource = _videoItems;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downora");
        _bitTorrentEngine = new BitTorrentEngine(appData, Path.Combine(_root, "BitTorrent"));
        _videoService = new VideoDownloadService(appData);
        _bitTorrentRefreshTimer.Tick += async (_, _) => await RefreshBitTorrentQueueAsync();
        _ed2kRefreshTimer.Tick += async (_, _) => await RefreshEd2kQueueAsync();
        _ed2kRefreshTimer.Start();
        Directory.CreateDirectory(_root);
        LoadQueue();
        EnsureEd2kEngineRunning();
        _ = RefreshEd2kQueueAsync();
        _ = InitializeBitTorrentAsync();
    }

    private async Task InitializeBitTorrentAsync()
    {
        var stateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downora", "BitTorrentState.json");
        try
        {
            var restored = await _bitTorrentEngine.RestoreStateAsync(stateFile);
            if (restored)
                FooterText.Text = _chinese ? "已恢复 BitTorrent 下载任务" : "BitTorrent tasks restored";
        }
        catch (Exception ex)
        {
            FooterText.Text = (_chinese ? "恢复 BitTorrent 任务失败：" : "Could not restore BitTorrent tasks: ") + ex.Message;
        }

        if (_allowClose) return;
        _bitTorrentRefreshTimer.Start();
        await RefreshBitTorrentQueueAsync();
    }

    private async Task RefreshBitTorrentQueueAsync()
    {
        if (_bitTorrentPollInProgress || _allowClose) return;
        _bitTorrentPollInProgress = true;
        try
        {
            var tasks = _bitTorrentEngine.GetTasks();
            var hashes = tasks.Select(task => task.InfoHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _bitTorrentItems.Where(item => !hashes.Contains(item.InfoHash)).ToList())
                _bitTorrentItems.Remove(stale);
            foreach (var task in tasks)
            {
                var item = _bitTorrentItems.FirstOrDefault(x => x.InfoHash.Equals(task.InfoHash, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    item = new BitTorrentDownloadItem(task.InfoHash, _chinese);
                    _bitTorrentItems.Add(item);
                }
                item.Update(task, _chinese);
            }
        }
        catch (Exception ex)
        {
            FooterText.Text = (_chinese ? "BitTorrent 引擎错误：" : "BitTorrent engine error: ") + ex.Message;
        }
        finally { _bitTorrentPollInProgress = false; }
    }

    private async void AddTorrentFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _chinese ? "选择种子文件" : "Select a torrent file",
            Filter = "BitTorrent files (*.torrent)|*.torrent",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _bitTorrentEngine.AddTorrentFileAsync(dialog.FileName);
            await RefreshBitTorrentQueueAsync();
            FooterText.Text = _chinese ? "种子任务已添加" : "Torrent task added";
        }
        catch (Exception ex)
        {
            MessageBox.Show((_chinese ? "无法添加种子文件：" : "Could not add torrent file: ") + ex.Message,
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BitTorrentPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BitTorrentDownloadItem item || item.IsComplete) return;
        try
        {
            if (item.IsPaused) await _bitTorrentEngine.ResumeAsync(item.InfoHash);
            else await _bitTorrentEngine.PauseAsync(item.InfoHash);
            await RefreshBitTorrentQueueAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show((_chinese ? "无法控制 BitTorrent 任务：" : "Could not control BitTorrent task: ") + ex.Message,
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BitTorrentRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BitTorrentDownloadItem item) return;
        if (MessageBox.Show(_chinese
                ? $"从任务列表移除此 BitTorrent 任务？已下载的数据会保留。\\n\\n{item.Name}"
                : $"Remove this BitTorrent task? Downloaded data will be kept.\\n\\n{item.Name}",
                "Downora", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _bitTorrentEngine.RemoveAsync(item.InfoHash, deleteDownloadedData: false);
            await RefreshBitTorrentQueueAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show((_chinese ? "无法移除任务：" : "Could not remove task: ") + ex.Message,
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EnsureEd2kEngineRunning()
    {
        var engineRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downora", "Engine", "aMule");
        if (!Directory.Exists(engineRoot) || Process.GetProcessesByName("amuled").Length > 0 || Process.GetProcessesByName("amule").Length > 0) return;

        var daemon = Directory.GetFiles(engineRoot, "amuled.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (daemon is null) return;

        try
        {
            var startInfo = new ProcessStartInfo(daemon, "--full-daemon")
            {
                WorkingDirectory = Path.GetDirectoryName(daemon)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            FooterText.Text = _chinese ? "eD2k 引擎启动失败：" + ex.Message : "Could not start eD2k engine: " + ex.Message;
        }
    }

    private async Task RefreshEd2kQueueAsync()
    {
        if (_ed2kPollInProgress) return;
        var apiAddress = new Uri("http://127.0.0.1:4713/");
        var credential = WindowsCredentialStore.Read(apiAddress);
        if (credential is null) return;

        _ed2kPollInProgress = true;
        try
        {
            var downloads = await AmuleApiClient.GetDownloadsAsync(apiAddress, credential.Password);
            var currentHashes = downloads.Select(x => x.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _ed2kItems.Where(x => !currentHashes.Contains(x.Hash)).ToList())
                _ed2kItems.Remove(stale);

            foreach (var download in downloads)
            {
                var item = _ed2kItems.FirstOrDefault(x => x.Hash.Equals(download.Hash, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    item = new Ed2kDownloadItem(download.Hash, _chinese);
                    _ed2kItems.Insert(0, item);
                }
                item.Update(download, _chinese);
            }
        }
        catch (AmuleApiAuthenticationException)
        {
            _ed2kRefreshTimer.Stop();
            FooterText.Text = _chinese ? "aMule 密码已更改，请重新提交 eD2k 链接以更新凭据" : "aMule password changed; submit an eD2k link again to refresh credentials";
        }
        catch
        {
            // The engine may be starting, stopping, or temporarily unavailable; retry on the next tick.
        }
        finally
        {
            _ed2kPollInProgress = false;
        }
    }

    private async void Ed2kPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Ed2kDownloadItem item || item.IsFinished) return;
        var credential = WindowsCredentialStore.Read(new Uri("http://127.0.0.1:4713/"));
        if (credential is null) return;
        try
        {
            var action = item.IsPaused ? "resume" : "pause";
            await AmuleApiClient.SetDownloadActionAsync(new Uri("http://127.0.0.1:4713/"), credential.Password, item.Hash, action);
            await RefreshEd2kQueueAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show((_chinese ? "无法控制 eD2k 任务：" : "Could not control eD2k task: ") + ex.Message,
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Ed2kCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Ed2kDownloadItem item || item.IsFinished) return;
        if (MessageBox.Show(_chinese
                ? $"确定取消并删除 aMule 中的任务及未完成数据吗？\\n\\n{item.FileName}"
                : $"Cancel this aMule task and delete its incomplete data?\\n\\n{item.FileName}",
                "Downora", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var credential = WindowsCredentialStore.Read(new Uri("http://127.0.0.1:4713/"));
        if (credential is null) return;
        try
        {
            await AmuleApiClient.RemoveDownloadAsync(new Uri("http://127.0.0.1:4713/"), credential.Password, item.Hash);
            await RefreshEd2kQueueAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show((_chinese ? "无法删除 eD2k 任务：" : "Could not remove eD2k task: ") + ex.Message,
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ed2kSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "Install-Ed2kEngine.ps1");
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(_chinese ? "未找到 eD2k 一键安装脚本。" : "The eD2k setup script could not be found.",
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"")
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show((_chinese ? "无法启动安装程序：" : "Could not launch setup: ") + ex.Message,
                "Downora", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        _chinese = !_chinese;
        BitTorrentTab.Header = _chinese ? "BitTorrent 下载" : "BitTorrent";
        VideoTab.Header = _chinese ? "视频下载" : "Video downloads";
        foreach (var item in _videoItems) item.RefreshLanguage(_chinese);
        AddTorrentFileButton.Content = _chinese ? "＋  添加 .torrent 文件" : "＋  Add .torrent file";
        foreach (var item in _bitTorrentItems) item.RefreshLanguage(_chinese);
        Subtitle.Text = _chinese ? "更简单的下载方式" : "A simpler way to download";
        UrlBox.ToolTip = _chinese ? "粘贴直链、Bilibili / YouTube 视频链接、eD2k 或 Magnet 链接" : "Paste a direct link, Bilibili / YouTube video URL, eD2k or Magnet link";
        QueueTitle.Text = _chinese ? "下载任务" : "Downloads";
        FooterText.Text = _chinese ? "就绪" : "Ready";
        EngineText.Text = _chinese ? "HTTP / HTTPS / FTP / FTPS / eD2k 链接解析" : "HTTP / HTTPS / FTP / FTPS / eD2k link parsing";
        HttpTab.Header = _chinese ? "普通下载" : "Direct downloads";
        Ed2kTab.Header = _chinese ? "eD2k 下载" : "eD2k downloads";
        foreach (var item in _ed2kItems) item.RefreshLanguage(_chinese);
        SettingsButton.Content = _chinese ? "设置" : "Settings";
        Ed2kSetupButton.Content = _chinese ? "一键配置 eD2k" : "Set up eD2k";
        foreach (var item in _items) item.RefreshLanguage(_chinese);
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddDownload();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddDownload();

    private async void AddDownload()
    {
        var raw = UrlBox.Text.Trim();
        if (raw.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetLink.TryParse(raw, out _, out var magnetError))
            {
                MessageBox.Show((_chinese ? "无效的 Magnet 链接：" : "Invalid Magnet link: ") + magnetError,
                    "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                await _bitTorrentEngine.AddMagnetAsync(raw);
                UrlBox.Clear();
                await RefreshBitTorrentQueueAsync();
                BitTorrentTab.IsSelected = true;
                FooterText.Text = _chinese ? "Magnet 任务已添加" : "Magnet task added";
            }
            catch (Exception ex)
            {
                MessageBox.Show((_chinese ? "无法添加 Magnet 任务：" : "Could not add Magnet task: ") + ex.Message,
                    "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (raw.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase))
        {
            if (Ed2kFileLink.TryParse(raw, out var ed2k, out var parseError))
            {
                var apiAddress = new Uri("http://127.0.0.1:4713/");
                var savedCredential = WindowsCredentialStore.Read(apiAddress);
                var password = savedCredential?.Password ?? PromptForAmulePassword();
                if (password is null) return;

                try
                {
                    await AmuleApiClient.AddLinkAsync(apiAddress, password, raw);
                    WindowsCredentialStore.Save(apiAddress, "admin", password);
                    FooterText.Text = _chinese ? "eD2k 任务已提交给 aMule" : "eD2k task sent to aMule";
                    MessageBox.Show(_chinese
                        ? $"eD2k 任务已提交给本机 aMule 引擎。\n\n{ed2k!.FileName}"
                        : $"The eD2k task was submitted to the local aMule engine.\n\n{ed2k!.FileName}",
                        "Downora", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (AmuleApiAuthenticationException)
                {
                    var retryPassword = PromptForAmulePassword();
                    if (retryPassword is null) return;
                    try
                    {
                        await AmuleApiClient.AddLinkAsync(apiAddress, retryPassword, raw);
                        WindowsCredentialStore.Save(apiAddress, "admin", retryPassword);
                        FooterText.Text = _chinese ? "eD2k 任务已提交给 aMule" : "eD2k task sent to aMule";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show((_chinese ? "无法提交 eD2k 任务：" : "Could not submit eD2k task: ") + ex.Message,
                            "Downora", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show((_chinese
                        ? "无法连接本机 aMule API。请安装并启动 aMule 3.1.0 或更新版本，并启用 amuleapi（默认端口 4713）。\n\n"
                        : "Could not reach the local aMule API. Install and start aMule 3.1.0 or newer and enable amuleapi (default port 4713).\n\n")
                        + ex.Message, "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show((_chinese ? "无效的 eD2k 链接：" : "Invalid eD2k link: ") + parseError,
                    "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp && uri.Scheme != "ftps"))
        {
            MessageBox.Show(_chinese ? "请输入有效的 HTTP、HTTPS、FTP 或 FTPS 链接。" : "Enter a valid HTTP, HTTPS, FTP or FTPS URL.",
                "Downora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            MessageBox.Show(_chinese
                ? "出于安全考虑，请勿在下载链接中嵌入账号或密码。"
                : "For security, credentials must not be embedded in download URLs.",
                "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (VideoDownloadService.IsSupportedPlatformUrl(uri))
        {
            UrlBox.Clear();
            await AddVideoDownloadAsync(uri);
            return;
        }

        if (uri.Scheme is "ftp" or "ftps")
        {
            if (WindowsCredentialStore.Read(uri) is null)
            {
                var credential = PromptForFtpCredentials();
                if (credential is null) return;
                try { WindowsCredentialStore.Save(uri, credential.UserName, credential.Password); }
                catch (Exception ex)
                {
                    MessageBox.Show((_chinese ? "无法保存凭据：" : "Could not save credentials: ") + ex.Message,
                        "Downora", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
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
        SaveQueue();
        StartDownload(item);
    }

    private async Task AddVideoDownloadAsync(Uri uri)
    {
        FooterText.Text = _chinese ? "正在读取视频信息…" : "Reading video information…";
        try
        {
            var info = await _videoService.GetInfoAsync(uri);
            var safeTitle = Sanitize(info.Title);
            if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "video-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var folder = Path.Combine(_root, "Videos");
            Directory.CreateDirectory(folder);
            var reservedPath = UniquePath(Path.Combine(folder, safeTitle + ".mp4"));
            var outputBase = Path.Combine(folder, Path.GetFileNameWithoutExtension(reservedPath));
            var item = new VideoDownloadItem(info, _chinese);
            _videoItems.Insert(0, item);
            VideoTab.IsSelected = true;
            _videoTasks.Add(RunVideoDownloadAsync(item, uri, outputBase));
            FooterText.Text = _chinese ? "视频任务已加入队列" : "Video task added to queue";
        }
        catch (Exception ex)
        {
            FooterText.Text = (_chinese ? "视频解析失败：" : "Video extraction failed: ") + ex.Message;
            MessageBox.Show(FooterText.Text, "Downora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunVideoDownloadAsync(VideoDownloadItem item, Uri uri, string outputBase)
    {
        var enteredSlot = false;
        try
        {
            item.SetStatus(_chinese ? "等待下载" : "Queued");
            await _videoSlots.WaitAsync(item.Token.Token);
            enteredSlot = true;
            item.SetStatus(_chinese ? "正在下载" : "Downloading");
            var progress = new Progress<VideoDownloadProgress>(value => item.Update(value, _chinese));
            var outputPath = await _videoService.DownloadAsync(uri, outputBase, progress, item.Token.Token);
            item.SetCompleted(outputPath, _chinese);
            FooterText.Text = _chinese ? "视频下载完成" : "Video download complete";
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
        catch (OperationCanceledException)
        {
            item.SetCancelled(_chinese);
        }
        catch (Exception ex)
        {
            item.SetFailed(ex.Message, _chinese);
        }
        finally
        {
            if (enteredSlot) _videoSlots.Release();
        }
    }

    private void VideoCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VideoDownloadItem item || !item.CanCancel) return;
        item.Token.Cancel();
    }

    private string? PromptForAmulePassword()
    {
        var dialog = new Window
        {
            Title = _chinese ? "aMule 管理员密码" : "aMule administrator password",
            Owner = this,
            Width = 390,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White
        };

        var password = new PasswordBox { Margin = new Thickness(0, 8, 0, 16), Height = 30 };
        var submit = new Button { Content = _chinese ? "连接并提交" : "Connect and submit", MinWidth = 120, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = _chinese ? "取消" : "Cancel", MinWidth = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(submit);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = _chinese ? "输入本机 aMule API 管理员密码" : "Enter the local aMule API administrator password" });
        panel.Children.Add(password);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        string? result = null;
        submit.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(password.Password))
            {
                MessageBox.Show(_chinese ? "请输入密码。" : "Enter the password.", "Downora");
                return;
            }
            result = password.Password;
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private NetworkCredential? PromptForFtpCredentials()
    {
        var dialog = new Window
        {
            Title = _chinese ? "FTP 登录凭据" : "FTP credentials",
            Owner = this,
            Width = 390,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White
        };

        var username = new TextBox { Text = "anonymous", Margin = new Thickness(0, 5, 0, 12), Height = 30 };
        var password = new PasswordBox { Margin = new Thickness(0, 5, 0, 14), Height = 30 };
        var save = new Button { Content = _chinese ? "保存并继续" : "Save and continue", MinWidth = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = _chinese ? "取消" : "Cancel", MinWidth = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = _chinese ? "用户名" : "Username" });
        panel.Children.Add(username);
        panel.Children.Add(new TextBlock { Text = _chinese ? "密码" : "Password" });
        panel.Children.Add(password);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        NetworkCredential? result = null;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(username.Text))
            {
                MessageBox.Show(_chinese ? "请输入用户名。" : "Enter a username.", "Downora");
                return;
            }
            result = new NetworkCredential(username.Text.Trim(), password.Password);
            dialog.DialogResult = true;
        };

        return dialog.ShowDialog() == true ? result : null;
    }

    private void StartDownload(DownloadItem item)
    {
        _downloadTasks.Add(RunDownloadAsync(item));
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

            if (item.Url.Scheme is "ftp" or "ftps")
            {
                await DownloadFtpAsync(item, partPath, token);
                return;
            }

            if (await TryDownloadSegmentedAsync(item, partPath, token))
                return;

            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            item.Status = _chinese ? "正在连接…" : "Connecting…";
            using var response = await SendHttpWithRetryAsync(item.Url, offset, item, token);
            response.EnsureSuccessStatusCode();

            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange?.From != offset)
                throw new InvalidDataException("The server returned an unexpected byte range.");
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
            await UpdateChecksumAsync(item, token);
            item.Progress = 100;
            item.IsPaused = false;
            item.IsCancelled = false;
            item.IsCompleted = true;
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
            SaveQueue();
        }
    }




    private async Task<bool> TryDownloadSegmentedAsync(DownloadItem item, string partPath, CancellationToken token)
    {
        const int segmentCount = 4;
        const long minimumSize = 16L * 1024 * 1024;

        using var head = new HttpRequestMessage(HttpMethod.Head, item.Url);
        using var metadata = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token);
        if (!metadata.IsSuccessStatusCode ||
            metadata.Content.Headers.ContentLength is not long total ||
            total < minimumSize ||
            !metadata.Headers.AcceptRanges.Any(x => x.Equals("bytes", StringComparison.OrdinalIgnoreCase)))
            return false;

        var segmentSize = (total + segmentCount - 1) / segmentCount;
        var segmentPaths = Enumerable.Range(0, segmentCount)
            .Select(index => partPath + $".seg{index}")
            .ToArray();

        try
        {
            item.Status = _chinese ? "正在准备分段下载…" : "Preparing segmented download…";
            long initialReceived = 0;
            for (var i = 0; i < segmentCount; i++)
            {
                var start = i * segmentSize;
                var end = Math.Min(total - 1, start + segmentSize - 1);
                if (start > end) continue;
                var length = end - start + 1;
                var existing = File.Exists(segmentPaths[i]) ? new FileInfo(segmentPaths[i]).Length : 0;
                if (existing > length)
                {
                    TryDelete(segmentPaths[i]);
                    existing = 0;
                }
                initialReceived += existing;
            }

            long receivedOverall = initialReceived;
            var started = DateTime.UtcNow;
            var workers = Enumerable.Range(0, segmentCount).Select(async index =>
            {
                var start = index * segmentSize;
                var end = Math.Min(total - 1, start + segmentSize - 1);
                if (start > end) return;
                var expected = end - start + 1;
                var path = segmentPaths[index];
                var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (existing == expected) return;

                var requestStart = start + existing;
                using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                request.Headers.Range = new RangeHeaderValue(requestStart, end);
                using var response = await SendHttpRangeWithRetryAsync(request, item, token);
                if (response.StatusCode != HttpStatusCode.PartialContent ||
                    response.Content.Headers.ContentRange?.From != requestStart ||
                    response.Content.Headers.ContentRange?.To is not long responseEnd ||
                    responseEnd > end)
                    throw new InvalidDataException("The server did not honor a segmented byte-range request.");

                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(path, existing > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    var now = Interlocked.Add(ref receivedOverall, read);
                    var seconds = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
                    item.Progress = Math.Min(99.9, now * 100d / total);
                    item.ProgressText = $"{FormatSize(now)} / {FormatSize(total)}";
                    item.SpeedText = FormatSize((now - initialReceived) / seconds) + "/s";
                }
                await output.FlushAsync(token);
                if (new FileInfo(path).Length != expected)
                    throw new EndOfStreamException("A segmented response ended before the requested range was complete.");
            }).ToArray();

            item.Status = _chinese ? "正在分段下载" : "Downloading in segments";
            await Task.WhenAll(workers);

            await using (var combined = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                foreach (var path in segmentPaths)
                {
                    await using var segment = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    int read;
                    while ((read = await segment.ReadAsync(buffer, token)) > 0)
                        await combined.WriteAsync(buffer.AsMemory(0, read), token);
                }
                await combined.FlushAsync(token);
            }

            if (new FileInfo(partPath).Length != total)
                throw new InvalidDataException("The assembled segmented download has an unexpected length.");

            foreach (var path in segmentPaths) TryDelete(path);
            if (File.Exists(item.TargetPath)) File.Delete(item.TargetPath);
            File.Move(partPath, item.TargetPath);
            await UpdateChecksumAsync(item, token);
            item.Progress = 100;
            item.ProgressText = FormatSize(total);
            item.SpeedText = "";
            item.IsPaused = false;
            item.IsCancelled = false;
            item.IsCompleted = true;
            item.Status = _chinese ? "已完成" : "Completed";
            FooterText.Text = _chinese ? "下载完成" : "Download complete";
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            return true;
        }
        catch (InvalidDataException)
        {
            // A server may advertise byte ranges but fail to honor a specific range request.
            // Discard segmented fragments and let the caller fall back to a normal HTTP transfer.
            foreach (var path in segmentPaths) TryDelete(path);
            TryDelete(partPath);
            return false;
        }
        catch
        {
            if (item.IsCancelled)
            {
                foreach (var path in segmentPaths) TryDelete(path);
            }
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendHttpRangeWithRetryAsync(
        HttpRequestMessage request, DownloadItem item, CancellationToken token)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            retryRequest.Headers.Range = request.Headers.Range;
            try
            {
                var response = await _http.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, token);
                var transient = response.StatusCode == HttpStatusCode.RequestTimeout ||
                                (int)response.StatusCode == 429 ||
                                (int)response.StatusCode >= 500;
                if (!transient || attempt >= maxAttempts)
                    return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maxAttempts && !token.IsCancellationRequested)
            {
            }

            item.Status = _chinese ? "分段连接失败，正在重试…" : "Segment connection failed, retrying…";
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), token);
        }
    }

    private async Task<HttpResponseMessage> SendHttpWithRetryAsync(
        Uri url, long offset, DownloadItem item, CancellationToken token)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

            try
            {
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var transient = response.StatusCode == HttpStatusCode.RequestTimeout ||
                                (int)response.StatusCode == 429 ||
                                (int)response.StatusCode >= 500;
                if (!transient || attempt >= maxAttempts)
                    return response;

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maxAttempts && !token.IsCancellationRequested)
            {
                // Retry transient connection failures with a bounded exponential delay.
            }

            item.Status = _chinese
                ? $"连接暂时失败，正在重试（{attempt}/{maxAttempts - 1}）…"
                : $"Temporary connection failure, retrying ({attempt}/{maxAttempts - 1})…";
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), token);
        }
    }

    private async Task DownloadFtpAsync(DownloadItem item, string partPath, CancellationToken token)
    {
        var isFtps = item.Url.Scheme == "ftps";
        var credential = WindowsCredentialStore.Read(item.Url)
            ?? new NetworkCredential("anonymous", "downora@localhost");

        var builder = new UriBuilder(item.Url) { Scheme = Uri.UriSchemeFtp };
        // This adapter implements explicit FTPS (AUTH TLS), which uses the FTP control port by default.
        // Implicit FTPS on port 990 is a different protocol and is not supported yet.
        if (builder.Port == 990)
            throw new NotSupportedException(_chinese
                ? "暂不支持隐式 FTPS（端口 990），请使用显式 FTPS（默认端口 21）。"
                : "Implicit FTPS (port 990) is not supported. Use explicit FTPS (port 21 by default).");
        if (builder.Port == -1) builder.Port = 21;
        builder.UserName = "anonymous";
        builder.Password = "downora@localhost";
        var request = (FtpWebRequest)WebRequest.Create(builder.Uri);
        request.Method = WebRequestMethods.Ftp.DownloadFile;
        request.UseBinary = true;
        request.UsePassive = true;
        request.EnableSsl = isFtps;
        request.KeepAlive = false;
        request.Credentials = credential;

        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (offset > 0) request.ContentOffset = offset;

        item.Status = _chinese ? (isFtps ? "正在连接 FTPS…" : "正在连接 FTP…")
                               : (isFtps ? "Connecting to FTPS…" : "Connecting to FTP…");

        using var cancellation = token.Register(request.Abort);
        try
        {
            using var response = (FtpWebResponse)await request.GetResponseAsync();
            await using var input = response.GetResponseStream();
            await using var output = new FileStream(partPath, offset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, true);

            var total = response.ContentLength > 0 ? offset + response.ContentLength : -1;
            var received = offset;
            var started = DateTime.UtcNow;
            var buffer = new byte[81920];
            int read;
            item.Status = _chinese ? "正在下载" : "Downloading";
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;
                item.Progress = total > 0 ? Math.Min(100, received * 100d / total) : 0;
                var seconds = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
                item.SpeedText = FormatSize((received - offset) / seconds) + "/s";
                item.ProgressText = total > 0 ? $"{FormatSize(received)} / {FormatSize(total)}" : FormatSize(received);
            }

            await output.FlushAsync(token);
            if (File.Exists(item.TargetPath)) File.Delete(item.TargetPath);
            File.Move(partPath, item.TargetPath);
            await UpdateChecksumAsync(item, token);
            item.Progress = 100;
            item.IsPaused = false;
            item.IsCancelled = false;
            item.IsCompleted = true;
            item.Status = _chinese ? "已完成" : "Completed";
            item.SpeedText = "";
            FooterText.Text = _chinese ? "下载完成" : "Download complete";
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
        catch (WebException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    private static async Task UpdateChecksumAsync(DownloadItem item, CancellationToken token)
    {
        await using var stream = new FileStream(item.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        item.ChecksumText = "SHA-256: " + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadItem item) return;
        if (item.IsCompleted || item.IsCancelled) return;

        if (item.IsPaused)
        {
            if (!(item.Status.StartsWith("Paused", StringComparison.Ordinal) || item.Status.StartsWith("已暂停", StringComparison.Ordinal)))
                return;
            item.IsPaused = false;
            item.IsCancelled = false;
            item.ResetToken();
            item.Status = _chinese ? "等待继续…" : "Resuming…";
            StartDownload(item);
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
            if (item.IsCompleted || item.IsCancelled) return;

            // A paused task has no active stream to unwind, so it can be finalized immediately.
            var wasPaused = item.IsPaused;
            item.IsCancelled = true;
            item.IsPaused = false;
            item.Token.Cancel();

            if (wasPaused)
            {
                TryDelete(item.TargetPath + ".part");
                item.Status = _chinese ? "已取消" : "Cancelled";
                SaveQueue();
            }
            else
            {
                // Let the active download release its file handle before deleting the partial file.
                item.Status = _chinese ? "正在取消…" : "Cancelling…";
            }
        }
    }


    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(_queueFile)) return;
            var saved = JsonSerializer.Deserialize<List<SavedDownload>>(File.ReadAllText(_queueFile)) ?? new();
            foreach (var entry in saved)
            {
                if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp && uri.Scheme != "ftps") ||
                    string.IsNullOrWhiteSpace(entry.TargetPath))
                    continue;

                var item = new DownloadItem(uri, entry.TargetPath, _chinese)
                {
                    IsCompleted = entry.IsCompleted || File.Exists(entry.TargetPath),
                    IsCancelled = entry.IsCancelled,
                    IsPaused = !entry.IsCompleted && !entry.IsCancelled
                };

                if (item.IsCompleted)
                {
                    item.IsPaused = false;
                    item.Progress = 100;
                    item.Status = _chinese ? "已完成" : "Completed";
                }
                else if (item.IsCancelled)
                {
                    item.IsPaused = false;
                    item.Status = _chinese ? "已取消" : "Cancelled";
                }
                else
                {
                    item.Status = _chinese ? "已暂停（可继续）" : "Paused — ready to resume";
                    var part = item.TargetPath + ".part";
                    if (File.Exists(part))
                        item.ProgressText = FormatSize(new FileInfo(part).Length);
                }

                _items.Add(item);
            }
            UpdateQueueCount();
        }
        catch
        {
            // A damaged queue file must not prevent the application from starting.
        }
    }

    private void SaveQueue()
    {
        try
        {
            var directory = Path.GetDirectoryName(_queueFile)!;
            Directory.CreateDirectory(directory);
            var entries = _items.Select(item => new SavedDownload(
                item.Url.AbsoluteUri, item.TargetPath, item.IsCompleted, item.IsCancelled)).ToList();
            var temporary = _queueFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _queueFile, true);
        }
        catch
        {
            // Queue persistence is best-effort; downloads should continue if the profile is read-only.
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;

        foreach (var item in _items)
        {
            if (!item.IsCompleted && !item.IsCancelled)
            {
                item.IsPaused = true;
                item.Token.Cancel();
            }
        }

        foreach (var video in _videoItems)
            if (video.CanCancel) video.Token.Cancel();
        try { await Task.WhenAll(_downloadTasks.Concat(_videoTasks).ToArray()); } catch { }
        _ed2kRefreshTimer.Stop();
        _bitTorrentRefreshTimer.Stop();
        try { await _bitTorrentEngine.SaveStateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downora", "BitTorrentState.json")); } catch { }
        try { await _bitTorrentEngine.DisposeAsync(); } catch { }
        _videoService.Dispose();
        SaveQueue();
        _allowClose = true;
        Close();
    }

    private sealed record SavedDownload(string Url, string TargetPath, bool IsCompleted, bool IsCancelled);

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


public sealed class VideoDownloadItem : INotifyPropertyChanged
{
    public VideoPlatformInfo Info { get; }
    public CancellationTokenSource Token { get; } = new();
    public string Title { get; }
    public string Subtitle { get; }
    public string Status { get; private set; } = "";
    public double Progress { get; private set; }
    public string ProgressText { get; private set; } = "0%";
    public string SpeedText { get; private set; } = "";
    public string EtaText { get; private set; } = "";
    public bool IsCompleted { get; private set; }
    public bool IsCancelled { get; private set; }
    public bool IsFailed { get; private set; }
    public bool CanCancel => !IsCompleted && !IsCancelled && !IsFailed && !Token.IsCancellationRequested;
    private bool _chinese;
    public event PropertyChangedEventHandler? PropertyChanged;

    public VideoDownloadItem(VideoPlatformInfo info, bool chinese)
    {
        Info = info;
        Title = info.Title;
        Subtitle = string.Join(" · ", new[] { info.Uploader, info.DurationSeconds is double seconds ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : null, info.Extractor }.Where(x => !string.IsNullOrWhiteSpace(x)));
        _chinese = chinese;
        Status = chinese ? "等待中" : "Queued";
    }

    public void SetStatus(string status) { Status = status; NotifyAll(); }

    public void Update(VideoDownloadProgress progress, bool chinese)
    {
        _chinese = chinese;
        Progress = Math.Clamp(progress.Percent, 0, 100);
        ProgressText = $"{Progress:0.0}%";
        SpeedText = progress.Speed ?? "";
        EtaText = progress.Eta is null ? "" : (chinese ? "剩余 " : "ETA ") + progress.Eta;
        Status = chinese ? "正在下载" : "Downloading";
        NotifyAll();
    }

    public void SetCompleted(string path, bool chinese)
    {
        Progress = 100;
        ProgressText = "100%";
        SpeedText = "";
        EtaText = "";
        Status = (chinese ? "已完成 · " : "Completed · ") + Path.GetFileName(path);
        IsCompleted = true;
        NotifyAll();
    }

    public void SetCancelled(bool chinese)
    {
        IsCancelled = true;
        Status = chinese ? "已取消（临时文件保留）" : "Cancelled (partial file kept)";
        SpeedText = "";
        EtaText = "";
        NotifyAll();
    }

    public void SetFailed(string error, bool chinese)
    {
        IsFailed = true;
        Status = (chinese ? "失败：" : "Failed: ") + error;
        SpeedText = "";
        EtaText = "";
        NotifyAll();
    }

    public void RefreshLanguage(bool chinese)
    {
        _chinese = chinese;
        NotifyAll();
    }

    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(Status), nameof(Progress), nameof(ProgressText), nameof(SpeedText), nameof(EtaText), nameof(CanCancel), nameof(IsFailed) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class BitTorrentDownloadItem : INotifyPropertyChanged
{
    public string InfoHash { get; }
    public string Name { get; private set; } = "";
    public string StatusText { get; private set; } = "";
    public string DetailText { get; private set; } = "";
    public double Progress { get; private set; }
    public string ProgressText { get; private set; } = "0%";
    public string SpeedText { get; private set; } = "";
    public string ActionText { get; private set; } = "Pause";
    public bool IsPaused { get; private set; }
    public bool IsComplete { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public BitTorrentDownloadItem(string infoHash, bool chinese) { InfoHash = infoHash; }

    internal void Update(BitTorrentTaskSnapshot task, bool chinese)
    {
        Name = task.Name;
        Progress = task.Progress;
        ProgressText = task.SizeBytes > 0
            ? $"{Progress:0.0}% · {Format(task.SizeBytes * Progress / 100)} / {Format(task.SizeBytes)}"
            : $"{Progress:0.0}%";
        SpeedText = $"{Format(task.DownloadSpeedBytesPerSecond)}/s ↓  {Format(task.UploadSpeedBytesPerSecond)}/s ↑";
        DetailText = chinese ? $"连接节点：{task.ConnectedPeers} · {InfoHash}" : $"Peers: {task.ConnectedPeers} · {InfoHash}";
        IsPaused = task.State.Equals("Paused", StringComparison.OrdinalIgnoreCase) || task.State.Equals("Stopped", StringComparison.OrdinalIgnoreCase);
        IsComplete = task.IsComplete;
        StatusText = chinese ? $"状态：{Localize(task.State)}" : task.State;
        ActionText = IsPaused ? (chinese ? "继续" : "Resume") : (chinese ? "暂停" : "Pause");
        NotifyAll();
    }

    public void RefreshLanguage(bool chinese) => NotifyAll();

    private static string Localize(string state) => state.ToLowerInvariant() switch
    {
        "downloading" => "正在下载", "paused" => "已暂停", "stopped" => "已停止",
        "seeding" => "做种中", "hashing" => "校验中", "error" => "错误",
        "metadata" => "获取元数据", "queued" => "排队中", _ => state
    };

    private static string Format(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.##} {units[index]}";
    }

    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(Name), nameof(StatusText), nameof(DetailText), nameof(Progress), nameof(ProgressText), nameof(SpeedText), nameof(ActionText), nameof(IsPaused), nameof(IsComplete) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    public bool IsCompleted { get; set; }
    private bool _chinese;
    private string _status;
    private double _progress;
    private string _progressText = "0%";
    private string _speedText = "";
    private string _checksumText = "";
    private string _actionText = "Pause";
    public string Status { get => _status; set { _status = value; OnChanged(); ActionText = IsPaused && (value.StartsWith("Paused", StringComparison.Ordinal) || value.StartsWith("已暂停", StringComparison.Ordinal)) ? (_chinese ? "继续" : "Resume") : (_chinese ? "暂停" : "Pause"); } }
    public double Progress { get => _progress; set { _progress = value; ProgressText = $"{value:0}%"; OnChanged(); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnChanged(); } }
    public string SpeedText { get => _speedText; set { _speedText = value; OnChanged(); } }
    public string ChecksumText { get => _checksumText; set { _checksumText = value; OnChanged(); } }
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
public sealed class Ed2kDownloadItem : INotifyPropertyChanged
{
    public string Hash { get; }
    public string FileName { get; private set; } = "";
    public string Status { get; private set; } = "";
    public double Progress { get; private set; }
    public string ProgressText { get; private set; } = "0%";
    public string SpeedText { get; private set; } = "";
    public string SourcesText { get; private set; } = "";
    public string ActionText { get; private set; } = "Pause";
    public bool IsPaused { get; private set; }
    public bool IsFinished { get; private set; }
    public bool CanControl => !IsFinished;
    private bool _chinese;
    public event PropertyChangedEventHandler? PropertyChanged;

    public Ed2kDownloadItem(string hash, bool chinese) { Hash = hash; _chinese = chinese; }

    internal void Update(AmuleDownload download, bool chinese)
    {
        _chinese = chinese;
        FileName = download.Name;
        Progress = download.ProgressPercent;
        ProgressText = download.SizeBytes > 0
            ? $"{Progress:0.0}% · {Format(download.CompletedBytes)} / {Format(download.SizeBytes)}"
            : $"{Progress:0.0}%";
        SpeedText = download.SpeedBytesPerSecond > 0 ? Format(download.SpeedBytesPerSecond) + "/s" : "";
        SourcesText = chinese ? $"来源：{download.SourceCount}" : $"Sources: {download.SourceCount}";
        IsPaused = download.Status is "paused" or "stopped";
        IsFinished = download.Status is "completed" or "completing";
        Status = LocalizeStatus(download.Status, chinese);
        ActionText = IsPaused ? (chinese ? "继续" : "Resume") : (chinese ? "暂停" : "Pause");
        NotifyAll();
    }

    public void RefreshLanguage(bool chinese)
    {
        _chinese = chinese;
        ActionText = IsPaused ? (chinese ? "继续" : "Resume") : (chinese ? "暂停" : "Pause");
        NotifyAll();
    }

    private static string LocalizeStatus(string status, bool chinese) => status switch
    {
        "downloading" => chinese ? "正在下载" : "Downloading",
        "waiting" => chinese ? "等待来源" : "Waiting for sources",
        "paused" => chinese ? "已暂停" : "Paused",
        "stopped" => chinese ? "已停止" : "Stopped",
        "hashing" => chinese ? "正在校验" : "Hashing",
        "allocating" => chinese ? "正在分配空间" : "Allocating",
        "completing" => chinese ? "正在完成" : "Completing",
        "completed" => chinese ? "已完成" : "Completed",
        "erroneous" => chinese ? "错误" : "Error",
        "insufficient_disk" => chinese ? "磁盘空间不足" : "Insufficient disk space",
        _ => status
    };

    private static string Format(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.##} {units[index]}";
    }

    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(FileName), nameof(Status), nameof(Progress), nameof(ProgressText), nameof(SpeedText), nameof(SourcesText), nameof(ActionText), nameof(IsPaused), nameof(IsFinished), nameof(CanControl) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
