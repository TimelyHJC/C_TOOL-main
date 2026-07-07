using C_toolsShared;

namespace C_toolsDddPlugin;

/// <summary>从 xlsx 导入文字标注浮窗列表：首行可为表头；无匹配表头时按列顺序。使用 <see cref="ExcelOoXmlReader"/> 读取 OOXML。</summary>
internal static class DddPanelXlsxImport
{
    internal static (
        List<DddRemarkRow>? remarks,
        List<DddPropRow>? props,
        List<DddMaterialRow>? materials,
        List<string> msgs
        ) ImportFromPath(string path, int selectedTabIndex)
    {
        var msgs = new List<string>();

        if (!ExcelOoXmlReader.TryReadFirstSheet(path, out var cellMap, out var minR, out var maxR, out var minC, out var maxC, out var readErr))
        {
            msgs.Add(readErr ?? "读取 Excel 失败。");
            return (null, null, null, msgs);
        }

        var firstRow = minR;
        var lastRow = maxR;
        var firstCol = minC;
        var lastCol = maxC;

        return selectedTabIndex switch
        {
            0 => ImportRemarks(cellMap, firstRow, lastRow, firstCol, lastCol, msgs),
            1 => ImportProps(cellMap, firstRow, lastRow, firstCol, lastCol, msgs),
            2 => ImportMaterials(cellMap, firstRow, lastRow, firstCol, lastCol, msgs),
            _ => (null, null, null, msgs)
        };
    }

    private static (List<DddRemarkRow>?, List<DddPropRow>?, List<DddMaterialRow>?, List<string>) ImportRemarks(
        Dictionary<(int r, int c), string> cellMap,
        int firstRow, int lastRow, int firstCol, int lastCol,
        List<string> msgs)
    {
        var colMap = ExcelOoXmlReader.BuildHeaderMap(cellMap, firstRow, firstCol, lastCol, ClassifyRemarkHeader);
        var hasNamedHeaders = colMap.Count > 0;
        var dataStartRow = hasNamedHeaders ? firstRow + 1 : firstRow;
        if (dataStartRow > lastRow)
        {
            msgs.Add("没有数据行。");
            return (new List<DddRemarkRow>(), null, null, msgs);
        }

        var list = new List<DddRemarkRow>();
        for (var r = dataStartRow; r <= lastRow; r++)
        {
            string text;
            if (hasNamedHeaders)
                text = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "text");
            else
                text = ExcelOoXmlReader.GetCell(cellMap, r, firstCol);

            text = text.Trim();
            if (text.Length == 0)
                continue;
            list.Add(new DddRemarkRow { RemarkText = text });
        }

        if (list.Count == 0)
            msgs.Add("未解析到有效行（至少需要首列有文字内容）。");

        return (list, null, null, msgs);
    }

    private static (List<DddRemarkRow>?, List<DddPropRow>?, List<DddMaterialRow>?, List<string>) ImportProps(
        Dictionary<(int r, int c), string> cellMap,
        int firstRow, int lastRow, int firstCol, int lastCol,
        List<string> msgs)
    {
        var colMap = ExcelOoXmlReader.BuildHeaderMap(cellMap, firstRow, firstCol, lastCol, ClassifyPropHeader);
        var hasNamedHeaders = colMap.Count >= 1;
        var dataStartRow = hasNamedHeaders ? firstRow + 1 : firstRow;
        if (dataStartRow > lastRow)
        {
            msgs.Add("没有数据行。");
            return (null, new List<DddPropRow>(), null, msgs);
        }

        var list = new List<DddPropRow>();
        for (var r = dataStartRow; r <= lastRow; r++)
        {
            string name, price, note;
            if (hasNamedHeaders)
            {
                name = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "itemname");
                price = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "price");
                note = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "note");
            }
            else
            {
                name = ExcelOoXmlReader.GetCell(cellMap, r, firstCol);
                price = lastCol >= firstCol + 1 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 1) : "";
                note = lastCol >= firstCol + 2 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 2) : "";
            }

            name = name.Trim();
            if (name.Length == 0)
                continue;
            list.Add(new DddPropRow { ItemName = name, Price = price.Trim(), Note = note.Trim() });
        }

        if (list.Count == 0)
            msgs.Add("未解析到有效行（至少需要「道具名称」列有内容）。");

        return (null, list, null, msgs);
    }

    private static (List<DddRemarkRow>?, List<DddPropRow>?, List<DddMaterialRow>?, List<string>) ImportMaterials(
        Dictionary<(int r, int c), string> cellMap,
        int firstRow, int lastRow, int firstCol, int lastCol,
        List<string> msgs)
    {
        var colMap = ExcelOoXmlReader.BuildHeaderMap(cellMap, firstRow, firstCol, lastCol, ClassifyMaterialHeader);
        var hasNamedHeaders = colMap.Count >= 1;
        var dataStartRow = hasNamedHeaders ? firstRow + 1 : firstRow;
        if (dataStartRow > lastRow)
        {
            msgs.Add("没有数据行。");
            return (null, null, new List<DddMaterialRow>(), msgs);
        }

        var list = new List<DddMaterialRow>();
        for (var r = dataStartRow; r <= lastRow; r++)
        {
            string name, spec, note;
            if (hasNamedHeaders)
            {
                name = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "material");
                spec = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "spec");
                note = ExcelOoXmlReader.GetCellString(cellMap, r, colMap, "note");
            }
            else
            {
                name = ExcelOoXmlReader.GetCell(cellMap, r, firstCol);
                spec = lastCol >= firstCol + 1 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 1) : "";
                note = lastCol >= firstCol + 2 ? ExcelOoXmlReader.GetCell(cellMap, r, firstCol + 2) : "";
            }

            name = name.Trim();
            if (name.Length == 0)
                continue;
            list.Add(new DddMaterialRow { MaterialName = name, Specification = spec.Trim(), Note = note.Trim() });
        }

        if (list.Count == 0)
            msgs.Add("未解析到有效行（至少需要「材料名称」列有内容）。");

        return (null, null, list, msgs);
    }

    private static string? ClassifyRemarkHeader(string h)
    {
        var n = ExcelOoXmlReader.NormalizeHeader(h);
        if (n.Contains("文字内容") || n == "文字" || n.Contains("备注文字"))
            return "text";
        if (n is "remark" or "text" or "内容")
            return "text";
        return null;
    }

    private static string? ClassifyPropHeader(string h)
    {
        var n = ExcelOoXmlReader.NormalizeHeader(h);
        if (n.Contains("道具名称") || (n.Contains("名称") && !n.Contains("材料")) || n is "itemname" or "name")
            return "itemname";
        if (n.Contains("价格") || n == "price" || n == "单价")
            return "price";
        if (n.Contains("备注") || n == "note" || n == "说明")
            return "note";
        return null;
    }

    private static string? ClassifyMaterialHeader(string h)
    {
        var n = ExcelOoXmlReader.NormalizeHeader(h);
        if (n.Contains("材料名称") || n.Contains("材料名") || n == "material" || (n.Contains("名称") && n.Contains("材料")))
            return "material";
        if (n.Contains("规格") || n == "spec" || n.Contains("型号"))
            return "spec";
        if (n.Contains("备注") || n == "note" || n == "说明")
            return "note";
        return null;
    }
}