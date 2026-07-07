using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using C_toolsJson;

namespace C_toolsPlugin;

/// <summary>
/// 非「图层命令」行的「说明」列持久化：按全局命令名存储，与 PGP 无关。
/// </summary>
internal static class CommandDescriptionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>与图层快捷键等同目录（安装布局下为 <c>User\\C_TOOL\\</c>），更新 Plugin 不会删除此处文件。</summary>
    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, "command_descriptions.json");

    /// <summary>旧版自定义安装时曾写在 <c>User\\</c> 根目录；启动后首次保存会迁到 <see cref="FilePath"/>。</summary>
    private static string LegacyFilePathInC_toolsRoot =>
        Path.Combine(C_toolsPaths.UserSiblingFolder, "command_descriptions.json");

    /// <summary>命令名 → 说明；仅包含有内容的项。</summary>
    internal static Dictionary<string, string> Load()
    {
        var path = ResolveReadPath();
        if (path == null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!C_toolsJsonFileStore.TryRead<Dictionary<string, string>>(
                path,
                JsonOptions,
                "读取 command_descriptions.json 失败",
                "解析 command_descriptions.json 失败",
                C_toolsDiagnostics.LogNonFatal,
                out var raw))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw == null)
            return map;

        foreach (var kv in raw)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            map[kv.Key.Trim()] = kv.Value ?? "";
        }

        return map;
    }

    private static string? ResolveReadPath()
    {
        if (File.Exists(FilePath))
            return FilePath;
        var legacy = LegacyFilePathInC_toolsRoot;
        if (!string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy))
            return legacy;
        return null;
    }

    internal static void Save(IReadOnlyDictionary<string, string> byCommand)
    {
        var ordered = byCommand
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value.Trim().Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (C_toolsJsonFileStore.TryWrite(
                FilePath,
                ordered,
                JsonOptions,
                "写入 command_descriptions.json 失败",
                C_toolsDiagnostics.LogNonFatal))
        {
            TryRemoveLegacyCommandDescriptionsIfDifferent();
        }
    }

    /// <summary>安装布局下若说明文件仍在 <c>User\\</c> 根目录，一次性迁移到 <see cref="FilePath"/>。</summary>
    internal static void TryMigrateLegacyFileToUserFolder()
    {
        if (File.Exists(FilePath))
            return;
        var legacy = LegacyFilePathInC_toolsRoot;
        if (string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy))
            return;
        var map = Load();
        if (map.Count == 0)
            return;
        Save(map);
    }

    private static void TryRemoveLegacyCommandDescriptionsIfDifferent()
    {
        var legacy = LegacyFilePathInC_toolsRoot;
        if (string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        C_toolsTextFileStore.TryDeleteFile(legacy, "删除遗留 command_descriptions.json 失败");
    }

    /// <summary>合并扫描结果上的默认说明：已保存的覆盖默认。</summary>
    internal static void ApplyToRows(IEnumerable<CommandCatalogRow> rows)
    {
        var map = Load();
        IReadOnlyList<CommandCatalogRow> rowList = rows as IReadOnlyList<CommandCatalogRow> ?? rows.ToList();
        var legacyFullSnapshot = LooksLikeLegacyFullSnapshot(map, rowList);
        foreach (var row in rowList)
        {
            if (string.Equals(row.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                continue;
            var cmd = (row.CommandName ?? "").Trim();
            if (cmd.Length == 0)
                continue;
            if (map.TryGetValue(cmd, out var saved))
            {
                if (legacyFullSnapshot && CommandDescriptionDefaults.TryGet(row, out _))
                    continue;
                row.Description = saved;
            }
        }
    }

    internal static void CollectFromRows(IEnumerable<CommandCatalogRow> rows)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.Equals(row.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                continue;
            var cmd = CadPgpMerge.NormalizeTarget(row.CommandName);
            if (cmd.Length == 0)
                continue;
            var d = (row.Description ?? "").Trim();
            if (d.Length > 0)
            {
                if (CommandDescriptionDefaults.TryGet(row, out var builtInDescription) &&
                    string.Equals(d, builtInDescription, StringComparison.Ordinal))
                    continue;
                map[cmd] = d;
            }
        }

        Save(map);
    }

    /// <summary>
    /// 旧版本会把整张表的默认说明也写进 JSON，导致后续代码里的默认文案无法通过「刷新」回到界面。
    /// 若缓存覆盖了当前列表的大部分行，则将其视为旧版整表快照，仅对无内置默认说明的命令回放说明。
    /// </summary>
    private static bool LooksLikeLegacyFullSnapshot(
        IReadOnlyDictionary<string, string> map,
        IReadOnlyList<CommandCatalogRow> rows)
    {
        if (map.Count < 8 || rows.Count == 0)
            return false;

        var totalNonLayerRows = 0;
        var coveredRows = 0;
        foreach (var row in rows)
        {
            if (string.Equals(row.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                continue;

            var cmd = CadPgpMerge.NormalizeTarget(row.CommandName);
            if (cmd.Length == 0)
                continue;

            totalNonLayerRows++;
            if (map.ContainsKey(cmd))
                coveredRows++;
        }

        if (coveredRows < 8 || totalNonLayerRows == 0)
            return false;

        return coveredRows * 10 >= totalNonLayerRows * 6;
    }
}
