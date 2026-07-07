namespace C_toolsSetup;

internal static class ProductVersionComparer
{
    internal static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);

        if (a.IsValid && b.IsValid)
        {
            for (var i = 0; i < a.Parts.Length; i++)
            {
                var partComparison = a.Parts[i].CompareTo(b.Parts[i]);
                if (partComparison != 0)
                    return partComparison;
            }

            if (string.IsNullOrWhiteSpace(a.Prerelease) && !string.IsNullOrWhiteSpace(b.Prerelease))
                return 1;
            if (!string.IsNullOrWhiteSpace(a.Prerelease) && string.IsNullOrWhiteSpace(b.Prerelease))
                return -1;

            return string.Compare(a.Prerelease, b.Prerelease, StringComparison.OrdinalIgnoreCase);
        }

        if (a.IsValid != b.IsValid)
            return a.IsValid ? 1 : -1;

        return string.Compare(a.Raw, b.Raw, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeForDisplay(string? version)
    {
        var raw = (version ?? "").Trim();
        if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            raw = raw[1..];
        return raw.Length == 0 ? "0.0.0" : raw;
    }

    private static ParsedVersion Parse(string? version)
    {
        var raw = NormalizeForDisplay(version);
        var withoutMetadata = raw.Split('+', 2)[0];
        var coreAndPrerelease = withoutMetadata.Split('-', 2);
        var core = coreAndPrerelease[0];
        var prerelease = coreAndPrerelease.Length > 1 ? coreAndPrerelease[1] : "";

        var parts = new[] { 0, 0, 0, 0 };
        var rawParts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (rawParts.Length == 0 || rawParts.Length > parts.Length)
            return new ParsedVersion(false, parts, prerelease, raw);

        for (var i = 0; i < rawParts.Length; i++)
        {
            if (!int.TryParse(rawParts[i], out var value) || value < 0)
                return new ParsedVersion(false, parts, prerelease, raw);

            parts[i] = value;
        }

        return new ParsedVersion(true, parts, prerelease, raw);
    }

    private sealed record ParsedVersion(bool IsValid, int[] Parts, string Prerelease, string Raw);
}
