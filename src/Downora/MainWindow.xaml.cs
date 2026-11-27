using System.Collections.ObjectModel;
using System.ComponentModel;
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

namespace Downora;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _items = new();
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
        Directory.CreateDirectory(_root);
        LoadQueue();
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        _chinese = !_chinese;
        Subtitle.Text = _chinese ? "更简单的下载方式" : "A simpler way to download";
        UrlBox.ToolTip = _chinese ? "粘贴 HTTP、HTTPS、FTP、FTPS 或 eD2k 链接" : "Paste an HTTP, HTTPS, FTP, FTPS or eD2k link";
        QueueTitle.Text = _chinese ? "下载任务" : "Downloads";
        FooterText.Text = _chinese ? "就绪" : "Ready";
        EngineText.Text = _chinese ? "HTTP / HTTPS / FTP / FTPS / eD2k 链接解析" : "HTTP / HTTPS / FTP / FTPS / eD2k link parsing";
        SettingsButton.Content = _chinese ? "设置" : "Settings";
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
        if (raw.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase))
        {
            if (Ed2kFileLink.TryParse(raw, out var ed2k, out var parseError))
            {
                var details = _chinese
                    ? $"已识别 eD2k 文件链接\n文件：{ed2k!.FileName}\n大小：{FormatSize(ed2k.Size)}\nMD4 哈希：{ed2k.Hash}\n\n当前版本尚未接入 eD2k 网络引擎，暂时不能开始传输。"
                    : $"eD2k file link recognized\nFile: {ed2k!.FileName}\nSize: {FormatSize(ed2k.Size)}\nMD4 hash: {ed2k.Hash}\n\nThe eD2k network engine is not integrated yet, so this file cannot be transferred in this build.";
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
                        ? $"eD2k 任务已提交给本机 aMule 引擎。\\n\\n{ed2k!.FileName}"
                        : $"The eD2k task was submitted to the local aMule engine.\\n\\n{ed2k!.FileName}",
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
                        ? "无法连接本机 aMule API。请安装并启动 aMule 3.1.0 或更新版本，并启用 amuleapi（默认端口 4713）。\\n\\n"
                        : "Could not reach the local aMule API. Install and start aMule 3.1.0 or newer and enable amuleapi (default port 4713).\\n\\n")
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

        try { await Task.WhenAll(_downloadTasks.ToArray()); } catch { }
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
