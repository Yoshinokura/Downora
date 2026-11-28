using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Downora;

public sealed record HlsSegment(Uri Uri, double DurationSeconds);

public sealed record HlsPlaylist(IReadOnlyList<HlsSegment> Segments, double TotalDurationSeconds)
{
    public static HlsPlaylist Parse(string content, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(playlistUri);
        if (playlistUri.Scheme is not ("http" or "https"))
            throw new NotSupportedException("HLS playlists must use HTTP or HTTPS.");

        var lines = content.Replace("\r", "").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0 || lines[0] != "#EXTM3U")
            throw new InvalidDataException("The URL did not return a valid M3U8 playlist.");

        var segments = new List<HlsSegment>();
        double? duration = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Master playlists are not supported yet. Use a media-playlist URL.");
            if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Encrypted HLS streams are not supported yet.");
            if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("HLS byte-range and fragmented MP4 playlists are not supported yet.");

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[8..].Split(',', 2)[0];
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                    !double.IsFinite(seconds) || seconds <= 0)
                    throw new InvalidDataException("The playlist contains an invalid EXTINF duration.");
                duration = seconds;
                continue;
            }

            if (line.StartsWith('#')) continue;
            if (duration is null)
                throw new InvalidDataException("A media segment is missing its EXTINF duration.");

            if (!Uri.TryCreate(playlistUri, line, out var segmentUri) ||
                segmentUri.Scheme is not ("http" or "https"))
                throw new InvalidDataException("The playlist contains an invalid or unsupported segment URL.");

            segments.Add(new HlsSegment(segmentUri, duration.Value));
            duration = null;
        }

        if (duration is not null)
            throw new InvalidDataException("The playlist ends before the final media segment URL.");
        if (segments.Count == 0)
            throw new InvalidDataException("The playlist does not contain any media segments.");
        if (!lines.Any(line => line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Live HLS playlists are not supported yet; use a completed VOD playlist.");

        return new HlsPlaylist(segments, segments.Sum(segment => segment.DurationSeconds));
    }

    /// <summary>Selects the highest-bandwidth HTTP(S) media variant from a master playlist.</summary>
    public static Uri SelectVariantPlaylist(string content, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(playlistUri);
        if (playlistUri.Scheme is not ("http" or "https"))
            throw new NotSupportedException("HLS playlists must use HTTP or HTTPS.");

        var lines = content.Replace("\r", "").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0 || lines[0] != "#EXTM3U")
            throw new InvalidDataException("The URL did not return a valid M3U8 playlist.");

        Uri? selected = null;
        long selectedBandwidth = -1;
        long? pendingBandwidth = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"(?:^|[:,])\s*BANDWIDTH=(?<value>\d+)", RegexOptions.IgnoreCase);
                if (!match.Success || !long.TryParse(match.Groups["value"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var bandwidth))
                    throw new InvalidDataException("An HLS variant is missing a valid BANDWIDTH value.");
                pendingBandwidth = bandwidth;
                continue;
            }

            if (line.StartsWith('#')) continue;
            if (pendingBandwidth is null) continue;
            if (!Uri.TryCreate(playlistUri, line, out var candidate) ||
                candidate.Scheme is not ("http" or "https"))
                throw new InvalidDataException("The master playlist contains an invalid variant URL.");
            if (pendingBandwidth.Value > selectedBandwidth)
            {
                selected = candidate;
                selectedBandwidth = pendingBandwidth.Value;
            }
            pendingBandwidth = null;
        }

        return selected ?? throw new InvalidDataException("The master playlist does not contain a usable media variant.");
    }

}

public static class HlsDownloader
{
    public static async Task DownloadAsync(
        HttpClient http,
        Uri playlistUri,
        string outputPath,
        IProgress<(int Completed, int Total, long Bytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (playlistUri.Scheme is not ("http" or "https"))
            throw new NotSupportedException("HLS playlists must use HTTP or HTTPS.");

        var mediaPlaylistUri = playlistUri;
        string content;
        for (var depth = 0; ; depth++)
        {
            using var playlistResponse = await http.GetAsync(mediaPlaylistUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            playlistResponse.EnsureSuccessStatusCode();
            content = await playlistResponse.Content.ReadAsStringAsync(cancellationToken);
            var hasVariants = content.Split('\n').Any(line =>
                line.TrimStart().StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase));
            if (!hasVariants) break;
            if (depth >= 3)
                throw new InvalidDataException("The HLS master playlist nesting limit was exceeded.");
            mediaPlaylistUri = HlsPlaylist.SelectVariantPlaylist(content, mediaPlaylistUri);
        }
        var playlist = HlsPlaylist.Parse(content, mediaPlaylistUri);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".hls-tmp";
        long totalBytes = 0;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                for (var index = 0; index < playlist.Segments.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var response = await GetSegmentWithRetryAsync(http, playlist.Segments[index].Uri, cancellationToken);
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        totalBytes += read;
                    }
                    progress?.Report((index + 1, playlist.Segments.Count, totalBytes));
                }
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, fullPath, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetSegmentWithRetryAsync(
        HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode) return response;
                var retry = (int)response.StatusCode == 408 || (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
                if (!retry || attempt >= 2)
                {
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                // Retry transient transport errors.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
        }
    }
}
