using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using C_toolsJson;

namespace C_toolsPlugin;

/// <summary>
/// 图层别名 → 图层名等。JSON + 保存时生成 c_tools_layer_shortcuts.lsp 并自动加载，命令行 A1+回车切层。
/// </summary>
internal static class LayerShortcutStore
{
    /// <summary>当前 <c>layer_shortcuts.json</c> 包装格式版本；升高时可在此做迁移。</summary>
    internal const int LayerShortcutsSchemaVersion = 1;

    private static readonly object FileIoLock = new();
    private static LayerShortcutCacheEntry? _cache;

    internal static string FilePath => Path.Combine(C_toolsPaths.LayerShortcutsDataFolder, "layer_shortcuts.json");

    /// <summary>旧版：自定义安装时曾放在 User 根目录。</summary>
    private static string LegacyFilePathInUserRoot => Path.Combine(C_toolsPaths.UserSiblingFolder, "layer_shortcuts.json");

    /// <summary>更旧：lsp 曾仅在 Support 下，JSON 一般不在此。</summary>
    private static string LegacySupportLispPath =>
        Path.Combine(C_toolsPaths.SupportFolder, LayerLispShortcuts.FileName);

    internal static List<LayerShortcutEntry> Load()
    {
        lock (FileIoLock)
        {
            try
            {
                TrySeedFromInitialFileIfMissing(out _, out _, out _);

                var primaryState = LayerShortcutFileState.Create(FilePath);
                var legacyState = LayerShortcutFileState.Create(LegacyFilePathInUserRoot);
                if (_cache != null && _cache.Matches(primaryState, legacyState))
                    return CloneEntries(_cache.Items);

                var path = ResolveReadPathForJson();
                if (path == null)
                {
                    _cache = new LayerShortcutCacheEntry(primaryState, legacyState, new List<LayerShortcutEntry>());
                    return new List<LayerShortcutEntry>();
                }

                var json = C_toolsTextFileStore.TryReadAllText(path, "读取 layer_shortcuts.json 失败");
                if (json is null || json.Length == 0)
                {
                    _cache = new LayerShortcutCacheEntry(primaryState, legacyState, new List<LayerShortcutEntry>());
                    return new List<LayerShortcutEntry>();
                }

                var list = DeserializeShortcutList(json);
                foreach (var e in list)
                {
                    MigrateHatchFields(e);
                    NormalizeHatchFlags(e);
                }

                if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                    Save(list);

                _cache = new LayerShortcutCacheEntry(primaryState, legacyState, CloneEntries(list));
                return list;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("读取 layer_shortcuts.json 失败", ex);
                return new List<LayerShortcutEntry>();
            }
        }
    }

    internal static bool TrySeedFromInitialFileIfMissing(
        out int count,
        out string? sourcePath,
        out List<string> warnings)
    {
        lock (FileIoLock)
        {
            count = 0;
            sourcePath = null;
            warnings = new List<string>();

            if (ResolveReadPathForJson() != null)
                return false;

            if (!LayerShortcutInitialData.TryLoadEntries(out var entries, out sourcePath, out warnings))
                return false;

            if (entries.Count == 0)
                return false;

            Save(entries);
            count = entries.Count;
            C_toolsDiagnostics.LogNonFatal(
                $"{LayerShortcutInitialData.FileName} 已生成默认 layer_shortcuts.json（{count} 条）：{sourcePath}",
                null);
            foreach (var warning in warnings)
                C_toolsDiagnostics.LogNonFatal($"{LayerShortcutInitialData.FileName}：{warning}", null);
            return true;
        }
    }

