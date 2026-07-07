using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal sealed class BbbDynamicBlockStateInfo
{
    internal string DisplayText { get; set; } = "";
    internal string TagKey { get; set; } = "";
    internal bool HasState => TagKey.Length > 0;
}

internal sealed class BbbManagedDeviceNameEntry
{
    internal int SlotIndex { get; set; }
    internal string Name { get; set; } = "";
    internal string Tag { get; set; } = "";
}

internal sealed class BbbManagedDeviceNameGroup
{
    internal BbbDynamicBlockStateInfo StateInfo { get; set; } = new();
    internal bool UsesLegacyTags { get; set; }
    internal List<BbbManagedDeviceNameEntry> Entries { get; } = new();
}

internal sealed class BbbManagedTagInfo
{
    internal bool IsLegacy { get; set; }
    internal int SlotIndex { get; set; }
    internal string StateTagKey { get; set; } = "";
}

internal sealed class BbbDynamicStatePart
{
    internal int Priority { get; set; }
    internal string Key { get; set; } = "";
    internal string SourceName { get; set; } = "";
    internal string DisplayLabel { get; set; } = "";
    internal string Display { get; set; } = "";
    internal string TagValue { get; set; } = "";
    internal string TagPart { get; set; } = "";
}

internal static class BbbDynamicBlockStateHelper
{
    private const string StateTagPrefix = "BXR_NAME_";
    private const string StateTagMarker = "__S_";
    private const string LegacyBaseTag = BbbHiddenDeviceNameConfigStore.DefaultBaseTag;

