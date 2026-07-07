namespace C_toolsPlugin;

/// <summary>ASCII 字母/数字判断；net48（AutoCAD 2024）无 BCL 的 IsAscii*，与 net8 共用本实现。</summary>
internal static class CharAsciiCompat
{
    internal static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    internal static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || (c >= '0' && c <= '9');
}