    private static List<LayerShortcutEntry> DeserializeShortcutList(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.Length == 0)
            return new List<LayerShortcutEntry>();

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<LayerShortcutsFile>(json, JsonOptionsCache.ReadRelaxed);
                if (doc?.Items != null)
                {
                    if (doc.SchemaVersion > LayerShortcutsSchemaVersion)
                        C_toolsDiagnostics.LogNonFatal(
                            $"layer_shortcuts.json 的 schemaVersion={doc.SchemaVersion} 高于插件支持的 {LayerShortcutsSchemaVersion}，仍尝试读取 items。");
                    MigrateDocumentSchemaIfNeeded(doc);
                    return doc.Items;
                }
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("按带 schemaVersion 的格式解析 layer_shortcuts.json 失败，将尝试旧版数组格式", ex);
            }
        }

        try
        {
            return JsonSerializer.Deserialize<List<LayerShortcutEntry>>(json, JsonOptionsCache.ReadRelaxed) ?? new List<LayerShortcutEntry>();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("按旧版数组解析 layer_shortcuts.json 失败", ex);
            return new List<LayerShortcutEntry>();
        }
    }

    private static void MigrateDocumentSchemaIfNeeded(LayerShortcutsFile doc)
    {
        while (doc.SchemaVersion < LayerShortcutsSchemaVersion)
        {
            doc.SchemaVersion++;
            // 将来按版本递增在此追加迁移步骤
        }
    }

    private static string? ResolveReadPathForJson()
    {
        if (File.Exists(FilePath))
            return FilePath;
        if (!string.Equals(FilePath, LegacyFilePathInUserRoot, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(LegacyFilePathInUserRoot))
            return LegacyFilePathInUserRoot;
        return null;
    }

    internal static void Save(IReadOnlyList<LayerShortcutEntry> items)
    {
        lock (FileIoLock)
        {
            var doc = new LayerShortcutsFile
            {
                SchemaVersion = LayerShortcutsSchemaVersion,
                Items = items.ToList()
            };
            if (C_toolsJsonFileStore.TryWrite(
                    FilePath,
                    doc,
                    JsonOptionsCache.WriteIndented,
                    "写入 layer_shortcuts.json 失败",
                    C_toolsDiagnostics.LogNonFatal))
            {
                TryRemoveLegacyJsonIfDifferent();
                _cache = new LayerShortcutCacheEntry(
                    LayerShortcutFileState.Create(FilePath),
                    LayerShortcutFileState.Create(LegacyFilePathInUserRoot),
                    CloneEntries(doc.Items ?? new List<LayerShortcutEntry>()));
            }
        }
    }

    private static void TryRemoveLegacyJsonIfDifferent()
    {
        var legacy = LegacyFilePathInUserRoot;
        if (string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        C_toolsTextFileStore.TryDeleteFile(legacy, "删除遗留 layer_shortcuts.json 路径失败");
    }

    private static void MigrateHatchFields(LayerShortcutEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.HatchStyle))
        {
            e.HatchPattern = null;
            e.HatchScale = null;
            e.HatchAngle = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(e.HatchPattern) || !string.IsNullOrWhiteSpace(e.HatchScale) ||
            !string.IsNullOrWhiteSpace(e.HatchAngle))
            e.HatchStyle = HatchStyleSnapshot.FromLegacyStrings(e.HatchPattern, e.HatchScale, e.HatchAngle).ToJson();
        e.HatchPattern = null;
        e.HatchScale = null;
        e.HatchAngle = null;
    }

    private static void NormalizeHatchFlags(LayerShortcutEntry e)
    {
        if (e.RunHatchWhenNoSelection && string.IsNullOrWhiteSpace(e.HatchStyle))
            e.HatchStyle = HatchStyleSnapshot.Defaults().ToJson();
        if (!string.IsNullOrWhiteSpace(e.HatchStyle))
            e.RunDimensionWhenNoSelection = false;
    }

    private static List<LayerShortcutEntry> CloneEntries(IReadOnlyList<LayerShortcutEntry> items)
    {
        var cloned = new List<LayerShortcutEntry>(items.Count);
        foreach (var item in items)
        {
            if (item == null)
                continue;

            cloned.Add(new LayerShortcutEntry
            {
                Alias = item.Alias,
                LayerName = item.LayerName,
                ColorIndex = item.ColorIndex,
                LinetypeName = item.LinetypeName,
                LineWeight = item.LineWeight,
                Description = item.Description,
                RunDimensionWhenNoSelection = item.RunDimensionWhenNoSelection,
                RunHatchWhenNoSelection = item.RunHatchWhenNoSelection,
                HatchStyle = item.HatchStyle,
                HatchPattern = item.HatchPattern,
                HatchScale = item.HatchScale,
                HatchAngle = item.HatchAngle
            });
        }

        return cloned;
    }

    private sealed class LayerShortcutCacheEntry
    {
        public LayerShortcutCacheEntry(
            LayerShortcutFileState primaryState,
            LayerShortcutFileState legacyState,
            List<LayerShortcutEntry> items)
        {
            PrimaryState = primaryState;
            LegacyState = legacyState;
            Items = items;
        }

        public LayerShortcutFileState PrimaryState { get; }
        public LayerShortcutFileState LegacyState { get; }
        public List<LayerShortcutEntry> Items { get; }

        public bool Matches(LayerShortcutFileState primaryState, LayerShortcutFileState legacyState)
        {
            return PrimaryState.Equals(primaryState) && LegacyState.Equals(legacyState);
        }
    }

    private readonly struct LayerShortcutFileState : IEquatable<LayerShortcutFileState>
    {
        private LayerShortcutFileState(string path, bool exists, DateTime lastWriteTimeUtc, long length)
        {
            Path = path;
            Exists = exists;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Length = length;
        }

        private string Path { get; }
        private bool Exists { get; }
        private DateTime LastWriteTimeUtc { get; }
        private long Length { get; }

        public static LayerShortcutFileState Create(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new LayerShortcutFileState(path, exists: false, DateTime.MinValue, length: 0);

                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new LayerShortcutFileState(path, exists: true, lastWriteTimeUtc, stream.Length);
            }
            catch
            {
                return new LayerShortcutFileState(path, exists: File.Exists(path), DateTime.MinValue, length: 0);
            }
        }

        public bool Equals(LayerShortcutFileState other)
        {
            return Exists == other.Exists &&
                   LastWriteTimeUtc == other.LastWriteTimeUtc &&
                   Length == other.Length &&
                   string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return obj is LayerShortcutFileState other && Equals(other);
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

/// <summary><c>layer_shortcuts.json</c> 根对象：带版本字段便于日后迁移；旧版为裸数组。</summary>
internal sealed class LayerShortcutsFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("items")]
    public List<LayerShortcutEntry>? Items { get; set; }
}