    internal static BbbDynamicBlockStateInfo GetStateInfo(BlockReference blockReference)
    {
        if (!blockReference.IsDynamicBlock)
        {
            return new BbbDynamicBlockStateInfo
            {
                DisplayText = "普通块",
                TagKey = ""
            };
        }

        var parts = new List<BbbDynamicStatePart>();

        try
        {
            foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
            {
                if (!TryBuildStatePart(property, out var part))
                    continue;

                if (parts.Any(x =>
                        x.Key == part.Key &&
                        x.SourceName == part.SourceName &&
                        string.Equals(x.TagValue, part.TagValue, StringComparison.OrdinalIgnoreCase)))
                    continue;

                parts.Add(part);
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取动态块状态键失败", ex);
        }

        if (parts.Count == 0)
        {
            return new BbbDynamicBlockStateInfo
            {
                DisplayText = "未识别",
                TagKey = ""
            };
        }

        parts = parts
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ThenBy(x => x.SourceName, StringComparer.Ordinal)
            .ThenBy(x => x.TagValue, StringComparer.Ordinal)
            .ToList();

        var displayText = parts.Count == 1 && string.Equals(parts[0].Key, "VIS", StringComparison.Ordinal)
            ? parts[0].Display
            : string.Join(" | ", parts.Select(x => $"{x.DisplayLabel}={x.Display}"));

        var tagKey = NormalizeStateTagKey(string.Join("__", parts.Select(x => x.TagPart)));
        return new BbbDynamicBlockStateInfo
        {
            DisplayText = displayText,
            TagKey = tagKey
        };
    }

    internal static BbbManagedDeviceNameGroup ReadCurrentManagedNames(BlockReference blockReference, Transaction transaction)
    {
        var stateInfo = GetStateInfo(blockReference);
        var currentStateEntries = new Dictionary<int, BbbManagedDeviceNameEntry>();
        var legacyEntries = new Dictionary<int, BbbManagedDeviceNameEntry>();
        var stateEntriesByKey = new Dictionary<string, Dictionary<int, BbbManagedDeviceNameEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var attributeReference in ReadManagedAttributes(blockReference, transaction, OpenMode.ForRead))
        {
            if (!TryParseManagedTag(attributeReference.Tag, out var tagInfo))
                continue;

            var value = GetAttributeValue(attributeReference).Trim();
            if (value.Length == 0)
                continue;

            var entry = new BbbManagedDeviceNameEntry
            {
                SlotIndex = tagInfo.SlotIndex,
                Name = value,
                Tag = attributeReference.Tag ?? ""
            };

            if (!tagInfo.IsLegacy)
            {
                if (!stateEntriesByKey.TryGetValue(tagInfo.StateTagKey, out var stateBucket))
                {
                    stateBucket = new Dictionary<int, BbbManagedDeviceNameEntry>();
                    stateEntriesByKey[tagInfo.StateTagKey] = stateBucket;
                }

                if (!stateBucket.ContainsKey(tagInfo.SlotIndex))
                    stateBucket[tagInfo.SlotIndex] = entry;

                if (stateInfo.HasState && string.Equals(tagInfo.StateTagKey, stateInfo.TagKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (!currentStateEntries.ContainsKey(tagInfo.SlotIndex))
                        currentStateEntries[tagInfo.SlotIndex] = entry;
                }

                continue;
            }

            if (tagInfo.IsLegacy && !legacyEntries.ContainsKey(tagInfo.SlotIndex))
                legacyEntries[tagInfo.SlotIndex] = entry;
        }

        var selectedEntries = currentStateEntries;
        var usesLegacyTags = false;
        if (selectedEntries.Count == 0 &&
            stateInfo.HasState &&
            TryFindEquivalentStateEntries(stateInfo.TagKey, stateEntriesByKey, out var equivalentEntries))
        {
            selectedEntries = equivalentEntries;
        }

        if (selectedEntries.Count == 0)
        {
            selectedEntries = legacyEntries;
            usesLegacyTags = legacyEntries.Count > 0;
        }

        var group = new BbbManagedDeviceNameGroup
        {
            StateInfo = stateInfo,
            UsesLegacyTags = usesLegacyTags
        };

        foreach (var entry in selectedEntries.OrderBy(x => x.Key).Select(x => x.Value))
            group.Entries.Add(entry);

        return group;
    }

    internal static List<AttributeReference> GetManagedAttributesForWrite(
        BlockReference blockReference,
        Transaction transaction,
        BbbDynamicBlockStateInfo stateInfo)
    {
        return ReadManagedAttributes(blockReference, transaction, OpenMode.ForWrite)
            .Where(x => TryParseManagedTag(x.Tag, out var tagInfo) && IsTagInCurrentWriteScope(tagInfo, stateInfo))
            .OrderBy(x => GetManagedTagOrder(x.Tag))
            .ToList();
    }

    internal static string BuildManagedTag(int slotIndex, BbbDynamicBlockStateInfo stateInfo)
    {
        return stateInfo.HasState
            ? $"{StateTagPrefix}{slotIndex}__S_{stateInfo.TagKey}"
            : BuildLegacyManagedTag(slotIndex);
    }

    internal static string BuildManagedIdentityKey(string? tag)
    {
        if (!TryParseManagedTag(tag, out var tagInfo))
            return "";

        return tagInfo.IsLegacy
            ? $"legacy:{tagInfo.SlotIndex}"
            : $"state:{tagInfo.StateTagKey}:{tagInfo.SlotIndex}";
    }

    internal static bool IsManagedTag(string? tag) => TryParseManagedTag(tag, out _);

    internal static int GetManagedTagOrder(string? tag)
    {
        return TryParseManagedTag(tag, out var tagInfo)
            ? tagInfo.SlotIndex
            : int.MaxValue;
    }

    internal static bool TryParseManagedTag(string? tag, out BbbManagedTagInfo tagInfo)
    {
        tagInfo = new BbbManagedTagInfo();
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var trimmed = tag!.Trim();
        if (trimmed.StartsWith(StateTagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var markerIndex = trimmed.IndexOf(StateTagMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= StateTagPrefix.Length)
                return false;

            var slotText = trimmed.Substring(StateTagPrefix.Length, markerIndex - StateTagPrefix.Length).Trim();
            if (!int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotIndex) || slotIndex <= 0)
                return false;

            var stateTagKey = NormalizeStateTagKey(trimmed.Substring(markerIndex + StateTagMarker.Length));
            if (stateTagKey.Length == 0)
                return false;

            tagInfo = new BbbManagedTagInfo
            {
                IsLegacy = false,
                SlotIndex = slotIndex,
                StateTagKey = stateTagKey
            };
            return true;
        }

        var normalizedTag = NormalizeLooseTag(trimmed);
        var normalizedBaseTag = NormalizeLooseTag(LegacyBaseTag);
        if (normalizedTag == normalizedBaseTag)
        {
            tagInfo = new BbbManagedTagInfo
            {
                IsLegacy = true,
                SlotIndex = 1
            };
            return true;
        }

        if (!normalizedTag.StartsWith(normalizedBaseTag, StringComparison.Ordinal))
            return false;

        var suffix = normalizedTag.Substring(normalizedBaseTag.Length);
        if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacySlotIndex) || legacySlotIndex <= 0)
            return false;

