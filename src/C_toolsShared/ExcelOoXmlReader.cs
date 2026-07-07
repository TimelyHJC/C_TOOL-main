using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace C_toolsShared;

/// <summary>
/// Excel OOXML (.xlsx) 文件读取工具类。
/// 不依赖 ClosedXML，直接读取 zip+xml 格式。
/// </summary>
public static class ExcelOoXmlReader
{
    /// <summary>SpreadsheetML 命名空间</summary>
    public static readonly XNamespace Sm = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Office Document 关系命名空间</summary>
    public static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Package 关系命名空间</summary>
    public static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// 读取 Excel 文件的第一个工作表，返回单元格映射和边界信息。
    /// </summary>
    /// <param name="path">Excel 文件路径</param>
    /// <param name="cellMap">单元格映射：(行号, 列号) → 字符串值</param>
    /// <param name="minR">最小行号</param>
    /// <param name="maxR">最大行号</param>
    /// <param name="minC">最小列号</param>
    /// <param name="maxC">最大列号</param>
    /// <param name="error">错误消息（失败时）</param>
    /// <returns>是否成功读取</returns>
    public static bool TryReadFirstSheet(
        string path,
        out Dictionary<(int r, int c), string> cellMap,
        out int minR,
        out int maxR,
        out int minC,
        out int maxC,
        out string? error)
    {
        return TryReadWorksheet(path, null, out cellMap, out minR, out maxR, out minC, out maxC, out error);
    }

    /// <summary>
    /// 读取 Excel 文件的指定工作表，返回单元格映射和边界信息。
    /// </summary>
    /// <param name="path">Excel 文件路径</param>
    /// <param name="preferredSheetName">指定工作表名称（null 表示第一个工作表）</param>
    /// <param name="cellMap">单元格映射：(行号, 列号) → 字符串值</param>
    /// <param name="minR">最小行号</param>
    /// <param name="maxR">最大行号</param>
    /// <param name="minC">最小列号</param>
    /// <param name="maxC">最大列号</param>
    /// <param name="error">错误消息（失败时）</param>
    /// <returns>是否成功读取</returns>
    public static bool TryReadWorksheet(
        string path,
        string? preferredSheetName,
        out Dictionary<(int r, int c), string> cellMap,
        out int minR,
        out int maxR,
        out int minC,
        out int maxC,
        out string? error)
    {
        cellMap = new Dictionary<(int, int), string>();
        minR = int.MaxValue;
        maxR = int.MinValue;
        minC = int.MaxValue;
        maxC = int.MinValue;
        error = null;

        try
        {
            using var fileStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);

            var sharedStrings = ReadSharedStrings(zip);
            var worksheetEntry = GetWorksheetEntry(zip, preferredSheetName);
            if (worksheetEntry == null)
            {
                error = string.IsNullOrWhiteSpace(preferredSheetName)
                    ? "工作簿中没有任何工作表。"
                    : $"未找到工作表\"{preferredSheetName}\"。";
                return false;
            }

            XDocument worksheetDocument;
            using (var stream = worksheetEntry.Open())
                worksheetDocument = XDocument.Load(stream);

            var sheetData = worksheetDocument.Root?.Element(Sm + "sheetData");
            if (sheetData == null)
            {
                error = "工作表为空。";
                return false;
            }

            var rowIndex = 0;
            foreach (var rowElement in sheetData.Elements(Sm + "row"))
            {
                var rowAttribute = rowElement.Attribute("r");
                if (rowAttribute != null &&
                    int.TryParse(rowAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitRow))
                {
                    rowIndex = explicitRow;
                }
                else
                {
                    rowIndex++;
                }

                foreach (var cell in rowElement.Elements(Sm + "c"))
                {
                    var address = cell.Attribute("r")?.Value;
                    if (string.IsNullOrWhiteSpace(address) || !TryParseCellRef(address!, out var col, out var row))
                        continue;

                    cellMap[(row, col)] = FormatCellValue(cell, sharedStrings);

                    if (row < minR) minR = row;
                    if (row > maxR) maxR = row;
                    if (col < minC) minC = col;
                    if (col > maxC) maxC = col;
                }
            }

            if (cellMap.Count == 0)
            {
                error = "工作表为空。";
                return false;
            }

            return true;
        }
        catch (InvalidDataException)
        {
            error = "不是有效的 .xlsx 文件（需为 Excel 2007+ 格式）。";
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取共享字符串表。
    /// </summary>
    /// <param name="zip">Excel zip 包</param>
    /// <returns>共享字符串列表</returns>
    public static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var sharedStrings = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null)
            return sharedStrings;

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var root = document.Root;
        if (root == null)
            return sharedStrings;

