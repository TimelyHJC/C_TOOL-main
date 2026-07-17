using System.Globalization;
using System.Text.RegularExpressions;

namespace C_toolsPlugin;

internal sealed class AnnotationScaleSnapshot
{
    internal static AnnotationScaleSnapshot Empty { get; } =
        new(Array.Empty<AnnotationScaleGroupInfo>(), Array.Empty<AnnotationScaleListItem>(), "");

    internal AnnotationScaleSnapshot(
        IReadOnlyList<AnnotationScaleGroupInfo> groups,
        IReadOnlyList<AnnotationScaleListItem> allScales,
        string currentScaleName,
        string currentDimStyleName = "")
    {
        Groups = groups;
        AllScales = allScales;
        CurrentScaleName = currentScaleName ?? "";
        CurrentDimStyleName = currentDimStyleName ?? "";
    }

    internal IReadOnlyList<AnnotationScaleGroupInfo> Groups { get; }

    internal IReadOnlyList<AnnotationScaleListItem> AllScales { get; }

    internal string CurrentScaleName { get; }

    internal string CurrentDimStyleName { get; }

    internal string? CurrentGroupPrefix => AnnotationScaleGrouping.GetBasePrefix(CurrentDimStyleName);

    internal bool? CurrentPrefersInnerDimStyle =>
        string.IsNullOrWhiteSpace(CurrentDimStyleName)
            ? null
            : AnnotationScaleGrouping.HasInnerSuffix(CurrentDimStyleName);
}

internal sealed class AnnotationScaleListItem
{
    internal AnnotationScaleListItem(string name, double paperUnits, double drawingUnits)
    {
        Name = name ?? "";
        PaperUnits = paperUnits;
        DrawingUnits = drawingUnits;
    }

    public string Name { get; }

    public double PaperUnits { get; }

    public double DrawingUnits { get; }

    public double ScaleRatio =>
        PaperUnits > 0.0 && !double.IsNaN(PaperUnits) && !double.IsInfinity(PaperUnits)
            ? DrawingUnits / PaperUnits
            : double.NaN;

    public string RatioDisplay
    {
        get
        {
            if (PaperUnits <= 0.0 || DrawingUnits <= 0.0 ||
                double.IsNaN(PaperUnits) || double.IsInfinity(PaperUnits) ||
                double.IsNaN(DrawingUnits) || double.IsInfinity(DrawingUnits))
                return "比例信息不可用";

            return $"{FormatUnit(PaperUnits)}:{FormatUnit(DrawingUnits)}";
        }
    }

    public string ListDisplay => RatioDisplay;

    internal string LookupKey => NormalizeLookupKey(Name);

    internal string RatioLookupKey => NormalizeLookupKey(RatioDisplay);

    internal string DedupKey => RatioLookupKey.Length > 0 ? RatioLookupKey : LookupKey;

