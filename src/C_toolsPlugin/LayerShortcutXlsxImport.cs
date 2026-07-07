using C_toolsShared;

namespace C_toolsPlugin;

/// <summary>从 xlsx 导入「图层命令」行：首行为表头，按列名匹配；无匹配列名时按固定顺序 A~G。使用 <see cref="ExcelOoXmlReader"/> 读取 OOXML。</summary>
internal static class LayerShortcutXlsxImport
{
    internal static (List<CommandCatalogRow> Rows, List<string> Messages) ImportFromPath(string path)
    {
        var rows = new List<CommandCatalogRow>();
        var messages = new List<string>();

        if (!ExcelOoXmlReader.TryReadFirstSheet(path, out var cellMap, out var minR, out var maxR, out var minC, out var maxC, out var readErr))
        {
            messages.Add(readErr ?? "读取 Excel 失败。");
            return (rows, messages);
        }

        var firstRow = minR;
        var lastRow = maxR;
        var firstCol = minC;
        var lastCol = maxC;

        var colMap = ExcelOoXmlReader.BuildHeaderMap(cellMap, firstRow, firstCol, lastCol, ClassifyHeader);
        var hasNamedHeaders = colMap.Count > 0;

        var dataStartRow = hasNamedHeaders ? firstRow + 1 : firstRow;
        if (dataStartRow > lastRow)
        {
            messages.Add("没有数据行。");
            return (rows, messages);
        }

        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var r = dataStartRow; r <= lastRow; r++)
        {
            string alias;
            string layerName;
            string color;
            string linetype;
            string lineweight;
            string desc;
            string dimCell;

            if (hasNamedHeaders)
            {
                alias = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "alias");
                layerName = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "layer");
                color = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "color");
                linetype = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "linetype");
                lineweight = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "lineweight");
                desc = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "description");
                dimCell = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "dimension");
            }
            else
            {
                alias = ExcelOoXmlReader.GetCell(cellMap, r, firstCol);
                layerName = ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 1);
                color = lastCol >= firstCol + 2 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 2) : "";
                linetype = lastCol >= firstCol + 3 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 3) : "";
                lineweight = lastCol >= firstCol + 4 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 4) : "";
                desc = lastCol >= firstCol + 5 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 5) : "";
                dimCell = lastCol >= firstCol + 6 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 6) : "";
            }

            layerName = layerName.Trim();
            if (layerName.Length == 0)
                continue;

            var pairKey = $"{CadPgpMerge.NormalizeAlias(alias)}|{layerName}";
            if (!seenPairs.Add(pairKey))
                continue;

            rows.Add(new CommandCatalogRow(PluginCommandIds.LayerShortcutCatalogCommandLabel, "—", "Excel 导入",
                CadCommandCatalogBuilder.TagLayerShortcut)
            {
                Alias = alias.Trim(),
                LayerName = layerName,
                LayerColor = color.Trim(),
                LayerLinetype = linetype.Trim(),
                LayerLineWeight = lineweight.Trim(),
                Description = desc.Trim(),
                LayerRunDimensionWhenNoSelection = TryParseDimensionYes(dimCell),
                IsUserModified = true
            });
        }

        if (rows.Count == 0)
            messages.Add("未解析到有效行（至少需要「图层名称」列有内容）。");

        return (rows, messages);
    }

    private static string? ClassifyHeader(string h)
    {
        var n = ExcelOoXmlReader.NormalizeHeader(h);
        if (n.Contains("图层快捷键") || n == "快捷键" || n == "代号" || n == "alias")
            return "alias";
        if (n.Contains("图层名称") || n.Contains("图层名") || n == "layername" || n == "layer")
            return "layer";
        if (n.Contains("颜色") || n == "aci" || n == "color")
            return "color";
        if (n.Contains("线型") || n == "linetype")
            return "linetype";
        if (n.Contains("线宽") || n == "lineweight")
            return "lineweight";
        if (n.Contains("说明") || n.Contains("备注") || n == "description")
            return "description";
        if (n.Contains("尺寸标注") || n.Contains("启用尺寸") || n == "dimension")
            return "dimension";
        return null;
    }

    private static bool TryParseDimensionYes(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return false;
        if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(s, "是", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(s, "y", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
