using System.Text;

namespace C_toolsDddPlugin;

internal static class DddTextReplaceHelper
{
    internal static string ReplaceOrdinal(string? source, string oldValue, string? newValue)
    {
        var text = source ?? "";
        var find = oldValue ?? "";
        var replace = newValue ?? "";
        if (text.Length == 0 || find.Length == 0)
            return text;

        var start = 0;
        var hit = text.IndexOf(find, start, System.StringComparison.Ordinal);
        if (hit < 0)
            return text;

        var sb = new StringBuilder(text.Length + System.Math.Max(0, replace.Length - find.Length) * 2);
        while (hit >= 0)
        {
            sb.Append(text, start, hit - start);
            sb.Append(replace);
            start = hit + find.Length;
            hit = text.IndexOf(find, start, System.StringComparison.Ordinal);
        }

        sb.Append(text, start, text.Length - start);
        return sb.ToString();
    }
}
