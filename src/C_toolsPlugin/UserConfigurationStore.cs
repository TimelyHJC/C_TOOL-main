using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace C_toolsPlugin;

/// <summary>
/// 读取统一初始化文件中的基础设置；兼容旧版 User 根目录下的 Configuration / Configuration.json。
/// </summary>
internal static class UserConfigurationStore
{
    internal const int FileVersion = 2;
    private const string PrimaryFileName = "Configuration";
    private const string AlternateFileName = "Configuration.json";
    private const string CommandPrefix = "command.";
    private const string KeyDimStyleName = "dimStyleName";
    private const string KeyDimStyleGroupPrefix = "dimStyleGroupPrefix";
    private const string KeyMLeaderStyleName = "mLeaderStyleName";
    private const string SuffixDimStyleName = ".dimStyleName";
    private const string SuffixDimStyle = ".dimStyle";
    private const string SuffixMLeaderStyleName = ".mLeaderStyleName";
    private const string SuffixMLeaderStyle = ".mLeaderStyle";
    private const string DefaultDimStyleName = "Standard";
    private const string DefaultMLeaderStyleName = "Standard";
    private static readonly string[] BuiltInMLeaderOverrideCommandIds =
    {
        C_toolsCommandIds.Ddd.Leader,
        C_toolsCommandIds.Ddd.InsertLeader,
        C_toolsCommandIds.Ddd.InsertText,
        C_toolsCommandIds.MainToolset.QuickArrow
    };
    private static readonly object CacheSyncRoot = new();
    private static ConfigurationCacheEntry? _cache;

    internal static string PrimaryFilePath => Path.Combine(C_toolsPaths.UserEditableFolder, PrimaryFileName);

    internal static string AlternateFilePath => Path.Combine(C_toolsPaths.UserEditableFolder, AlternateFileName);

    internal static string? TryGetDimStyleGroupPrefix() => TryLoad()?.DimStyleGroupPrefix;

    internal static string? TryGetDimStyleName() => TryLoad()?.DimStyleName;

    internal static string? TryGetDimStyleName(string? commandId)
    {
        var config = TryLoad();
        if (config == null)
            return null;

        var normalizedCommandId = NormalizeToken(commandId);
        if (normalizedCommandId != null &&
            config.CommandDimStyleNames.TryGetValue(normalizedCommandId, out var commandStyleName) &&
            !string.IsNullOrWhiteSpace(commandStyleName))
        {
            return commandStyleName;
        }

        return config.DimStyleName;
    }

    internal static string? TryGetMLeaderStyleName() => TryLoad()?.MLeaderStyleName;

    internal static string? TryGetMLeaderStyleName(string? commandId)
    {
        var config = TryLoad();
        if (config == null)
            return null;

        var normalizedCommandId = NormalizeToken(commandId);
        if (normalizedCommandId != null &&
            config.CommandMLeaderStyleNames.TryGetValue(normalizedCommandId, out var commandStyleName) &&
            !string.IsNullOrWhiteSpace(commandStyleName))
        {
            return commandStyleName;
        }

        return config.MLeaderStyleName;
    }

    internal static void EnsureInitialFileIfMissing()
    {
        if (File.Exists(PrimaryFilePath) || File.Exists(AlternateFilePath))
            TryCompactExistingFile();
    }

    internal static string BuildDefaultFileContent() => BuildFileContent(document: null, useFactoryDefaults: true);

    internal static string? TryBuildCompactedFileContent(string path, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ShouldCompactExistingFile(path, text))
            return null;

        var parsed = LooksLikeJson(path, text)
            ? TryParseJson(text)
            : ParseKeyValueText(text);
        if (parsed == null)
            return null;

