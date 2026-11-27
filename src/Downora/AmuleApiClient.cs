using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Downora;

internal sealed class AmuleApiAuthenticationException : Exception
{
    public AmuleApiAuthenticationException(string message) : base(message) { }
}

internal sealed record AmuleDownload(
    string Hash,
    string Name,
    long SizeBytes,
    long CompletedBytes,
    long SpeedBytesPerSecond,
    double ProgressPercent,
    string Status,
    int SourceCount);

internal static class AmuleApiClient
{
    internal static async Task AddLinkAsync(Uri baseAddress, string password, string link, CancellationToken token = default)
    {
        using var http = await CreateAuthenticatedClientAsync(baseAddress, password, token);
        using var body = new StringContent(
            JsonSerializer.Serialize(new { links = new[] { link } }),
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("api/v1/downloads", body, token);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is 0) return;
        var responseText = await response.Content.ReadAsStringAsync(token);
        if (string.IsNullOrWhiteSpace(responseText)) return;

        using var result = JsonDocument.Parse(responseText);
        if (result.RootElement.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    var message = item.TryGetProperty("error", out var error) &&
                                  error.TryGetProperty("message", out var text)
                        ? text.GetString()
                        : null;
                    throw new InvalidOperationException(message ?? "aMule rejected the eD2k link.");
                }
            }
        }
    }

    internal static async Task<IReadOnlyList<AmuleDownload>> GetDownloadsAsync(
        Uri baseAddress, string password, CancellationToken token = default)
    {
        using var http = await CreateAuthenticatedClientAsync(baseAddress, password, token);
        using var response = await http.GetAsync("api/v1/downloads?status=all", token);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!document.RootElement.TryGetProperty("downloads", out var downloads) ||
            downloads.ValueKind != JsonValueKind.Array)
            return Array.Empty<AmuleDownload>();

        var result = new List<AmuleDownload>();
        foreach (var item in downloads.EnumerateArray())
        {
            var hash = ReadString(item, "hash");
            if (hash.Length != 32) continue;

            var progress = item.TryGetProperty("progress", out var progressElement)
                ? ReadDouble(progressElement, "percent")
                : 0;
            var sources = item.TryGetProperty("sources", out var sourcesElement)
                ? ReadInt(sourcesElement, "total")
                : 0;

            result.Add(new AmuleDownload(
                hash,
                ReadString(item, "name"),
                ReadLong(item, "size_bytes"),
                ReadLong(item, "completed_bytes"),
                ReadLong(item, "speed_bytes_per_second"),
                Math.Clamp(progress, 0, 100),
                ReadString(item, "status"),
                sources));
        }

        return result;
    }

    internal static async Task SetDownloadActionAsync(
        Uri baseAddress, string password, string hash, string action, CancellationToken token = default)
    {
        if (action is not ("pause" or "resume"))
            throw new ArgumentOutOfRangeException(nameof(action));

        using var http = await CreateAuthenticatedClientAsync(baseAddress, password, token);
        using var body = new StringContent(JsonSerializer.Serialize(new { action }), Encoding.UTF8, "application/json");
        using var response = await http.PatchAsync($"api/v1/downloads/{Uri.EscapeDataString(hash)}", body, token);
        response.EnsureSuccessStatusCode();
    }

    internal static async Task RemoveDownloadAsync(
        Uri baseAddress, string password, string hash, CancellationToken token = default)
    {
        using var http = await CreateAuthenticatedClientAsync(baseAddress, password, token);
        using var response = await http.DeleteAsync($"api/v1/downloads/{Uri.EscapeDataString(hash)}", token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        Uri baseAddress, string password, CancellationToken token)
    {
        var http = new HttpClient { BaseAddress = EnsureTrailingSlash(baseAddress), Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var loginContent = new StringContent(
                JsonSerializer.Serialize(new { password }), Encoding.UTF8, "application/json");
            using var login = await http.PostAsync("api/v1/auth/login?include_token=true", loginContent, token);
            if (login.StatusCode == HttpStatusCode.Unauthorized)
                throw new AmuleApiAuthenticationException("The aMule API password was rejected.");
            login.EnsureSuccessStatusCode();

            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync(token));
            if (!loginJson.RootElement.TryGetProperty("token", out var tokenElement) ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                throw new InvalidDataException("The aMule API did not return an access token.");

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            return http;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static double ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return new Uri(value.EndsWith('/') ? value : value + '/');
    }
}