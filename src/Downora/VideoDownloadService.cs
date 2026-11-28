using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Downora;

public sealed record VideoPlatformInfo(
    string Title,
    string Uploader,
    double? DurationSeconds,
    string? Thumbnail,
    string Extractor,
    string VideoId);

public sealed record VideoDownloadProgress(double Percent, string? Speed, string? Eta, string? Message);

/// <summary>
/// Adapter around the official yt-dlp Windows binary. The binary is downloaded on first use
/// and verified against the SHA-256 digest published in the official GitHub release metadata.
/// </summary>
public sealed class VideoDownloadService : IDisposable
{
    public const string YtDlpVersion = "2026.11.23";
    public const string YtDlpSha256 = "622ed48023cac1daa52a3bf3a6ac38f03514de214b5f0fa069389fd0f6d17e5c";
    public const string YtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.11.23/yt-dlp.exe";
    public const string DenoVersion = "2.9.8";
    public const string DenoSha256 = "cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf";
    public const string DenoDownloadUrl = "https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip";

    private readonly string _toolsDirectory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public VideoDownloadService(string appDataDirectory, HttpClient? httpClient = null)
    {
        _toolsDirectory = Path.Combine(appDataDirectory, "Tools");
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public string ExecutablePath => Path.Combine(_toolsDirectory, "yt-dlp.exe");
    public string DenoPath => Path.Combine(_toolsDirectory, "deno.exe");

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    public static bool IsSupportedPlatformUrl(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        string[] domains =
        [
            "youtube.com", "youtu.be", "bilibili.com", "bilibili.tv", "b23.tv",
            "vimeo.com", "dailymotion.com", "twitch.tv",
            "douyin.com", "iesdouyin.com", "tiktok.com", "iqiyi.com", "iq.com",
            "youku.com", "mgtv.com", "acfun.cn", "ixigua.com", "sohu.com",
            "weibo.com", "weibo.cn", "douyu.com", "huya.com"
        ];
        return domains.Any(domain => host.Equals(domain, StringComparison.Ordinal) ||
                                     host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_toolsDirectory);
        if (File.Exists(ExecutablePath))
        {
            var existingHash = await ComputeSha256Async(ExecutablePath, cancellationToken);
            if (existingHash.Equals(YtDlpSha256, StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(ExecutablePath);
        }

        var temporary = ExecutablePath + ".download";
        try
        {
            using var response = await _http.GetAsync(YtDlpDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);

            var downloadedHash = await ComputeSha256Async(temporary, cancellationToken);
            if (!downloadedHash.Equals(YtDlpSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded yt-dlp binary failed SHA-256 verification.");

            File.Move(temporary, ExecutablePath, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    public async Task<VideoPlatformInfo> GetInfoAsync(Uri videoUrl, CancellationToken cancellationToken = default)
    {
        ValidateVideoUrl(videoUrl);
        await EnsureInstalledAsync(cancellationToken);
        if (IsYouTube(videoUrl)) await EnsureDenoInstalledAsync(cancellationToken);
        var args = BuildRuntimeArguments(videoUrl);
        args.AddRange(["--ignore-config", "--dump-single-json", "--skip-download", "--no-playlist", "--no-warnings", "--", videoUrl.AbsoluteUri]);
        var result = await RunAsync(args, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(LastUsefulLine(result.StandardError, result.StandardOutput));
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        var title = GetString(root, "title") ?? throw new InvalidDataException("The video platform did not return a title.");
        return new VideoPlatformInfo(
            title,
            GetString(root, "uploader") ?? GetString(root, "channel") ?? "",
            GetDouble(root, "duration"),
            GetString(root, "thumbnail"),
            GetString(root, "extractor_key") ?? GetString(root, "extractor") ?? "",
            GetString(root, "id") ?? "");
    }

    public async Task<string> DownloadAsync(
        Uri videoUrl,
        string outputPathWithoutExtension,
        IProgress<VideoDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateVideoUrl(videoUrl);
        await EnsureInstalledAsync(cancellationToken);
        if (IsYouTube(videoUrl)) await EnsureDenoInstalledAsync(cancellationToken);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPathWithoutExtension))!;
        Directory.CreateDirectory(directory);

        var args = BuildRuntimeArguments(videoUrl);
        args.AddRange([
            "--ignore-config", "--newline", "--progress", "--continue", "--no-playlist", "--no-warnings",
            "--format", "best[height<=1080]/best",
            "--print", "after_move:filepath",
            "--output", outputPathWithoutExtension + ".%(ext)s",
            "--", videoUrl.AbsoluteUri
        ]);
        var result = await RunAsync(args, cancellationToken, line => progress?.Report(ParseProgress(line)));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(LastUsefulLine(result.StandardError, result.StandardOutput));
        var finalPath = result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => File.Exists(line.Trim()))?.Trim();
        if (finalPath is null)
            throw new IOException("yt-dlp finished without reporting an output file.");
        return Path.GetFullPath(finalPath);
    }

    private List<string> BuildRuntimeArguments(Uri videoUrl)
    {
        var arguments = new List<string>();
        if (IsYouTube(videoUrl))
        {
            arguments.Add("--js-runtimes");
            arguments.Add("deno:" + DenoPath);
            arguments.Add("--remote-components");
            arguments.Add("ejs:github");
        }
        return arguments;
    }

    private static bool IsYouTube(Uri uri) =>
        uri.IdnHost.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);

    private async Task EnsureDenoInstalledAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(DenoPath)) return;

        var zipPath = Path.Combine(_toolsDirectory, "deno.zip.download");
        var extractPath = Path.Combine(_toolsDirectory, "deno-extract");
        try
        {
            using var response = await _http.GetAsync(DenoDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);

            var downloadedHash = await ComputeSha256Async(zipPath, cancellationToken);
            if (!downloadedHash.Equals(DenoSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded Deno runtime failed SHA-256 verification.");

            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            ZipFile.ExtractToDirectory(zipPath, extractPath, true);
            var extracted = Directory.GetFiles(extractPath, "deno.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (extracted is null) throw new InvalidDataException("The Deno archive did not contain deno.exe.");
            File.Move(extracted, DenoPath, true);
        }
        catch
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
            throw;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        }
    }

    private static void ValidateVideoUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsSupportedPlatformUrl(uri))
            throw new NotSupportedException("This video platform URL is not in the currently enabled platform list.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Credentials must not be embedded in video URLs.");
    }

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? outputLine = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _toolsDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start yt-dlp.");

        var stdout = new List<string>();
        var stderr = new List<string>();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, outputLine);
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, outputLine);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr));
    }

    private static async Task ReadLinesAsync(StreamReader reader, List<string> lines, Action<string>? callback)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (lines) lines.Add(line);
            callback?.Invoke(line);
        }
    }

    private static VideoDownloadProgress ParseProgress(string line)
    {
        var match = Regex.Match(line, @"^\[download\]\s+(?<percent>[\d.]+)%");
        var speed = Regex.Match(line, @"\bat\s+(?<speed>\S+)");
        var eta = Regex.Match(line, @"\bETA\s+(?<eta>\S+)");
        var percent = match.Success && double.TryParse(match.Groups["percent"].Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
        return new VideoDownloadProgress(percent,
            speed.Success ? speed.Groups["speed"].Value : null,
            eta.Success ? eta.Groups["eta"].Value : null, line);
    }

    private static string LastUsefulLine(string stderr, string stdout) =>
        (stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
         ?? stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
         ?? "yt-dlp failed to download this video.").Trim();

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number : null;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
