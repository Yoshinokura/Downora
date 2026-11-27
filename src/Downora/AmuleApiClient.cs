using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Downora;

internal sealed class AmuleApiAuthenticationException : Exception
{
    public AmuleApiAuthenticationException(string message) : base(message) { }
}

internal static class AmuleApiClient
{
    internal static async Task AddLinkAsync(Uri baseAddress, string password, string link, CancellationToken token = default)
    {
        using var http = new HttpClient { BaseAddress = EnsureTrailingSlash(baseAddress), Timeout = TimeSpan.FromSeconds(20) };

        using var loginContent = new StringContent(
            JsonSerializer.Serialize(new { password }),
            Encoding.UTF8,
            "application/json");
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

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return new Uri(value.EndsWith('/') ? value : value + '/');
    }
}
