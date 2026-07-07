using System.Text;

namespace C_toolsPlugin;

/// <summary>保存成功弹窗摘要：仅列出本次编辑过的最短结果行，避免分组标题和技术附录。</summary>
internal static class SaveSummaryBuilder
{
    internal const int MaxDetailLines = 36;

    internal static string Build(IEnumerable<CommandCatalogRow> rows)
    {
        var all = rows as IList<CommandCatalogRow> ?? rows.ToList();
        var layerKeys = CatalogLayerMerge.GetLayerShortcutAliasKeys(all);
        var forSummary = all.Where(r => r.IsUserModified);
        var layerLines = new List<string>();
        var cmdLines = new List<string>();
        foreach (var row in forSummary)
        {
            if (string.Equals(row.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
            {
                var tokens = CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(row.Alias).ToList();
                var ln = (row.LayerName ?? "").Trim();
                if (tokens.Count == 0 || ln.Length == 0)
                    continue;
                layerLines.Add($"{string.Join("、", tokens)}  {ln}");
                continue;
            }

            var target = CadPgpMerge.NormalizeTarget(row.CommandName);
            if (target.Length == 0)
                continue;
            var parts = row.EnumerateAliasTokensForCommandSave().ToList();
            if (parts.Count == 0)
                continue;
            parts = parts.Where(p => !layerKeys.Contains(p)).ToList();
            if (parts.Count == 0)
                continue;
            var aliasJoined = string.Join("、", parts);
            var hintCmd = string.IsNullOrWhiteSpace(row.Description)
                ? (row.CommandName ?? "").Trim()
                : row.Description.Trim();
            cmdLines.Add($"{aliasJoined}  {hintCmd}");
        }

        var lines = new List<string>();
        lines.AddRange(layerLines);
        lines.AddRange(cmdLines);

        var sb = new StringBuilder();
        if (lines.Count == 0)
        {
            sb.AppendLine("已保存。");
        }
        else
        {
            var total = lines.Count;
            var take = Math.Min(lines.Count, MaxDetailLines);
            for (var i = 0; i < take; i++)
                sb.AppendLine(lines[i]);
            if (total > take)
                sb.AppendLine($"… 共 {total} 条，此处仅显示前 {take} 条。");
        }

        return sb.ToString().TrimEnd();
    }
}
