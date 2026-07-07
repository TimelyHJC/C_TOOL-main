namespace C_toolsPlugin;

/// <summary>.NET Framework 无 <c>string.Contains(string, StringComparison)</c>，用 <see cref="string.IndexOf(string, StringComparison)"/> 兼容 net48。</summary>
internal static class StringSearchCompat
{
    internal static bool ContainsOrdinalIgnoreCase(string? haystack, string needle)
    {
        if (haystack == null || haystack.Length == 0 || needle.Length == 0)
            return false;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
