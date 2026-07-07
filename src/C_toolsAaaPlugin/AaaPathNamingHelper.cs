using System.IO;
using System.Text;

namespace C_toolsAaaPlugin;

internal static class AaaPathNamingHelper
{
    internal static string BuildUniqueDwgFilePath(
        string targetFolder,
        string displayName,
        string fallbackStem,
        ISet<string>? reservedPaths = null)
    {
        return BuildUniquePath(
            targetFolder,
            displayName,
            fallbackStem,
            ".dwg",
            reservedPaths,
            path => File.Exists(path));
    }

    internal static string BuildUniqueDirectoryPath(
        string targetFolder,
        string displayName,
        string fallbackStem,
        string directorySuffix,
        ISet<string>? reservedPaths = null)
    {
        return BuildUniquePath(
            targetFolder,
            displayName,
            fallbackStem,
            directorySuffix,
            reservedPaths,
            path => Directory.Exists(path));
    }

    internal static string SanitizeStem(string? name, string fallbackStem)
    {
        var fallback = SanitizeRawStem(fallbackStem, "item");
        var result = SanitizeRawStem(name, fallback);
        if (result.Length > 64)
            result = result[..64].TrimEnd('_', '.', ' ');
        return result.Length == 0 ? fallback : result;
    }

    private static string BuildUniquePath(
        string targetFolder,
        string displayName,
        string fallbackStem,
        string suffix,
        ISet<string>? reservedPaths,
        Func<string, bool> exists)
    {
        var safeStem = SanitizeStem(displayName, fallbackStem);
        var preferred = Path.Combine(targetFolder, safeStem + suffix);
        if (!exists(preferred) && !(reservedPaths?.Contains(preferred) ?? false))
            return preferred;

        var safeFallback = SanitizeStem(fallbackStem, "item");
        var withFallback = Path.Combine(targetFolder, $"{safeStem}_{safeFallback}{suffix}");
        if (!exists(withFallback) && !(reservedPaths?.Contains(withFallback) ?? false))
            return withFallback;

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(targetFolder, $"{safeStem}_{index}{suffix}");
            if (!exists(candidate) && !(reservedPaths?.Contains(candidate) ?? false))
                return candidate;
        }
    }

    private static string SanitizeRawStem(string? value, string fallbackStem)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            trimmed = fallbackStem;

        var builder = new StringBuilder(trimmed.Length);
        var previousUnderscore = false;
        foreach (var ch in trimmed)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!previousUnderscore && builder.Length > 0)
                    builder.Append('_');
                previousUnderscore = true;
                continue;
            }

            builder.Append(ch);
            previousUnderscore = false;
        }

        var result = builder.ToString().Trim('_', '.', ' ');
        return result.Length == 0 ? fallbackStem : result;
    }
}