        tagInfo = new BbbManagedTagInfo
        {
            IsLegacy = true,
            SlotIndex = legacySlotIndex
        };
        return true;
    }

    internal static string GetStateDisplayText(BbbDynamicBlockStateInfo stateInfo)
    {
        return stateInfo.HasState ? stateInfo.DisplayText : "未识别";
    }

    internal static string BuildDynamicPropertyDebugText(BlockReference blockReference)
    {
        if (!blockReference.IsDynamicBlock)
            return "";

        var parts = new List<string>();
        try
        {
            foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
            {
                var propertyName = (property.PropertyName ?? "").Trim();
                if (propertyName.Length == 0)
                    propertyName = "<未命名参数>";

                string propertyValue;
                try
                {
                    propertyValue = FormatPropertyValue(property.Value);
                }
                catch
                {
                    propertyValue = "<读取失败>";
                }

                var hint = TryClassifyStateProperty(propertyName, out _, out var key, out _, out _)
                    ? $" [{key}]"
                    : "";
                parts.Add($"{propertyName}={propertyValue}{hint}");
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取动态图块调试参数失败", ex);
            return $"读取动态参数失败：{ex.Message}";
        }

        return parts.Count == 0 ? "未读取到任何动态参数。" : string.Join("；", parts);
    }

    private static IEnumerable<AttributeReference> ReadManagedAttributes(
        BlockReference blockReference,
        Transaction transaction,
        OpenMode openMode)
    {
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (attributeId.IsInvalid())
                continue;

            if (transaction.GetObject(attributeId, openMode, false) is not AttributeReference attributeReference)
                continue;

            if (!IsManagedTag(attributeReference.Tag))
                continue;

            yield return attributeReference;
        }
    }

    private static bool IsTagInCurrentWriteScope(BbbManagedTagInfo tagInfo, BbbDynamicBlockStateInfo stateInfo)
    {
        if (stateInfo.HasState)
            return !tagInfo.IsLegacy && string.Equals(tagInfo.StateTagKey, stateInfo.TagKey, StringComparison.OrdinalIgnoreCase);

        return tagInfo.IsLegacy;
    }

    private static string BuildLegacyManagedTag(int slotIndex)
    {
        return slotIndex <= 1 ? LegacyBaseTag : $"{LegacyBaseTag}_{slotIndex}";
    }

    private static bool TryBuildStatePart(
        DynamicBlockReferenceProperty property,
        out BbbDynamicStatePart part)
    {
        part = new BbbDynamicStatePart();

        var name = (property.PropertyName ?? "").Trim();
        if (name.Length == 0)
            return false;

        if (!TryClassifyStateProperty(name, out var priority, out var key, out var displayLabel, out var includeSourceNameInTag))
            return false;

        string display;
        try
        {
            display = FormatPropertyValue(property.Value);
        }
        catch
        {
            return false;
        }

        if (display.Length == 0)
            return false;

        var tagValue = NormalizeStateToken(display);
        if (tagValue.Length == 0)
            return false;

        var sourceName = NormalizeLooseTag(name);
        var tagPart = includeSourceNameInTag && sourceName.Length > 0
            ? $"{key}_{sourceName}_{tagValue}"
            : $"{key}_{tagValue}";

        part = new BbbDynamicStatePart
        {
            Priority = priority,
            Key = key,
            SourceName = sourceName,
            DisplayLabel = displayLabel,
            Display = display,
            TagValue = tagValue,
            TagPart = tagPart
        };
        return true;
    }

    private static bool TryFindEquivalentStateEntries(
        string currentStateTagKey,
        IReadOnlyDictionary<string, Dictionary<int, BbbManagedDeviceNameEntry>> stateEntriesByKey,
        out Dictionary<int, BbbManagedDeviceNameEntry> entries)
    {
        entries = new Dictionary<int, BbbManagedDeviceNameEntry>();

        var currentSignature = BuildStateTagSignature(currentStateTagKey);
        if (currentSignature.Length == 0)
            return false;

        foreach (var pair in stateEntriesByKey)
        {
            if (string.Equals(pair.Key, currentStateTagKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(BuildStateTagSignature(pair.Key), currentSignature, StringComparison.OrdinalIgnoreCase))
                continue;

            entries = pair.Value;
            return entries.Count > 0;
        }

        return false;
    }

    private static string BuildStateTagSignature(string? stateTagKey)
    {
        if (string.IsNullOrWhiteSpace(stateTagKey))
            return "";

        var parts = NormalizeStateTagKey(stateTagKey)!
            .Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 0)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return parts.Count == 0 ? "" : string.Join("__", parts);
    }

    private static string NormalizeStateTagKey(string? stateTagKey)
    {
        if (string.IsNullOrWhiteSpace(stateTagKey))
            return "";

        var parts = stateTagKey!
            .Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeStateToken)
            .Where(x => x.Length > 0)
            .ToList();

        return parts.Count == 0 ? "" : string.Join("__", parts);
    }

    private static bool TryClassifyStateProperty(
        string propertyName,
        out int priority,
        out string key,
        out string displayLabel,
        out bool includeSourceNameInTag)
    {
        priority = int.MaxValue;
        key = "";
        displayLabel = propertyName.Trim();
        includeSourceNameInTag = false;
        var normalized = NormalizeLooseTag(propertyName);

        if (normalized.IndexOf("visibility", StringComparison.Ordinal) >= 0 || normalized.IndexOf("可见性", StringComparison.Ordinal) >= 0)
        {
            priority = 1;
            key = "VIS";
            displayLabel = "可见性";
            return true;
        }

        if (normalized.IndexOf("lookup", StringComparison.Ordinal) >= 0 || normalized.IndexOf("查找", StringComparison.Ordinal) >= 0)
        {
            priority = 2;
            key = "LOOKUP";
            displayLabel = "查找";
            return true;
        }

        if (normalized.IndexOf("flip", StringComparison.Ordinal) >= 0 || normalized.IndexOf("翻转", StringComparison.Ordinal) >= 0)
        {
            priority = 3;
            key = "FLIP";
            displayLabel = "翻转";
            return true;
        }

        if (normalized.IndexOf("state", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("状态", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("形态", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("方向", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("mode", StringComparison.Ordinal) >= 0)
        {
            priority = 4;
            key = "STATE";
            return true;
        }

        if (normalized.IndexOf("rotation", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("rotate", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("angle", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("旋转", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("角度", StringComparison.Ordinal) >= 0)
        {
            priority = 5;
            key = "ANGLE";
            includeSourceNameInTag = true;
            return true;
        }

        if (normalized.IndexOf("distance", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("stretch", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("length", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("width", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("height", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("size", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("radius", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("diameter", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("距离", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("拉伸", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("长度", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("宽度", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("高度", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("尺寸", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("半径", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("直径", StringComparison.Ordinal) >= 0)
        {
            priority = 6;
            key = "SIZE";
            includeSourceNameInTag = true;
            return true;
        }

        if (normalized.IndexOf("scale", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("比例", StringComparison.Ordinal) >= 0 ||
            normalized.IndexOf("缩放", StringComparison.Ordinal) >= 0)
        {
            priority = 7;
            key = "SCALE";
            includeSourceNameInTag = true;
            return true;
        }

        return false;
    }

    private static string FormatPropertyValue(object? value)
    {
        return value switch
        {
            null => "",
            string s => s.Trim(),
            bool b => b ? "1" : "0",
            double d when Math.Abs(d - Math.Round(d)) < 1e-9 => ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f when Math.Abs(f - Math.Round(f)) < 1e-6f => ((int)Math.Round(f)).ToString(CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim()
        };
    }

    private static string NormalizeStateToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value!.Length);
        var pendingSeparator = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch >= 0x2E80)
            {
                if (pendingSeparator && builder.Length > 0 && builder[builder.Length - 1] != '_')
                    builder.Append('_');

                builder.Append(char.IsLetter(ch) ? char.ToLowerInvariant(ch) : ch);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static string NormalizeLooseTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value!.Length);
        foreach (var ch in value!)
        {
            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/' || ch == '\\' || ch == '(' || ch == ')' || ch == '（' || ch == '）' || ch == '[' || ch == ']' || ch == '【' || ch == '】' || ch == ':' || ch == '：' || ch == '.')
                continue;

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static string GetAttributeValue(AttributeReference attributeReference)
    {
        if (!attributeReference.IsMTextAttribute)
            return attributeReference.TextString ?? "";

        var mText = attributeReference.MTextAttribute;
        return mText?.Text ?? attributeReference.TextString ?? "";
    }
}
