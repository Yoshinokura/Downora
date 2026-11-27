using System.Text;

namespace Downora;

public sealed record MagnetLink(string InfoHash, string? DisplayName, IReadOnlyList<Uri> Trackers)
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static bool TryParse(string value, out MagnetLink? magnet, out string error)
    {
        magnet = null;
        error = "Invalid BitTorrent magnet link.";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            return false;

        var queryStart = value.IndexOf('?');
        var query = queryStart >= 0 ? value[(queryStart + 1)..] : string.Empty;
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0) query = query[..fragmentStart];
        var parameters = ParseQuery(query);
        if (!parameters.TryGetValue("xt", out var exactTopics))
        {
            error = "The magnet link does not contain an exact topic (xt).";
            return false;
        }

        string? infoHash = null;
        foreach (var topic in exactTopics)
        {
            const string prefix = "urn:btih";
            if (!topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                topic.Length <= prefix.Length ||
                topic[prefix.Length] is not (':' or '=')) continue;
            var candidate = topic[(prefix.Length + 1)..].Trim();
            if (TryNormalizeInfoHash(candidate, out infoHash)) break;
        }

        if (infoHash is null)
        {
            error = "No valid btih topic found. Parsed xt values: " + string.Join(" | ", exactTopics.Select(topic => $"{topic} (length {topic.Length})"));
            return false;
        }

        var displayName = parameters.TryGetValue("dn", out var names)
            ? names.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            : null;
        var trackers = parameters.TryGetValue("tr", out var trackerValues)
            ? trackerValues.Select(x => Uri.TryCreate(x, UriKind.Absolute, out var tracker) &&
                                        tracker.Scheme is "http" or "https" or "udp"
                    ? tracker : null)
                .Where(x => x is not null).Cast<Uri>().Distinct().ToArray()
            : Array.Empty<Uri>();

        magnet = new MagnetLink(infoHash, displayName, trackers);
        error = string.Empty;
        return true;
    }

    private static Dictionary<string, List<string>> ParseQuery(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Decode(separator < 0 ? part : part[..separator]);
            var value = Decode(separator < 0 ? string.Empty : part[(separator + 1)..]);
            if (!result.TryGetValue(key, out var values))
                result[key] = values = new List<string>();
            values.Add(value);
        }
        return result;
    }

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));

    private static bool TryNormalizeInfoHash(string value, out string? normalized)
    {
        normalized = null;
        if (value.Length == 40 && value.All(Uri.IsHexDigit))
        {
            normalized = value.ToLowerInvariant();
            return true;
        }

        if (value.Length != 32) return false;
        var bitStream = new StringBuilder(160);
        foreach (var character in value.ToUpperInvariant())
        {
            var index = Base32Alphabet.IndexOf(character);
            if (index < 0) return false;
            bitStream.Append(Convert.ToString(index, 2).PadLeft(5, '0'));
        }

        if (bitStream.Length != 160) return false;
        var bytes = new byte[20];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(bitStream.ToString(i * 8, 8), 2);

        normalized = Convert.ToHexString(bytes).ToLowerInvariant();
        return true;
    }
}
