using System.Text;

namespace C_toolsPlugin;

/// <summary>保存前检测：图层快捷键重复、命令别名重复、图层与命令共用代号。</summary>
internal static class SaveDuplicateChecker
{
    internal const int MaxLinesPerSection = 48;

    internal sealed class PreSaveDuplicateResult(bool hasIssues, string dialogText)
    {
        internal bool HasIssues { get; } = hasIssues;
        internal string DialogText { get; } = dialogText;
    }

    internal static PreSaveDuplicateResult Analyze(IEnumerable<CommandCatalogRow> rows)
    {
        var list = (rows as IList<CommandCatalogRow> ?? rows.ToList());
        var sb = new StringBuilder();
        var any = false;

        var layerBlock = FormatSection(
            "【图层】同一快捷键对应多个图层名（保存后 LISP 中后者会覆盖前者行为，请改为不同代号）",
            BuildLayerInternalDuplicates(list));
        if (layerBlock.Length > 0)
        {
            any = true;
            sb.AppendLine(layerBlock);
        }

        var cmdBlock = FormatSection(
            "【命令】同一别名指向多个不同命令（写入 PGP 时后者覆盖前者）",
            BuildCommandInternalDuplicates(list));
        if (cmdBlock.Length > 0)
        {
            any = true;
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine(cmdBlock);
        }

        var layerKeys = CatalogLayerMerge.GetLayerShortcutAliasKeys(list);
        var crossBlock = FormatSection(
            "【交叉】代号同时用作图层快捷键与命令别名（保存时以图层为准，下列命令侧同名别名不写 PGP）",
            BuildCrossLayerCommandLines(list, layerKeys));
        if (crossBlock.Length > 0)
        {
            any = true;
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine(crossBlock);
        }

        if (!any)
            return new PreSaveDuplicateResult(false, "");

        sb.AppendLine();
        sb.AppendLine("────────────────────────");
        sb.Append("若仍保存，将按上述规则写入（可能有覆盖）。是否继续？");
        return new PreSaveDuplicateResult(true, sb.ToString().TrimEnd());
    }

    private static string FormatSection(string title, List<string> lines)
    {
        if (lines.Count == 0)
            return "";
        var sb = new StringBuilder();
        sb.AppendLine(title);
        var take = Math.Min(lines.Count, MaxLinesPerSection);
        for (var i = 0; i < take; i++)
            sb.AppendLine("  • " + lines[i]);
        if (lines.Count > take)
            sb.AppendLine($"  … 另有 {lines.Count - take} 条未显示。");
        return sb.ToString().TrimEnd();
    }

    private static bool IsLayerRow(CommandCatalogRow r) =>
        string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal);

    private static List<string> BuildLayerInternalDuplicates(IList<CommandCatalogRow> list)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in list.Where(IsLayerRow))
        {
            var ln = (row.LayerName ?? "").Trim();
            if (ln.Length == 0)
                continue;
            foreach (var a in CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(row.Alias))
            {
                if (a.Length == 0)
                    continue;
                if (!map.TryGetValue(a, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[a] = set;
                }

                set.Add(ln);
            }
        }

        var lines = new List<string>();
        foreach (var kv in map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kv.Value.Count <= 1)
                continue;
            var layers = string.Join("、", kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            lines.Add($"「{kv.Key}」→ 图层：{layers}");
        }

        return lines;
    }

    private static List<string> BuildCommandInternalDuplicates(IList<CommandCatalogRow> list)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in list.Where(r => !IsLayerRow(r)))
        {
            var target = CadPgpMerge.NormalizeTarget(row.CommandName);
            if (target.Length == 0)
                continue;
            foreach (var a in row.EnumerateAliasTokensForCommandSave())
            {
                if (a.Length == 0)
                    continue;
                if (!map.TryGetValue(a, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[a] = set;
                }

                set.Add(target);
            }
        }

        var lines = new List<string>();
        foreach (var kv in map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kv.Value.Count <= 1)
                continue;
            var cmds = string.Join(" / ", kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            lines.Add($"「{kv.Key}」→ 命令：{cmds}");
        }

        return lines;
    }

    private static List<string> BuildCrossLayerCommandLines(
        IList<CommandCatalogRow> list,
        HashSet<string> layerKeys)
    {
        var lines = new List<string>();
        foreach (var key in layerKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var cmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in list.Where(r => !IsLayerRow(r)))
            {
                foreach (var a in CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(row.AliasForCommandSave))
                {
                    if (!string.Equals(a, key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var cmd = (row.CommandName ?? "").Trim();
                    if (cmd.Length > 0)
                        cmds.Add(cmd);
                }
            }

            if (cmds.Count == 0)
                continue;
            var joined = string.Join("、", cmds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            lines.Add($"「{key}」为图层快捷键；命令表中的同名别名对应：{joined}");
        }

        return lines;
    }
}