internal sealed class LayerShortcutEntry
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = "";

    [JsonPropertyName("layerName")]
    public string LayerName { get; set; } = "";

    /// <summary>AutoCAD 颜色索引 ACI 1–255；有预选切层时对选中图元设 ByAci，不修改图层表颜色。</summary>
    [JsonPropertyName("colorIndex")]
    public int? ColorIndex { get; set; }

    /// <summary>线型名（如 Continuous）；空则按 Continuous。</summary>
    [JsonPropertyName("linetypeName")]
    public string? LinetypeName { get; set; }

    /// <summary>线宽：0/默认 或 LineWeight 枚举名（如 LineWeight025）。</summary>
    [JsonPropertyName("lineWeight")]
    public string? LineWeight { get; set; }

    /// <summary>浮层「说明」列，随图层快捷一并保存。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>无预选仅切当前层时，是否在切层后自动执行对齐标注命令。</summary>
    [JsonPropertyName("runDimensionWhenNoSelection")]
    public bool RunDimensionWhenNoSelection { get; set; }

    /// <summary>是否存在已保存的填充样式（与 <see cref="HatchStyle"/> 非空等价；写入 JSON 便于外部工具识别）。</summary>
    [JsonPropertyName("runHatchWhenNoSelection")]
    public bool RunHatchWhenNoSelection { get; set; }

    /// <summary>拾取的填充样式 JSON（<see cref="HatchStyleSnapshot"/>）；空则快捷键填充时用 CAD 默认。</summary>
    [JsonPropertyName("hatchStyle")]
    public string? HatchStyle { get; set; }

    /// <summary>旧版三列；读入后迁移到 <see cref="HatchStyle"/> 并清空。</summary>
    [JsonPropertyName("hatchPattern")]
    public string? HatchPattern { get; set; }

    [JsonPropertyName("hatchScale")]
    public string? HatchScale { get; set; }

    [JsonPropertyName("hatchAngle")]
    public string? HatchAngle { get; set; }
}
