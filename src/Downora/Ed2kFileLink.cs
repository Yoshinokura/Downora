namespace Downora;

public sealed record Ed2kFileLink(string FileName, long Size, string Hash)
{
    public static bool TryParse(string value, out Ed2kFileLink? link, out string error)
    {
        link = null;
        error = "Invalid eD2k file link.";

        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("ed2k://|file|", StringComparison.OrdinalIgnoreCase))
            return false;

        var fields = value.Split('|');
        if (fields.Length < 6 ||
            !fields[0].Equals("ed2k://", StringComparison.OrdinalIgnoreCase) ||
            !fields[1].Equals("file", StringComparison.OrdinalIgnoreCase) ||
            !fields[^1].Equals("/", StringComparison.Ordinal))
            return false;

        string name;
        try { name = Uri.UnescapeDataString(fields[2]); }
        catch (UriFormatException) { return false; }

        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The eD2k link contains an invalid file name.";
            return false;
        }

        if (!long.TryParse(fields[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0)
        {
            error = "The eD2k link contains an invalid file size.";
            return false;
        }

        var hash = fields[4];
        if (hash.Length != 32 || !hash.All(Uri.IsHexDigit))
        {
            error = "The eD2k file hash must contain exactly 32 hexadecimal characters.";
            return false;
        }

        // Optional source-server fields may follow the file hash; they are deliberately
        // not interpreted here. The parser only extracts the canonical file identity.
        link = new Ed2kFileLink(name, size, hash.ToLowerInvariant());
        error = string.Empty;
        return true;
    }
}
