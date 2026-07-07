using C_toolsShared;

namespace C_toolsBbbPlugin;

internal static class BbbWorkbookReader
{
    internal static (List<BbbExcelDeviceRow> Rows, List<string> Messages) ImportFromPath(string path)
    {
        var rows = new List<BbbExcelDeviceRow>();
        var messages = new List<string>();

        if (!ExcelOoXmlReader.TryReadWorksheet(path, null, out var cellMap, out var minR, out var maxR, out var minC, out var maxC, out var error))
        {
            messages.Add(error ?? "读取 Excel 失败。");
            return (rows, messages);
        }

        var headerWarnings = new List<string>();
        var colMap = ExcelOoXmlReader.BuildHeaderMap(cellMap, minR, minC, maxC,
            text => ClassifyHeader(text, headerWarnings));
        var hasNamedHeaders = colMap.Count > 0;
        if (!hasNamedHeaders)
            messages.Add("未识别到标准表头，已按 A~G 列顺序读取。");
        else
            messages.AddRange(headerWarnings);

        var dataStartRow = hasNamedHeaders ? minR + 1 : minR;
        if (dataStartRow > maxR)
        {
            messages.Add("Excel 中没有数据行。");
            return (rows, messages);
        }

        for (var rowNumber = dataStartRow; rowNumber <= maxR; rowNumber++)
        {
            string deviceCode;
            string deviceName;
            string model;
            string location;
            string systemName;
            string quantity;
            string remark;

            if (hasNamedHeaders)
            {
                deviceCode = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "code");
                deviceName = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "name");
                model = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "model");
                location = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "location");
                systemName = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "system");
                quantity = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "qty");
                remark = ExcelOoXmlReader.GetCellString(cellMap, rowNumber, colMap, "remark");
            }
            else
            {
                deviceCode = ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC);
                deviceName = maxC >= minC + 1 ? ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC + 1) : "";
                model = maxC >= minC + 2 ? ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC + 2) : "";
                location = maxC >= minC + 3 ? ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC + 3) : "";
                systemName = maxC >= minC + 4 ? ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC + 4) : "";
                quantity = maxC >= minC + 5 ? ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC + 5) : "";
                remark = maxC >= minC + 6 ? ExcelOoXmlReader.GetCell(cellMap, rowNumber, minC + 6) : "";
            }

            deviceCode = deviceCode.Trim();
            deviceName = deviceName.Trim();
            model = model.Trim();
            location = location.Trim();
            systemName = systemName.Trim();
            quantity = quantity.Trim();
            remark = remark.Trim();

            if (deviceCode.Length == 0 &&
                deviceName.Length == 0 &&
                model.Length == 0 &&
                location.Length == 0 &&
                systemName.Length == 0)
            {
                continue;
            }

            rows.Add(new BbbExcelDeviceRow
            {
                SourceRowNumber = rowNumber,
                DeviceCode = deviceCode,
                DeviceName = deviceName,
                Model = model,
                Location = location,
                SystemName = systemName,
                Quantity = quantity.Length == 0 ? "1" : quantity,
                Remark = remark
            });
        }

        if (rows.Count == 0)
            messages.Add("未解析到有效设备行。");

        return (rows, messages);
    }

    internal static (List<string> Values, List<string> Messages) ImportDistinctColumnValues(
        string path,
        string sheetName,
        string headerName)
    {
        var values = new List<string>();
        var messages = new List<string>();

        if (!ExcelOoXmlReader.TryReadWorksheet(path, sheetName, out var cellMap, out var minR, out var maxR, out var minC, out var maxC, out var error))
        {
            messages.Add(error ?? "读取 Excel 失败。");
            return (values, messages);
        }

        var normalizedHeader = ExcelOoXmlReader.NormalizeHeader(headerName);
        var headerRow = 0;
        var headerColumn = 0;

        for (var rowNumber = minR; rowNumber <= maxR && headerColumn == 0; rowNumber++)
        {
            for (var columnNumber = minC; columnNumber <= maxC; columnNumber++)
            {
                if (ExcelOoXmlReader.NormalizeHeader(ExcelOoXmlReader.GetCell(cellMap, rowNumber, columnNumber).Trim()) != normalizedHeader)
                    continue;

                headerRow = rowNumber;
                headerColumn = columnNumber;
                break;
            }
        }

        if (headerColumn == 0)
        {
            messages.Add($"工作表\"{sheetName}\"中未找到列\"{headerName}\"。");
            return (values, messages);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var rowNumber = headerRow + 1; rowNumber <= maxR; rowNumber++)
        {
            var value = ExcelOoXmlReader.GetCell(cellMap, rowNumber, headerColumn).Trim();
            if (value.Length == 0 || !seen.Add(value))
                continue;

            values.Add(value);
        }

        if (values.Count == 0)
            messages.Add($"工作表\"{sheetName}\"的列\"{headerName}\"中没有可用数据。");

        return (values, messages);
    }

    private static string? ClassifyHeader(string text, List<string> warnings)
    {
        var n = ExcelOoXmlReader.NormalizeHeader(text);

        // 精确匹配优先
        if (n == "设备位号" || n == "位号" || n == "设备编号" || n == "tag" || n == "code" || n == "deviceid")
            return "code";
        if (n == "设备名称" || n == "name" || n == "devicename" || n == "设备名")
            return "name";
        if (n == "型号规格" || n == "规格型号" || n == "型号" || n == "规格" || n == "model" || n == "type" || n == "spec")
            return "model";
        if (n == "安装位置" || n == "location" || n == "loc" || n == "位置")
            return "location";
        if (n == "系统" || n == "system" || n == "sys")
            return "system";
        if (n == "数量" || n == "qty" || n == "count")
            return "qty";
        if (n == "备注" || n == "说明" || n == "remark" || n == "note")
            return "remark";

        // 模糊匹配降级，发出警告
        if (n.Contains("设备位号") || n.Contains("位号") || n.Contains("设备编号"))
        {
            warnings.Add($"列 [位号] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "code";
        }
        if (n.Contains("设备名称") || n.Contains("设备名"))
        {
            warnings.Add($"列 [设备名称] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "name";
        }
        if (n.Contains("型号规格") || n.Contains("规格型号") || n.Contains("型号") || n.Contains("规格"))
        {
            warnings.Add($"列 [型号规格] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "model";
        }
        if (n.Contains("安装位置"))
        {
            warnings.Add($"列 [安装位置] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "location";
        }
        if (n.Contains("系统"))
        {
            warnings.Add($"列 [系统] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "system";
        }
        if (n.Contains("数量"))
        {
            warnings.Add($"列 [数量] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "qty";
        }
        if (n.Contains("备注") || n.Contains("说明"))
        {
            warnings.Add($"列 [备注] 通过模糊匹配识别（实际表头：\"{text}\"），建议将表头改为标准名称。");
            return "remark";
        }

        return null;
    }
}

internal sealed class BbbExcelDeviceRow
{
    internal int SourceRowNumber { get; set; }
    internal string DeviceCode { get; set; } = "";
    internal string DeviceName { get; set; } = "";
    internal string Model { get; set; } = "";
    internal string Location { get; set; } = "";
    internal string SystemName { get; set; } = "";
    internal string Quantity { get; set; } = "1";
    internal string Remark { get; set; } = "";
}