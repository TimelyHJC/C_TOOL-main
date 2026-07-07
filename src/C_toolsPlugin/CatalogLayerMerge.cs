using System.Collections.Generic;

namespace C_toolsPlugin;

/// <summary>
/// 将 layer_shortcuts.json 合并进命令表：去掉与图层别名冲突的命令行，再追加图层配置行。
/// </summary>
internal static class CatalogLayerMerge
{
    /// <summary>读入并筛出可参与目录的图层快捷键条目（一次刷新内应只调用一次 <see cref="LayerShortcutStore.Load"/>）。</summary>
    internal static List<LayerShortcutEntry> LoadValidLayerShortcutEntries()
    {
        return LayerShortcutStore.Load()
            .Where(x => CadPgpMerge.NormalizeAlias(x.Alias).Length > 0 && !string.IsNullOrWhiteSpace(x.LayerName))
            .ToList();
    }

    /// <summary>仅由已载入条目生成图层命令行（首屏快速显示，不经 PGP 合并）。</summary>
    internal static List<CommandCatalogRow> BuildLayerShortcutRowsOnly(IReadOnlyList<LayerShortcutEntry> layerEntries)
    {
        var rows = new List<CommandCatalogRow>(layerEntries.Count);
        foreach (var e in layerEntries.OrderBy(x => CadPgpMerge.NormalizeAlias(x.Alias), StringComparer.OrdinalIgnoreCase))
        {
            var a = CadPgpMerge.NormalizeAlias(e.Alias);
            var dim = e.RunDimensionWhenNoSelection;
            if (!string.IsNullOrWhiteSpace(e.HatchStyle))
                dim = false;
            rows.Add(new CommandCatalogRow(PluginCommandIds.LayerShortcutCatalogCommandLabel, "—", "C_TOOL图层", CadCommandCatalogBuilder.TagLayerShortcut)
            {
                Alias = a,
                LayerName = e.LayerName.Trim(),
                LayerColor = e.ColorIndex?.ToString() ?? "",
                LayerLinetype = e.LinetypeName ?? "",
                LayerLineWeight = e.LineWeight ?? "",
                LayerRunDimensionWhenNoSelection = dim,
                LayerHatchStyleJson = e.HatchStyle ?? "",
                Description = (e.Description ?? "").Trim()
            });
        }

        return rows;
    }

    /// <summary>
    /// 「图层命令」中已填写的代号（规范化后）。与命令表别名冲突时，保存 PGP 以图层为准。
    /// </summary>
    internal static HashSet<string> GetLayerShortcutAliasKeys(IEnumerable<CommandCatalogRow> rows)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.Equals(row.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                continue;
            foreach (var a in CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(row.Alias))
                set.Add(a);
        }

        return set;
    }

    /// <param name="preloadedLayerEntries">若本刷新已读过 <see cref="LayerShortcutStore"/>，传入避免二次读盘。</param>
    internal static void AppendLayerShortcutRows(List<CommandCatalogRow> rows,
        IReadOnlyList<LayerShortcutEntry>? preloadedLayerEntries = null)
    {
        IReadOnlyList<LayerShortcutEntry> layerEntries = preloadedLayerEntries ?? LoadValidLayerShortcutEntries();
        if (layerEntries.Count == 0)
            return;

        rows.RemoveAll(r =>
            string.Equals(r.CommandName, PluginCommandIds.LayerShortcutCatalogCommandLabel, StringComparison.OrdinalIgnoreCase)
            || CadPgpMerge.MatchesDeprecatedLayerMacroToken(r.CommandName ?? ""));

        var aliasKeys = new HashSet<string>(
            layerEntries.Select(x => CadPgpMerge.NormalizeAlias(x.Alias)),
            StringComparer.OrdinalIgnoreCase);

        rows.RemoveAll(r =>
        {
            var cn = CadPgpMerge.NormalizeAlias(r.CommandName);
            return cn.Length > 0 && aliasKeys.Contains(cn);
        });

        foreach (var e in layerEntries.OrderBy(x => CadPgpMerge.NormalizeAlias(x.Alias), StringComparer.OrdinalIgnoreCase))
        {
            var a = CadPgpMerge.NormalizeAlias(e.Alias);
            var dim = e.RunDimensionWhenNoSelection;
            if (!string.IsNullOrWhiteSpace(e.HatchStyle))
                dim = false;
            rows.Add(new CommandCatalogRow(PluginCommandIds.LayerShortcutCatalogCommandLabel, "—", "C_TOOL图层", CadCommandCatalogBuilder.TagLayerShortcut)
            {
                Alias = a,
                LayerName = e.LayerName.Trim(),
                LayerColor = e.ColorIndex?.ToString() ?? "",
                LayerLinetype = e.LinetypeName ?? "",
                LayerLineWeight = e.LineWeight ?? "",
                LayerRunDimensionWhenNoSelection = dim,
                LayerHatchStyleJson = e.HatchStyle ?? "",
                Description = (e.Description ?? "").Trim()
            });
        }
    }
}
