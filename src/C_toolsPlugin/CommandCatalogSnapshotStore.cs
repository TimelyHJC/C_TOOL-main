using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using C_toolsJson;

namespace C_toolsPlugin;

/// <summary>命令表 JSON 快照：仅缓存「反射+PGP+说明」基础行；图层快捷键行不入库，每次从 JSON 合并。</summary>
internal static class CommandCatalogSnapshotStore
{
    private const int SchemaVersion = 5;

    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, "command_catalog_snapshot.json");

    internal static void Invalidate()
    {
        lock (FileIoLock)
        {
            C_toolsTextFileStore.TryDeleteFile(FilePath, "删除 command_catalog_snapshot.json");
        }
    }

    internal static bool TryLoad(string fingerprint, out List<CommandCatalogRow> rows)
    {
        rows = new List<CommandCatalogRow>();
        lock (FileIoLock)
        {
            var sw = Stopwatch.StartNew();
            if (!C_toolsJsonFileStore.TryRead<SnapshotDocument>(
                    FilePath,
                    JsonReadOptions,
                    "读取 command_catalog_snapshot.json",
                    "解析 command_catalog_snapshot.json",
                    C_toolsDiagnostics.LogNonFatal,
                    out var doc))
            {
                return false;
            }

            if (doc == null || doc.Fingerprint != fingerprint)
                return false;
            if (doc.Schema != SchemaVersion)
                return false;
            if (doc.Rows == null || doc.Rows.Count == 0)
                return false;

            foreach (var dto in doc.Rows)
            {
                if (string.Equals(dto.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                    continue;
                rows.Add(MapFromDto(dto));
            }

            if (rows.Count == 0)
                return false;
            sw.Stop();
            C_toolsDiagnostics.LogPerf("快照 JSON 读入+反序列化+行映射", sw.ElapsedMilliseconds, $"n={rows.Count}");
            return true;
        }
    }

    internal static void Save(string fingerprint, IReadOnlyList<CommandCatalogRow> rows)
    {
        lock (FileIoLock)
        {
            var toStore = rows
                .Where(r => !string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                .Select(MapToDto)
                .ToList();
            var doc = new SnapshotDocument
            {
                Schema = SchemaVersion,
                Fingerprint = fingerprint,
                Rows = toStore
            };
            C_toolsJsonFileStore.TryWrite(
                FilePath,
                doc,
                JsonWriteOptions,
                "写入 command_catalog_snapshot.json",
                C_toolsDiagnostics.LogNonFatal,
                serializeOperationName: "序列化 command_catalog_snapshot.json",
                encoding: new System.Text.UTF8Encoding(false));
        }
    }

    private static CommandCatalogRowDto MapToDto(CommandCatalogRow r) =>
        new()
        {
            CommandName = r.CommandName,
            AliasesSummary = r.AliasesSummary,
            Source = r.Source,
            CategoryTag = r.CategoryTag,
            LayerName = r.LayerName,
            LayerColor = r.LayerColor,
            LayerLinetype = r.LayerLinetype,
            LayerLineWeight = r.LayerLineWeight,
            LayerRunDimensionWhenNoSelection = r.LayerRunDimensionWhenNoSelection,
            LayerHatchStyleJson = r.LayerHatchStyleJson,
            Alias = r.AliasForPersistence,
            Description = r.Description
        };

    private static CommandCatalogRow MapFromDto(CommandCatalogRowDto d)
    {
        var row = new CommandCatalogRow(d.CommandName ?? "", d.AliasesSummary ?? "", d.Source ?? "", d.CategoryTag ?? "")
        {
            LayerName = d.LayerName ?? "",
            LayerColor = d.LayerColor ?? "",
            LayerLinetype = d.LayerLinetype ?? "",
            LayerLineWeight = d.LayerLineWeight ?? "",
            LayerRunDimensionWhenNoSelection = d.LayerRunDimensionWhenNoSelection,
            LayerHatchStyleJson = d.LayerHatchStyleJson ?? "",
            Description = d.Description ?? ""
        };
        row.SetAliasFromCatalog(d.Alias ?? "");
        return row;
    }

    private sealed class SnapshotDocument
    {
        public int Schema { get; set; }
        public string Fingerprint { get; set; } = "";
        public List<CommandCatalogRowDto>? Rows { get; set; }
    }

    private sealed class CommandCatalogRowDto
    {
        public string CommandName { get; set; } = "";
        public string AliasesSummary { get; set; } = "";
        public string Source { get; set; } = "";
        public string CategoryTag { get; set; } = "";
        public string LayerName { get; set; } = "";
        public string LayerColor { get; set; } = "";
        public string LayerLinetype { get; set; } = "";
        public string LayerLineWeight { get; set; } = "";
        public bool LayerRunDimensionWhenNoSelection { get; set; }
        public string LayerHatchStyleJson { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
