using System.IO;
using System.Text;

namespace C_toolsPlugin;

internal static class LayerShortcutInitialData
{
    internal const string FileName = "初始化文件.md";
    private const string LegacyFileName = "初始化快捷键文件.md";

    internal static string PrimaryPath => Path.Combine(C_toolsPaths.UserEditableFolder, FileName);

    internal static bool TryLoadEntries(
        out List<LayerShortcutEntry> entries,
        out string? sourcePath,
        out List<string> warnings)
    {
        sourcePath = ResolveFilePath();
        warnings = new List<string>();
        entries = new List<LayerShortcutEntry>();

        if (sourcePath == null)
            return false;

        try
        {
            var markdown = File.ReadAllText(sourcePath, Encoding.UTF8);
            entries = ParseMarkdown(markdown, warnings);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"读取 {FileName} 失败：{ex.Message}");
            C_toolsDiagnostics.LogNonFatal($"读取 {FileName} 失败", ex);
            return false;
        }
    }

    internal static bool TryLoadCommandAliases(
        out List<PgpAliasDto> aliases,
        out string? sourcePath,
        out List<string> warnings)
    {
        sourcePath = ResolveFilePath();
        warnings = new List<string>();
        aliases = new List<PgpAliasDto>();

        if (sourcePath == null)
            return false;

        try
        {
            var markdown = File.ReadAllText(sourcePath, Encoding.UTF8);
            aliases = ParseCommandAliases(markdown, warnings);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"读取 {FileName} 失败：{ex.Message}");
            C_toolsDiagnostics.LogNonFatal($"读取 {FileName} 中的命令别名失败", ex);
            return false;
        }
    }

    internal static List<LayerShortcutEntry> ParseMarkdown(string markdown, List<string>? warnings = null)
    {
        warnings ??= new List<string>();
        var rows = new List<LayerShortcutEntry>();
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tableActive = false;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.Length == 0 || !line.StartsWith("|", StringComparison.Ordinal))
            {
                tableActive = false;
                continue;
            }

            var cells = SplitMarkdownTableLine(line);
            if (cells.Count == 0)
                continue;

            var candidateHeaders = BuildHeaderIndex(cells);
            if (candidateHeaders.ContainsKey("alias") && candidateHeaders.ContainsKey("layerName"))
            {
                headers = candidateHeaders;
                tableActive = true;
                continue;
            }

            if (!tableActive)
                continue;

            if (IsMarkdownSeparatorRow(cells))
                continue;

            AddEntriesFromCells(cells, headers, lineIndex + 1, rows, warnings);
        }

        return rows;
    }

    internal static List<PgpAliasDto> ParseCommandAliases(string markdown, List<string>? warnings = null)
    {
        warnings ??= new List<string>();
        var rows = new List<PgpAliasDto>();
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tableActive = false;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.Length == 0 || !line.StartsWith("|", StringComparison.Ordinal))
            {
                tableActive = false;
                continue;
            }

            var cells = SplitMarkdownTableLine(line);
            if (cells.Count == 0)
                continue;

            var candidateHeaders = BuildCommandAliasHeaderIndex(cells);
            if (candidateHeaders.ContainsKey("alias") && candidateHeaders.ContainsKey("command"))
            {
                headers = candidateHeaders;
                tableActive = true;
                continue;
            }

            if (!tableActive)
                continue;

            if (IsMarkdownSeparatorRow(cells))
                continue;

            AddCommandAliasesFromCells(cells, headers, lineIndex + 1, rows, warnings);
        }

        return rows;
    }

    internal static string? ResolveFilePath()
    {
        foreach (var candidate in EnumerateCandidatePaths())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore invalid candidate paths.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { FileName, LegacyFileName })
        {
            foreach (var path in EnumerateCandidatePathsForName(name))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidatePathsForName(string fileName)
    {
        yield return Path.Combine(C_toolsPaths.UserEditableFolder, fileName);
        yield return Path.Combine(C_toolsPaths.UserSiblingFolder, fileName);
        yield return Path.Combine(C_toolsPaths.AppDataRoot, fileName);

        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
            yield break;

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "model", fileName);
            yield return Path.Combine(dir.FullName, fileName);
        }
    }

    private static Dictionary<string, int> BuildHeaderIndex(IReadOnlyList<string> cells)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cells.Count; i++)
        {
            var key = NormalizeHeader(cells[i]);
            if (key.Length == 0 || map.ContainsKey(key))
                continue;
            map[key] = i;
        }

        return map;
    }

    private static Dictionary<string, int> BuildCommandAliasHeaderIndex(IReadOnlyList<string> cells)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cells.Count; i++)
        {
            var key = NormalizeCommandAliasHeader(cells[i]);
            if (key.Length == 0 || map.ContainsKey(key))
                continue;
            map[key] = i;
        }

        return map;
    }

    private static string NormalizeHeader(string value)
    {
        var h = value.Trim().Trim('*', '`').Replace(" ", "").ToLowerInvariant();
        return h switch
        {
            "快捷键" or "快捷命令" or "别名" or "代号" or "alias" or "shortcut" => "alias",
            "图层" or "图层名" or "图层名称" or "layer" or "layername" => "layerName",
            "颜色" or "颜色索引" or "aci" or "color" or "colorindex" => "colorIndex",
            "线型" or "linetype" or "linetypename" => "linetypeName",
            "线宽" or "lineweight" => "lineWeight",
            "说明" or "备注" or "description" or "note" => "description",
            "尺寸标注" or "标注" or "自动标注" or "dimension" or "rundimension" => "dimension",
            "填充样式" or "填充" or "hatch" or "hatchstyle" => "hatchStyle",
            _ => ""
        };
    }

    private static string NormalizeCommandAliasHeader(string value)
    {
        var h = value.Trim().Trim('*', '`').Replace(" ", "").ToLowerInvariant();
        return h switch
        {
            "快捷键" or "快捷命令" or "别名" or "代号" or "alias" or "shortcut" => "alias",
            "命令" or "命令名" or "目标命令" or "command" or "target" or "targetcommand" => "command",
            "说明" or "备注" or "description" or "note" => "description",
            _ => ""
        };
    }

    private static List<string> SplitMarkdownTableLine(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("|", StringComparison.Ordinal))
            text = text[1..];
        if (text.EndsWith("|", StringComparison.Ordinal))
            text = text[..^1];

        var cells = new List<string>();
        var sb = new StringBuilder();
        var escaped = false;
        foreach (var ch in text)
        {
            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '|')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        if (escaped)
            sb.Append('\\');

        cells.Add(sb.ToString().Trim());
        return cells;
    }

    private static bool IsMarkdownSeparatorRow(IReadOnlyList<string> cells)
    {
        foreach (var cell in cells)
        {
            var value = cell.Trim();
            if (value.Length == 0)
                return false;
            if (value.Any(ch => ch != '-' && ch != ':' && ch != ' '))
                return false;
        }

        return true;
    }

    private static void AddEntriesFromCells(
        IReadOnlyList<string> cells,
        IReadOnlyDictionary<string, int> headers,
        int lineNumber,
        List<LayerShortcutEntry> rows,
        List<string> warnings)
    {
        var aliasCell = GetCell(cells, headers, "alias");
        var layerName = GetCell(cells, headers, "layerName");
        if (aliasCell.Length == 0 && layerName.Length == 0)
            return;

        if (aliasCell.Length == 0 || layerName.Length == 0)
        {
            warnings.Add($"第 {lineNumber} 行缺少快捷键或图层名称，已跳过。");
            return;
        }

        var aliases = CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(aliasCell).ToList();
        if (aliases.Count == 0)
        {
            warnings.Add($"第 {lineNumber} 行没有可用快捷键，已跳过。");
            return;
        }

        var colorText = GetCell(cells, headers, "colorIndex");
        var hatchStyle = NormalizeHatchStyle(GetCell(cells, headers, "hatchStyle"));
        foreach (var alias in aliases)
        {
            if (!LayerAliasRules.IsValidGeneratedCommandAlias(alias, out var reason))
            {
                warnings.Add($"第 {lineNumber} 行快捷键 {alias} 无效：{reason}");
                continue;
            }

            rows.Add(new LayerShortcutEntry
            {
                Alias = alias,
                LayerName = layerName,
                ColorIndex = LayerStyleHelper.TryParseAciColor(colorText),
                LinetypeName = EmptyToNull(GetCell(cells, headers, "linetypeName")),
                LineWeight = EmptyToNull(GetCell(cells, headers, "lineWeight")),
                Description = EmptyToNull(GetCell(cells, headers, "description")),
                RunDimensionWhenNoSelection = ParseBool(GetCell(cells, headers, "dimension")) &&
                                              string.IsNullOrWhiteSpace(hatchStyle),
                RunHatchWhenNoSelection = !string.IsNullOrWhiteSpace(hatchStyle),
                HatchStyle = EmptyToNull(hatchStyle)
            });
        }
    }

    private static void AddCommandAliasesFromCells(
        IReadOnlyList<string> cells,
        IReadOnlyDictionary<string, int> headers,
        int lineNumber,
        List<PgpAliasDto> rows,
        List<string> warnings)
    {
        var aliasCell = GetCell(cells, headers, "alias");
        var command = CadPgpMerge.NormalizeTarget(GetCell(cells, headers, "command"));
        if (aliasCell.Length == 0 && command.Length == 0)
            return;

        if (aliasCell.Length == 0 || command.Length == 0)
        {
            warnings.Add($"第 {lineNumber} 行缺少快捷键或命令，已跳过。");
            return;
        }

        var aliases = CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(aliasCell).ToList();
        if (aliases.Count == 0)
        {
            warnings.Add($"第 {lineNumber} 行没有可用命令快捷键，已跳过。");
            return;
        }

        foreach (var alias in aliases)
        {
            if (string.Equals(alias, command, StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(new PgpAliasDto { Alias = alias, Target = command });
        }
    }

    private static string GetCell(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string key)
    {
        if (!headers.TryGetValue(key, out var index) || index < 0 || index >= cells.Count)
            return "";
        return cells[index].Trim();
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBool(string value)
    {
        var v = value.Trim();
        return v.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("是", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("启用", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("开启", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHatchStyle(string value)
    {
        var v = value.Trim();
        if (v.Length == 0)
            return "";
        if (v.StartsWith("{", StringComparison.Ordinal))
            return v;
        if (ParseBool(v) ||
            v.Equals("默认", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return HatchStyleSnapshot.Defaults().ToJson();
        }

        return v;
    }
}