    internal bool MatchesScale(string? scaleName)
    {
        var key = NormalizeLookupKey(scaleName);
        if (key.Length == 0)
            return false;

        return string.Equals(LookupKey, key, StringComparison.OrdinalIgnoreCase)
               || string.Equals(RatioLookupKey, key, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatUnit(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    internal static string NormalizeLookupKey(string? text)
    {
        var raw = text ?? "";
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var buffer = new char[raw.Length];
        var writeIndex = 0;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            buffer[writeIndex++] = ch;
        }

        return new string(buffer, 0, writeIndex);
    }
}

internal sealed class AnnotationScaleGroupInfo
{
    internal AnnotationScaleGroupInfo(
        string prefix,
        IReadOnlyList<AnnotationScaleListItem> normalScales,
        IReadOnlyList<AnnotationScaleListItem> innerScales)
    {
        Prefix = prefix;
        NormalScales = normalScales;
        InnerScales = innerScales;
    }

    public string Prefix { get; }

    public IReadOnlyList<AnnotationScaleListItem> NormalScales { get; }

    public IReadOnlyList<AnnotationScaleListItem> InnerScales { get; }

    public int TotalCount => NormalScales.Count + InnerScales.Count;

    public string DisplayLabel => $"{Prefix} ({TotalCount}个)";

    public override string ToString() => DisplayLabel;
}

internal static class AnnotationScaleGrouping
{
    private const string UnmappedGroupPrefix = "未应用";
    private static readonly Regex s_ratioPattern = new(
        @"\d+(?:\.\d+)?\s*:\s*\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_styleScalePattern = new(
        @"^.{1,2}\s*-\s*(?<denominator>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static List<AnnotationScaleGroupInfo> ListGroups(
        IReadOnlyList<AnnotationScaleListItem> scales,
        IReadOnlyList<string> dimStyleNames)
    {
        var groups = BuildGroupsFromDimStyles(scales, dimStyleNames, out var mappedScaleKeys);
        if (groups.Count > 0)
        {
            AppendUnmappedScaleGroup(scales, mappedScaleKeys, groups);
            return groups;
        }

        return BuildFallbackGroups(scales);
    }

    internal static string? NormalizeGroupPrefix(string? prefix)
    {
        var trimmed = prefix?.Trim() ?? "";
        if (trimmed.Length == 0)
            return null;

        return HasInnerGroupSuffix(trimmed)
            ? trimmed.Substring(0, trimmed.Length - 1)
            : trimmed;
    }

    private static List<AnnotationScaleGroupInfo> BuildGroupsFromDimStyles(
        IReadOnlyList<AnnotationScaleListItem> scales,
        IReadOnlyList<string> dimStyleNames,
        out HashSet<string> mappedScaleKeys)
    {
        mappedScaleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, ScaleGroupBucket>(StringComparer.OrdinalIgnoreCase);
        if (scales.Count == 0 || dimStyleNames.Count == 0)
            return new List<AnnotationScaleGroupInfo>();

        var scaleLookup = BuildScaleLookup(scales);
        foreach (var styleName in dimStyleNames)
        {
            if (string.IsNullOrWhiteSpace(styleName))
                continue;

            var basePrefix = GetBasePrefix(styleName);
            if (basePrefix == null)
                continue;
            if (!TryExtractRatioToken(styleName, out var ratioToken))
                continue;

            var scale = ResolveScale(scaleLookup, ratioToken);
            if (scale == null)
                continue;

            if (!map.TryGetValue(basePrefix, out var bucket))
            {
                bucket = new ScaleGroupBucket();
                map[basePrefix] = bucket;
            }

            if (HasInnerSuffix(styleName))
                bucket.AddInner(scale);
            else
                bucket.AddNormal(scale);

            var scaleKey = GetScaleKey(scale);
            if (scaleKey.Length > 0)
                mappedScaleKeys.Add(scaleKey);
        }

        return OrderGroups(map);
    }

    private static void AppendUnmappedScaleGroup(
        IReadOnlyList<AnnotationScaleListItem> scales,
        HashSet<string> mappedScaleKeys,
        List<AnnotationScaleGroupInfo> groups)
    {
        if (scales.Count == 0)
            return;

        var orderedScales = scales
            .OrderBy(scale => scale, AnnotationScaleListItemComparer.Instance)
            .ToList();
        var normal = new List<AnnotationScaleListItem>();
        var inner = new List<AnnotationScaleListItem>();
        var appendedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scale in orderedScales)
        {
            var key = GetScaleKey(scale);
            if (key.Length == 0)
                continue;
            if (mappedScaleKeys.Contains(key))
                continue;
            if (!appendedKeys.Add(key))
                continue;

            if (HasInnerSuffix(scale.Name))
                inner.Add(scale);
            else
                normal.Add(scale);
        }

        if (normal.Count == 0 && inner.Count == 0)
            return;

        groups.Add(new AnnotationScaleGroupInfo(UnmappedGroupPrefix, normal, inner));
    }

    internal static string? GetBasePrefix(string? scaleName)
    {
        var trimmed = scaleName?.Trim() ?? "";
        if (trimmed.Length == 0)
            return null;

        return trimmed.Length >= 2 ? trimmed.Substring(0, 2) : trimmed.Substring(0, 1);
    }

    private static List<AnnotationScaleGroupInfo> BuildFallbackGroups(IReadOnlyList<AnnotationScaleListItem> scales)
    {
        if (scales.Count == 0)
            return new List<AnnotationScaleGroupInfo>();

        if (scales.All(scale => LooksLikeRatio(scale.Name)))
        {
            var fallbackPrefix = NormalizeGroupPrefix(AnnotationScaleLastGroupStore.TryGetPrefix()) ?? "全部";
            var ordered = scales.OrderBy(scale => scale, AnnotationScaleListItemComparer.Instance).ToList();
            return new List<AnnotationScaleGroupInfo>
            {
                new(fallbackPrefix, ordered, Array.Empty<AnnotationScaleListItem>())
            };
        }

        var map = new Dictionary<string, ScaleGroupBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var scale in scales)
        {
            if (string.IsNullOrWhiteSpace(scale.Name))
                continue;

            var basePrefix = GetBasePrefix(scale.Name);
            if (basePrefix == null)
                continue;

            if (!map.TryGetValue(basePrefix, out var bucket))
            {
                bucket = new ScaleGroupBucket();
                map[basePrefix] = bucket;
            }

            if (HasInnerSuffix(scale.Name))
                bucket.AddInner(scale);
            else
                bucket.AddNormal(scale);
        }

        return OrderGroups(map);
    }

