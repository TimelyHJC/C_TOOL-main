using System.Collections.Concurrent;
using System.Text;
using Microsoft.International.Converters.PinYinConverter;

namespace C_toolsShared;

public static class SearchTextCompat
{
    public const int NoMatchRank = int.MaxValue;

    private static readonly ConcurrentDictionary<string, SearchSignature> SignatureCache = new(StringComparer.Ordinal);

    public static bool ContainsFuzzy(string? haystack, string? needle)
    {
        return GetFuzzyMatchRank(haystack, needle) != NoMatchRank;
    }

    public static int GetFuzzyMatchRank(string? haystack, string? needle)
    {
        if (string.IsNullOrWhiteSpace(haystack))
            return NoMatchRank;
        if (string.IsNullOrWhiteSpace(needle))
            return NoMatchRank;

        var source = haystack!;
        var query = needle!.Trim();
        if (query.Length == 0)
            return NoMatchRank;

        if (source.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;

        var haystackSignature = GetSignature(source);
        var querySignature = BuildSignature(query);

        return new[]
            {
                KeyMatchRank(haystackSignature.CompactText, querySignature.CompactText, startsWithRank: 2, containsRank: 3, subsequenceRank: 12),
                KeyMatchRank(haystackSignature.InitialText, querySignature.InitialText, startsWithRank: 4, containsRank: 5, subsequenceRank: 10),
                KeyMatchRank(haystackSignature.PinyinText, querySignature.PinyinText, startsWithRank: 7, containsRank: 8, subsequenceRank: 13)
            }
            .Min();
    }

    private static SearchSignature GetSignature(string text) =>
        SignatureCache.GetOrAdd(text, BuildSignature);

    private static SearchSignature BuildSignature(string text)
    {
        var compact = new StringBuilder(text.Length);
        var pinyin = new StringBuilder(text.Length * 2);
        var initials = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (IsAsciiLetterOrDigit(ch))
            {
                var lower = char.ToLowerInvariant(ch);
                compact.Append(lower);
                pinyin.Append(lower);
                initials.Append(lower);
                continue;
            }

            foreach (var candidate in EnumeratePinyinCandidates(ch))
            {
                pinyin.Append(candidate);
                initials.Append(candidate[0]);
            }
        }

        return new SearchSignature(compact.ToString(), pinyin.ToString(), initials.ToString());
    }

    private static IEnumerable<string> EnumeratePinyinCandidates(char ch)
    {
        if (!ChineseChar.IsValidChar(ch))
            yield break;

        ChineseChar chineseChar;
        try
        {
            chineseChar = new ChineseChar(ch);
        }
        catch
        {
            yield break;
        }

        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pinyin in chineseChar.Pinyins)
        {
            var normalized = NormalizePinyin(pinyin);
            if (normalized.Length == 0 || !yielded.Add(normalized))
                continue;

            yield return normalized;
        }
    }

    private static string NormalizePinyin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var rawValue = value!;
        var builder = new StringBuilder(rawValue.Length);
        foreach (var ch in rawValue)
        {
            if (!IsAsciiLetter(ch))
                continue;

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static int KeyMatchRank(
        string haystackKey,
        string queryKey,
        int startsWithRank,
        int containsRank,
        int subsequenceRank)
    {
        if (haystackKey.Length == 0 || queryKey.Length == 0)
            return NoMatchRank;

        if (haystackKey.StartsWith(queryKey, StringComparison.OrdinalIgnoreCase))
            return startsWithRank;
        if (haystackKey.IndexOf(queryKey, StringComparison.OrdinalIgnoreCase) >= 0)
            return containsRank;
        return IsOrderedSubsequence(haystackKey, queryKey) ? subsequenceRank : NoMatchRank;
    }

    private static bool IsOrderedSubsequence(string haystackKey, string queryKey)
    {
        var queryIndex = 0;
        foreach (var ch in haystackKey)
        {
            if (char.ToLowerInvariant(ch) != char.ToLowerInvariant(queryKey[queryIndex]))
                continue;

            queryIndex++;
            if (queryIndex == queryKey.Length)
                return true;
        }

        return false;
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        (ch >= '0' && ch <= '9') || IsAsciiLetter(ch);

    private static bool IsAsciiLetter(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private sealed record SearchSignature(string CompactText, string PinyinText, string InitialText);
}