        foreach (var sharedItem in root.Elements(Sm + "si"))
        {
            var parts = sharedItem.Descendants(Sm + "t").Select(x => (string)x).ToList();
            sharedStrings.Add(parts.Count == 0 ? "" : string.Concat(parts));
        }

        return sharedStrings;
    }

    /// <summary>
    /// 获取工作表入口。
    /// </summary>
    /// <param name="zip">Excel zip 包</param>
    /// <param name="preferredSheetName">指定工作表名称（null 表示第一个工作表）</param>
    /// <returns>工作表 ZipArchiveEntry，或 null</returns>
    public static ZipArchiveEntry? GetWorksheetEntry(ZipArchive zip, string? preferredSheetName)
    {
        var fallback = zip.GetEntry("xl/worksheets/sheet1.xml");
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        if (workbookEntry == null)
            return string.IsNullOrWhiteSpace(preferredSheetName) ? fallback : null;

        XDocument workbookDocument;
        using (var stream = workbookEntry.Open())
            workbookDocument = XDocument.Load(stream);

        var sheets = workbookDocument.Root?.Element(Sm + "sheets")?.Elements(Sm + "sheet");
        var selectedSheet = string.IsNullOrWhiteSpace(preferredSheetName)
            ? sheets?.FirstOrDefault()
            : sheets?.FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), preferredSheetName, StringComparison.OrdinalIgnoreCase));
        var relationshipId = selectedSheet?.Attribute(Rel + "id")?.Value;
        if (string.IsNullOrWhiteSpace(relationshipId))
            return string.IsNullOrWhiteSpace(preferredSheetName) ? fallback : null;

        var relationsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relationsEntry == null)
            return string.IsNullOrWhiteSpace(preferredSheetName) ? fallback : null;

        XDocument relationsDocument;
        using (var stream = relationsEntry.Open())
            relationsDocument = XDocument.Load(stream);

        var relation = relationsDocument.Root?.Elements(Pr + "Relationship")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("Id"), relationshipId, StringComparison.Ordinal));
        var target = (string?)relation?.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target))
            return string.IsNullOrWhiteSpace(preferredSheetName) ? fallback : null;

        target = target!.Replace('\\', '/').TrimStart('/');
        var fullPath = target.IndexOf("xl/", StringComparison.OrdinalIgnoreCase) == 0 ? target : "xl/" + target;
        return zip.GetEntry(fullPath) ?? (string.IsNullOrWhiteSpace(preferredSheetName) ? fallback : null);
    }

    /// <summary>
    /// 格式化单元格值。
    /// </summary>
    /// <param name="cell">单元格 XElement</param>
    /// <param name="sharedStrings">共享字符串列表</param>
    /// <returns>单元格字符串值</returns>
    public static string FormatCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var cellType = (string?)cell.Attribute("t");
        var valueElement = cell.Element(Sm + "v");
        var inlineStringElement = cell.Element(Sm + "is");

        // 内联字符串
        if (string.Equals(cellType, "inlineStr", StringComparison.Ordinal) && inlineStringElement != null)
        {
            var texts = inlineStringElement.Descendants(Sm + "t").Select(x => (string)x).ToArray();
            return string.Concat(texts).Trim();
        }

        // 共享字符串索引
        if (string.Equals(cellType, "s", StringComparison.Ordinal) &&
            valueElement != null &&
            int.TryParse(valueElement.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex) &&
            sharedIndex >= 0 &&
            sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex].Trim();
        }

        // 布尔值
        if (string.Equals(cellType, "b", StringComparison.Ordinal) && valueElement != null)
            return valueElement.Value == "1" ? "1" : "0";

        // 数值
        if (valueElement != null)
        {
            if (double.TryParse(valueElement.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                // 整数显示为整数形式
                if (number >= int.MinValue && number <= int.MaxValue && Math.Abs(number - Math.Round(number)) < 1e-9)
                    return ((int)Math.Round(number)).ToString(CultureInfo.InvariantCulture);

                return number.ToString(CultureInfo.InvariantCulture);
            }

            return valueElement.Value.Trim();
        }

        return "";
    }

    /// <summary>
    /// 解析单元格引用（如 "A1", "BC23"）为行列号。
    /// </summary>
    /// <param name="reference">单元格引用字符串</param>
    /// <param name="col">列号（1-based）</param>
    /// <param name="row">行号（1-based）</param>
    /// <returns>是否解析成功</returns>
    public static bool TryParseCellRef(string reference, out int col, out int row)
    {
        col = 0;
        row = 0;

        var index = 0;
        while (index < reference.Length && char.IsLetter(reference[index]))
            index++;

        if (index == 0 || index >= reference.Length)
            return false;

        var letters = reference.Substring(0, index).ToUpperInvariant();
        foreach (var letter in letters)
        {
            if (letter is < 'A' or > 'Z')
                return false;
            col = col * 26 + (letter - 'A' + 1);
        }

        return int.TryParse(reference.Substring(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out row);
    }

    /// <summary>
    /// 从单元格映射中获取指定位置的值。
    /// </summary>
    /// <param name="map">单元格映射</param>
    /// <param name="row">行号</param>
    /// <param name="col">列号</param>
    /// <returns>单元格值，不存在则返回空字符串</returns>
    public static string GetCell(IReadOnlyDictionary<(int r, int c), string> map, int row, int col) =>
        map.TryGetValue((row, col), out var value) ? value : "";

    /// <summary>
    /// 通过列名映射获取单元格值。
    /// </summary>
    /// <param name="map">单元格映射</param>
    /// <param name="row">行号</param>
    /// <param name="columnMap">列名到列号的映射</param>
    /// <param name="key">列名键</param>
    /// <returns>单元格值，不存在则返回空字符串</returns>
    public static string GetCellString(
        IReadOnlyDictionary<(int r, int c), string> map,
        int row,
        IReadOnlyDictionary<string, int> columnMap,
        string key) =>
        !columnMap.TryGetValue(key, out var column) ? "" : GetCell(map, row, column);

    /// <summary>
    /// 构建表头行到列名的映射。
    /// </summary>
    /// <param name="cellMap">单元格映射</param>
    /// <param name="headerRow">表头行号</param>
    /// <param name="minC">最小列号</param>
    /// <param name="maxC">最大列号</param>
    /// <param name="classifyHeader">表头分类函数</param>
    /// <returns>列名到列号的映射</returns>
    public static Dictionary<string, int> BuildHeaderMap(
        IReadOnlyDictionary<(int r, int c), string> cellMap,
        int headerRow,
        int minC,
        int maxC,
        Func<string, string?> classifyHeader)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var col = minC; col <= maxC; col++)
        {
            var raw = GetCell(cellMap, headerRow, col).Trim();
            if (raw.Length == 0)
                continue;

            var key = classifyHeader(raw);
            if (key != null && !map.ContainsKey(key))
                map[key] = col;
        }

        return map;
    }

    /// <summary>
    /// 标准化表头字符串（去除空格、特殊字符，转小写）。
    /// </summary>
    /// <param name="text">原始表头文本</param>
    /// <returns>标准化后的字符串</returns>
    public static string NormalizeHeader(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var chars = new List<char>(text!.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/' || ch == '\\' ||
                ch == '（' || ch == '）' || ch == '(' || ch == ')' || ch == ':' || ch == '：')
                continue;
            chars.Add(char.ToLowerInvariant(ch));
        }

        return new string(chars.ToArray());
    }
}