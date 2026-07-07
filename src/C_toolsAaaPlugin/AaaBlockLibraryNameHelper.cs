using System.Text.RegularExpressions;

namespace C_toolsAaaPlugin;

internal static class AaaBlockLibraryNameHelper
{
    private static readonly Regex TimestampSuffixRegex = new(
        @"^(?<device>.+?)-(?<stamp>\d{8,17})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string GetDeviceName(string? displayName)
    {
        var trimmed = (displayName ?? "").Trim();
        if (trimmed.Length == 0)
            return "";

        var match = TimestampSuffixRegex.Match(trimmed);
        if (!match.Success)
            return trimmed;

        var device = match.Groups["device"].Value.Trim();
        return device.Length == 0 ? trimmed : device;
    }
}