    private static List<AnnotationScaleGroupInfo> OrderGroups(
        Dictionary<string, ScaleGroupBucket> map)
    {
        foreach (var bucket in map.Values)
            bucket.Sort();

        return map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new AnnotationScaleGroupInfo(kv.Key, kv.Value.NormalScales, kv.Value.InnerScales))
            .ToList();
    }

    private static Dictionary<string, AnnotationScaleListItem> BuildScaleLookup(
        IReadOnlyList<AnnotationScaleListItem> scales)
    {
        var lookup = new Dictionary<string, AnnotationScaleListItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var scale in scales)
        {
            AddLookup(lookup, scale.LookupKey, scale);
            AddLookup(lookup, scale.RatioLookupKey, scale);
        }

        return lookup;
    }

    private static void AddLookup(
        Dictionary<string, AnnotationScaleListItem> lookup,
        string key,
        AnnotationScaleListItem scale)
    {
        if (key.Length == 0 || lookup.ContainsKey(key))
            return;

        lookup[key] = scale;
    }

    private static AnnotationScaleListItem? ResolveScale(
        Dictionary<string, AnnotationScaleListItem> lookup,
        string ratioToken)
    {
        var key = AnnotationScaleListItem.NormalizeLookupKey(ratioToken);
        return key.Length > 0 && lookup.TryGetValue(key, out var scale)
            ? scale
            : null;
    }

    private static string GetScaleKey(AnnotationScaleListItem scale)
    {
        if (scale.DedupKey.Length > 0)
            return scale.DedupKey;
        if (scale.LookupKey.Length > 0)
            return scale.LookupKey;
        return scale.Name ?? "";
    }

    private static bool TryExtractRatioToken(string styleName, out string ratioToken)
    {
        ratioToken = "";
        var trimmed = styleName?.Trim() ?? "";
        if (trimmed.Length == 0)
            return false;

        var match = s_ratioPattern.Match(trimmed);
        if (match.Success)
        {
            ratioToken = AnnotationScaleListItem.NormalizeLookupKey(match.Value);
            return ratioToken.Length > 0;
        }

        match = s_styleScalePattern.Match(trimmed);
        if (!match.Success)
            return false;

        var denominator = match.Groups["denominator"].Value;
        if (denominator.Length == 0)
            return false;

        ratioToken = AnnotationScaleListItem.NormalizeLookupKey("1:" + denominator);
        return ratioToken.Length > 0;
    }

    private static bool LooksLikeRatio(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return false;

        var match = s_ratioPattern.Match(trimmed);
        return match.Success && match.Value == trimmed;
    }

    internal static bool HasInnerSuffix(string scaleName)
    {
        var trimmed = scaleName?.Trim() ?? "";
        if (trimmed.Length == 0)
            return false;

        var firstDash = trimmed.IndexOf('-');
        if (firstDash < 0)
            return trimmed.EndsWith("内", StringComparison.Ordinal);

        if (firstDash >= trimmed.Length - 1)
            return false;

        var secondDash = trimmed.IndexOf('-', firstDash + 1);
        var segmentEnd = secondDash >= 0 ? secondDash : trimmed.Length;
        return segmentEnd > firstDash + 1 && trimmed[segmentEnd - 1] == '内';
    }

    private static bool HasInnerGroupSuffix(string groupPrefix)
    {
        return groupPrefix.Length > 1 && groupPrefix[^1] == '内';
    }

    private sealed class ScaleGroupBucket
    {
        private readonly HashSet<string> _normalKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _innerKeys = new(StringComparer.OrdinalIgnoreCase);

        internal List<AnnotationScaleListItem> NormalScales { get; } = new();
        internal List<AnnotationScaleListItem> InnerScales { get; } = new();

        internal void AddNormal(AnnotationScaleListItem scale) => Add(scale, NormalScales, _normalKeys);

        internal void AddInner(AnnotationScaleListItem scale) => Add(scale, InnerScales, _innerKeys);

        internal void Sort()
        {
            NormalScales.Sort(AnnotationScaleListItemComparer.Instance);
            InnerScales.Sort(AnnotationScaleListItemComparer.Instance);
        }

        private static void Add(
            AnnotationScaleListItem scale,
            List<AnnotationScaleListItem> target,
            HashSet<string> keys)
        {
            var key = scale.DedupKey;
            if (key.Length == 0 || !keys.Add(key))
                return;

            target.Add(scale);
        }
    }

    private sealed class AnnotationScaleListItemComparer : IComparer<AnnotationScaleListItem>
    {
        internal static AnnotationScaleListItemComparer Instance { get; } = new();

        public int Compare(AnnotationScaleListItem? left, AnnotationScaleListItem? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left == null)
                return 1;
            if (right == null)
                return -1;

            var leftValid = !double.IsNaN(left.ScaleRatio) && !double.IsInfinity(left.ScaleRatio);
            var rightValid = !double.IsNaN(right.ScaleRatio) && !double.IsInfinity(right.ScaleRatio);
            if (leftValid != rightValid)
                return leftValid ? -1 : 1;
            if (leftValid && rightValid)
            {
                var ratioCompare = left.ScaleRatio.CompareTo(right.ScaleRatio);
                if (ratioCompare != 0)
                    return ratioCompare;
            }

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