        return BuildFileContent(Normalize(parsed), useFactoryDefaults: false);
    }

    private static void TryCompactExistingFile()
    {
        foreach (var path in EnumerateCandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            var text = C_toolsTextFileStore.TryReadAllText(path, "读取 User\\Configuration");
            if (text == null)
                return;

            var compacted = TryBuildCompactedFileContent(path, text);
            if (compacted == null)
                return;

            _ = C_toolsTextFileStore.TryWriteAllText(
                PrimaryFilePath,
                compacted,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                "整理 User\\Configuration");
            return;
        }
    }

    private static string BuildFileContent(ConfigurationDocument? document, bool useFactoryDefaults)
    {
        var dimStyleName = ResolveWrittenValue(document?.DimStyleName, useFactoryDefaults ? DefaultDimStyleName : null);
        var dimStyleGroupPrefix = ResolveWrittenValue(document?.DimStyleGroupPrefix, fallback: null);
        var mleaderStyleName = ResolveWrittenValue(document?.MLeaderStyleName, useFactoryDefaults ? DefaultMLeaderStyleName : null);
        var lines = new[]
        {
            "# C_TOOL Configuration",
            "# 直接用记事本修改等号右侧即可；样式名需与当前图纸中的名称一致。",
            "# 空值表示不覆盖，继续沿用默认值或当前图纸设置。",
            "# 注释行以 # 开头，可随意增删。",
            "# 当前模板只保留当前版本会直接读取的键；其余样式细项请在系统配置面板或“设置引线”界面中调整。",
            "",
            "# 基础设置",
            "# dimStyleName：系统配置面板打开“标注样式”页时优先定位的样式名；不会自动改当前 CAD 标注样式",
            $"{KeyDimStyleName}={dimStyleName}",
            "# dimStyleGroupPrefix：系统配置面板打开“标注样式”页时，若找不到 dimStyleName，则按此前缀回退选组",
            $"{KeyDimStyleGroupPrefix}={dimStyleGroupPrefix}",
            "# mLeaderStyleName：默认多重引线样式，供 F_DddLeader / F_DDD_INSERT_LEADER / F_DDD_INSERT_TEXT / F_JT 使用",
            $"{KeyMLeaderStyleName}={mleaderStyleName}",
            "",
            "# 命令覆盖",
            "# F_DA / F_DC：始终跟随当前 CAD 标注样式，这里无需单独配置。",
            "# DDD 引线/文字命令：留空时继承 mLeaderStyleName",
            "# command.F_DddLeader.mLeaderStyleName：面板发起的多重引线",
            $"command.{C_toolsCommandIds.Ddd.Leader}.mLeaderStyleName={GetCommandOverrideValue(document?.CommandMLeaderStyleNames, C_toolsCommandIds.Ddd.Leader)}",
            "# command.F_DDD_INSERT_LEADER.mLeaderStyleName：列表文字插入多重引线",
            $"command.{C_toolsCommandIds.Ddd.InsertLeader}.mLeaderStyleName={GetCommandOverrideValue(document?.CommandMLeaderStyleNames, C_toolsCommandIds.Ddd.InsertLeader)}",
            "# command.F_DDD_INSERT_TEXT.mLeaderStyleName：列表文字插入纯文字时使用其文字样式来源",
            $"command.{C_toolsCommandIds.Ddd.InsertText}.mLeaderStyleName={GetCommandOverrideValue(document?.CommandMLeaderStyleNames, C_toolsCommandIds.Ddd.InsertText)}",
            "# command.F_JT.mLeaderStyleName：快速箭头命令；留空时继承 mLeaderStyleName",
            $"command.{C_toolsCommandIds.MainToolset.QuickArrow}.mLeaderStyleName={GetCommandOverrideValue(document?.CommandMLeaderStyleNames, C_toolsCommandIds.MainToolset.QuickArrow)}"
        };

        var contentLines = new List<string>(lines);
        AppendPreservedLegacyCommandOverrides(contentLines, document);
        contentLines.Add("");
        contentLines.Add("# 其他 CAD 样式细项请在系统配置面板或“设置引线”界面中调整，本文件不再展开 reference.* 只读清单。");
        return string.Join("\r\n", contentLines) + "\r\n";
    }

    private static bool ShouldCompactExistingFile(string path, string text)
    {
        if (LooksLikeJson(path, text))
            return true;

        return ContainsIgnoreCase(text, "reference.dimStyle.") ||
               ContainsIgnoreCase(text, "reference.mLeader.") ||
               ContainsIgnoreCase(text, "控制参数清单（reference.*") ||
               ContainsIgnoreCase(text, "reference.* 仅供显示/查阅");
    }

    private static void AppendPreservedLegacyCommandOverrides(List<string> lines, ConfigurationDocument? document)
    {
        if (document == null)
            return;

        var legacyDimStyleOverrides = document.CommandDimStyleNames
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var extraMLeaderOverrides = document.CommandMLeaderStyleNames
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) &&
                         !BuiltInMLeaderOverrideCommandIds.Any(id => string.Equals(id, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (legacyDimStyleOverrides.Count == 0 && extraMLeaderOverrides.Count == 0)
            return;

        lines.Add("");
        lines.Add("# 兼容保留：以下命令级样式项来自旧配置；当前版本一般无需修改，如无需要可手动删除。");

        foreach (var kv in legacyDimStyleOverrides)
            lines.Add($"command.{kv.Key}.dimStyleName={kv.Value}");

        foreach (var kv in extraMLeaderOverrides)
            lines.Add($"command.{kv.Key}.mLeaderStyleName={kv.Value}");
    }

    private static string ResolveWrittenValue(string? value, string? fallback)
    {
        return NormalizeToken(value) ?? fallback ?? "";
    }

    private static string GetCommandOverrideValue(Dictionary<string, string>? map, string commandId)
    {
        if (map == null || !map.TryGetValue(commandId, out var value))
            return "";

        return ResolveWrittenValue(value, fallback: null);
    }

    private static bool ContainsIgnoreCase(string text, string fragment)
    {
        return text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static ConfigurationDocument? TryLoad()
    {
        lock (CacheSyncRoot)
        {
            var primaryState = ConfigurationFileState.Create(PrimaryFilePath);
            var alternateState = ConfigurationFileState.Create(AlternateFilePath);
            var initialDataPath = LayerShortcutInitialData.ResolveFilePath() ?? LayerShortcutInitialData.PrimaryPath;
            var initialDataState = ConfigurationFileState.Create(initialDataPath);
            if (_cache != null && _cache.Matches(primaryState, alternateState, initialDataState))
                return _cache.Document;

            var document = TryLoadWithoutCache(primaryState, alternateState, initialDataState);
            _cache = new ConfigurationCacheEntry(primaryState, alternateState, initialDataState, document);
            return document;
        }
    }

    private static ConfigurationDocument? TryLoadWithoutCache(
        ConfigurationFileState primaryState,
        ConfigurationFileState alternateState,
        ConfigurationFileState initialDataState)
    {
        if (TryLoadFromState(primaryState, "读取 User\\Configuration", out var primaryDocument))
            return primaryDocument;

        if (TryLoadFromState(alternateState, "读取 User\\Configuration", out var alternateDocument))
            return alternateDocument;

        if (TryLoadFromState(initialDataState, $"读取 {LayerShortcutInitialData.FileName}", out var initialDocument))
            return initialDocument;

        return null;
    }

    private static bool TryLoadFromState(ConfigurationFileState state, string readErrorContext, out ConfigurationDocument? document)
    {
        document = null;
        if (!state.Exists)
            return false;

        var text = C_toolsTextFileStore.TryReadAllText(state.Path, readErrorContext);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var content = text!;
        var parsed = LooksLikeJson(state.Path, content)
            ? TryParseJson(content)
            : ParseKeyValueText(content);
        if (parsed == null)
            return false;

        document = Normalize(parsed);
        return true;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        yield return PrimaryFilePath;
        yield return AlternateFilePath;
    }

    private static bool LooksLikeJson(string path, string text)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal);
    }

    private static ConfigurationDocument? TryParseJson(string text)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ConfigurationJsonDto>(text, JsonOptionsCache.ReadRelaxedCamelCase);
            if (dto == null)
                return null;

            var document = new ConfigurationDocument
            {
                Version = FileVersion,
                DimStyleGroupPrefix = dto.DimStyleGroupPrefix,
                DimStyleName = dto.DimStyleName,
                MLeaderStyleName = dto.MLeaderStyleName
            };

            if (dto.CommandDimStyleNames != null)
            {
                foreach (var kv in dto.CommandDimStyleNames)
                    document.CommandDimStyleNames[kv.Key] = kv.Value ?? "";
            }

            if (dto.CommandMLeaderStyleNames != null)
            {
                foreach (var kv in dto.CommandMLeaderStyleNames)
                    document.CommandMLeaderStyleNames[kv.Key] = kv.Value ?? "";
            }

            return document;
        }
        catch (JsonException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析 User\\Configuration（JSON）", ex);
            return null;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析 User\\Configuration（JSON 类型）", ex);
            return null;
        }
    }

    private static ConfigurationDocument ParseKeyValueText(string text)
    {
        var document = new ConfigurationDocument { Version = FileVersion };
        var lineStart = 0;
        while (TryReadNextTrimmedLine(text, ref lineStart, out var line))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith(";", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
                separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var rawKey = line.Substring(0, separatorIndex).Trim();
            var rawValue = line[(separatorIndex + 1)..].Trim();
            if (rawKey.Length == 0)
                continue;

            var key = rawKey.Trim();
            var value = Unquote(rawValue);

            if (TryApplyGeneralKey(document, key, value))
                continue;

            TryApplyCommandKey(document, key, value);
        }

        return document;
    }

    private static bool TryReadNextTrimmedLine(string text, ref int lineStart, out string line)
    {
        var length = text.Length;
        while (lineStart < length)
        {
            var lineEnd = lineStart;
            while (lineEnd < length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
                lineEnd++;

            var contentStart = lineStart;
            while (contentStart < lineEnd && char.IsWhiteSpace(text[contentStart]))
                contentStart++;

            var contentEnd = lineEnd - 1;
            while (contentEnd >= contentStart && char.IsWhiteSpace(text[contentEnd]))
                contentEnd--;

            if (lineEnd < length && text[lineEnd] == '\r' && lineEnd + 1 < length && text[lineEnd + 1] == '\n')
                lineEnd++;

            lineStart = lineEnd + 1;
            if (contentStart > contentEnd)
                continue;

            line = text.Substring(contentStart, contentEnd - contentStart + 1);
            return true;
        }

        line = "";
        return false;
    }

    private static bool TryApplyGeneralKey(ConfigurationDocument document, string key, string value)
    {
        if (key.Equals(KeyDimStyleName, StringComparison.OrdinalIgnoreCase) ||
            key.Equals("default.dimStyleName", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("default.dimStyle", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("dimStyle", StringComparison.OrdinalIgnoreCase))
        {
            document.DimStyleName = value;
            return true;
        }

        if (key.Equals(KeyDimStyleGroupPrefix, StringComparison.OrdinalIgnoreCase) ||
            key.Equals("default.dimStyleGroupPrefix", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("dimStyleGroup", StringComparison.OrdinalIgnoreCase))
        {
            document.DimStyleGroupPrefix = value;
            return true;
        }

        if (key.Equals(KeyMLeaderStyleName, StringComparison.OrdinalIgnoreCase) ||
            key.Equals("default.mLeaderStyleName", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("default.mLeaderStyle", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("mLeaderStyle", StringComparison.OrdinalIgnoreCase))
        {
            document.MLeaderStyleName = value;
            return true;
        }

        return false;
    }

    private static void TryApplyCommandKey(ConfigurationDocument document, string key, string value)
    {
        if (!key.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        var remainder = key[CommandPrefix.Length..].Trim();
        if (TryMatchCommandSuffix(remainder, SuffixDimStyleName, out var dimStyleCommandId) ||
            TryMatchCommandSuffix(remainder, SuffixDimStyle, out dimStyleCommandId))
        {
            document.CommandDimStyleNames[dimStyleCommandId] = value;
            return;
        }

        if (TryMatchCommandSuffix(remainder, SuffixMLeaderStyleName, out var mleaderCommandId) ||
            TryMatchCommandSuffix(remainder, SuffixMLeaderStyle, out mleaderCommandId))
        {
            document.CommandMLeaderStyleNames[mleaderCommandId] = value;
        }
    }

    private static bool TryMatchCommandSuffix(string value, string suffix, out string commandId)
    {
        commandId = "";
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rawCommandId = value[..^suffix.Length].Trim();
        if (rawCommandId.Length == 0)
            return false;

        commandId = rawCommandId;
        return true;
    }

    private static ConfigurationDocument Normalize(ConfigurationDocument document)
    {
        document.Version = FileVersion;
        document.DimStyleGroupPrefix = NormalizeToken(document.DimStyleGroupPrefix);
        document.DimStyleName = NormalizeToken(document.DimStyleName);
        document.MLeaderStyleName = NormalizeToken(document.MLeaderStyleName);

        NormalizeDictionary(document.CommandDimStyleNames);
        NormalizeDictionary(document.CommandMLeaderStyleNames);
        return document;
    }

    private static void NormalizeDictionary(Dictionary<string, string> map)
    {
        var snapshot = map.ToArray();
        map.Clear();

        foreach (var kv in snapshot)
        {
            var key = NormalizeToken(kv.Key);
            if (key == null)
                continue;

            map[key] = NormalizeToken(kv.Value) ?? "";
        }
    }

    private static string? NormalizeToken(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private sealed class ConfigurationDocument
    {
        public int Version { get; set; }
        public string? DimStyleGroupPrefix { get; set; }
        public string? DimStyleName { get; set; }
        public string? MLeaderStyleName { get; set; }
        public Dictionary<string, string> CommandDimStyleNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CommandMLeaderStyleNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ConfigurationJsonDto
    {
        public int Version { get; set; }
        public string? DimStyleGroupPrefix { get; set; }
        public string? DimStyleName { get; set; }
        public string? MLeaderStyleName { get; set; }
        public Dictionary<string, string?>? CommandDimStyleNames { get; set; }
        public Dictionary<string, string?>? CommandMLeaderStyleNames { get; set; }
    }

    private sealed class ConfigurationCacheEntry
    {
        public ConfigurationCacheEntry(
            ConfigurationFileState primaryState,
            ConfigurationFileState alternateState,
            ConfigurationFileState initialDataState,
            ConfigurationDocument? document)
        {
            PrimaryState = primaryState;
            AlternateState = alternateState;
            InitialDataState = initialDataState;
            Document = document;
        }

        public ConfigurationFileState PrimaryState { get; }
        public ConfigurationFileState AlternateState { get; }
        public ConfigurationFileState InitialDataState { get; }
        public ConfigurationDocument? Document { get; }

        public bool Matches(
            ConfigurationFileState primaryState,
            ConfigurationFileState alternateState,
            ConfigurationFileState initialDataState)
        {
            return PrimaryState.Equals(primaryState) &&
                   AlternateState.Equals(alternateState) &&
                   InitialDataState.Equals(initialDataState);
        }
    }

    private readonly struct ConfigurationFileState : IEquatable<ConfigurationFileState>
    {
        public ConfigurationFileState(string path, bool exists, DateTime lastWriteTimeUtc, long length)
        {
            Path = path;
            Exists = exists;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Length = length;
        }

        public string Path { get; }
        public bool Exists { get; }
        public DateTime LastWriteTimeUtc { get; }
        public long Length { get; }

        public static ConfigurationFileState Create(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new ConfigurationFileState(path, exists: false, DateTime.MinValue, length: 0);

                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new ConfigurationFileState(path, exists: true, lastWriteTimeUtc, stream.Length);
            }
            catch
            {
                return new ConfigurationFileState(path, exists: File.Exists(path), DateTime.MinValue, length: 0);
            }
        }

        public bool Equals(ConfigurationFileState other)
        {
            return Exists == other.Exists &&
                   LastWriteTimeUtc == other.LastWriteTimeUtc &&
                   Length == other.Length &&
                   string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return obj is ConfigurationFileState other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Path);
                hash = (hash * 397) ^ Exists.GetHashCode();
                hash = (hash * 397) ^ LastWriteTimeUtc.GetHashCode();
                hash = (hash * 397) ^ Length.GetHashCode();
                return hash;
            }
        }
    }
}
