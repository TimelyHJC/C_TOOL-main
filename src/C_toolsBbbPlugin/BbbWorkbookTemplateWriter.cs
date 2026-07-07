using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace C_toolsBbbPlugin;

internal sealed class BbbWorkbookOutputRow
{
    internal string DeviceName { get; set; } = "";
    internal string QuantityText { get; set; } = "";
}

internal sealed class BbbWorkbookOutputResult
{
    internal bool Success { get; set; }
    internal string Message { get; set; } = "";
    internal string WorksheetName { get; set; } = "";
    internal int WrittenRowCount { get; set; }
}

internal static class BbbWorkbookTemplateWriter
{
    private const string PreferredWorksheetName = "首批模板";
    private const int OutputStartRowNumber = 9;
    private const int TemplateSourceRowNumber = 15;
    private const int SequenceColumn = 1;
    private const int TemplateStartColumn = 2;
    private const int TemplateEndColumn = 7;
    private const int DeviceNameColumn = 2;
    private const int QuantityColumn = 4;
    private const string StylesEntryPath = "xl/styles.xml";
    private static readonly string TemplateRangeText = $"B{TemplateSourceRowNumber}:G{TemplateSourceRowNumber}";
    private static readonly string OutputStartCellText = $"A{OutputStartRowNumber}";

    private static readonly Regex CellReferenceRegex = new(
        @"(?<![A-Za-z0-9_])(\$?[A-Za-z]{1,3})(\$?)(\d+)",
        RegexOptions.Compiled);

    private static readonly XNamespace Sm = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static BbbWorkbookOutputResult WriteSummaryRows(string path, IReadOnlyList<BbbWorkbookOutputRow> rows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new BbbWorkbookOutputResult
            {
                Message = "Excel 模板路径为空。"
            };
        }

        if (!File.Exists(path))
        {
            return new BbbWorkbookOutputResult
            {
                Message = $"Excel 模板不存在：{path}"
            };
        }

        try
        {
            using var fileStream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: false);

            if (!TryLoadStyleContext(zip, out var styleContext, out var styleError))
            {
                return new BbbWorkbookOutputResult
                {
                    Message = styleError ?? "Excel 模板样式读取失败。"
                };
            }

            if (!TryOpenTemplateWorksheet(zip, out var worksheetContext, out var worksheetError))
            {
                return new BbbWorkbookOutputResult
                {
                    Message = worksheetError ?? $"Excel 模板中未找到 {TemplateRangeText} 输出区。"
                };
            }

            var sheetData = worksheetContext.WorksheetDocument.Root?.Element(Sm + "sheetData");
            if (sheetData == null)
            {
                return new BbbWorkbookOutputResult
                {
                    WorksheetName = worksheetContext.SheetName,
                    Message = "工作表为空。"
                };
            }

            worksheetContext.WritableRows.Clear();
            if (rows.Count > 0)
            {
                for (var index = 0; index < rows.Count; index++)
                    worksheetContext.WritableRows.Add(OutputStartRowNumber + index);

                var additionalRows = Math.Max(0, rows.Count - 1);
                if (additionalRows > 0)
                    ShiftRows(sheetData, OutputStartRowNumber + 1, additionalRows);
            }

            WriteRows(worksheetContext, styleContext, rows);
            if (rows.Count > 0)
            {
                var templateDeleteRow = TemplateSourceRowNumber + Math.Max(0, rows.Count - 1);
                DeleteRow(sheetData, templateDeleteRow);
            }

            SaveStyles(zip, styleContext);
            SaveWorksheet(zip, worksheetContext);

            return new BbbWorkbookOutputResult
            {
                Success = true,
                WorksheetName = worksheetContext.SheetName,
                WrittenRowCount = rows.Count,
                Message = $"已写入 Excel：工作表“{worksheetContext.SheetName}”，按 {TemplateRangeText} 模板输出 {rows.Count} 种设备。"
            };
        }
        catch (InvalidDataException)
        {
            return new BbbWorkbookOutputResult
            {
                Message = "不是有效的 .xlsx 文件（需为 Excel 2007+ 格式）。"
            };
        }
        catch (IOException ex)
        {
            return new BbbWorkbookOutputResult
            {
                Message = ex.Message
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new BbbWorkbookOutputResult
            {
                Message = ex.Message
            };
        }
        catch (Exception ex)
        {
            return new BbbWorkbookOutputResult
            {
                Message = ex.Message
            };
        }
    }

    private static bool TryOpenTemplateWorksheet(
        ZipArchive zip,
        out BbbTemplateWorksheetContext context,
        out string? error)
    {
        context = new BbbTemplateWorksheetContext();
        error = null;

        var worksheets = GetWorksheetDescriptors(zip);
        if (worksheets.Count == 0)
        {
            error = "Excel 工作簿中没有任何工作表。";
            return false;
        }

        var orderedWorksheets = worksheets
            .OrderBy(GetWorksheetPriority)
            .ThenBy(x => x.SheetName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var worksheet in orderedWorksheets)
        {
            var entry = zip.GetEntry(worksheet.EntryPath);
            if (entry == null)
                continue;

            XDocument document;
            using (var stream = entry.Open())
                document = XDocument.Load(stream);

            if (!TryCreateWorksheetContext(worksheet, document, out context))
                continue;

            return true;
        }

        error = $"Excel 模板中未在任意工作表找到 {TemplateRangeText} 模板行。";
        return false;
    }

    private static int GetWorksheetPriority(BbbWorksheetDescriptor worksheet)
    {
        var sheetName = (worksheet.SheetName ?? "").Trim();
        if (string.Equals(sheetName, PreferredWorksheetName, StringComparison.CurrentCultureIgnoreCase))
            return 0;

        if (sheetName.IndexOf("说明", StringComparison.CurrentCultureIgnoreCase) >= 0)
            return 2;

        return 1;
    }

    private static bool TryCreateWorksheetContext(
        BbbWorksheetDescriptor worksheet,
        XDocument worksheetDocument,
        out BbbTemplateWorksheetContext context)
    {
        context = new BbbTemplateWorksheetContext();
        var sheetData = worksheetDocument.Root?.Element(Sm + "sheetData");
        if (sheetData == null)
            return false;

        var templateRow = FindRow(sheetData, TemplateSourceRowNumber);
        if (templateRow == null)
            return false;

        var templateCells = new Dictionary<int, XElement>();
        for (var columnNumber = TemplateStartColumn; columnNumber <= TemplateEndColumn; columnNumber++)
        {
            var templateCell = FindCell(templateRow, columnNumber);
            if (templateCell != null)
                templateCells[columnNumber] = new XElement(templateCell);
        }

        if (templateCells.Count == 0)
            return false;

        var sequenceTemplateRow = FindRow(sheetData, OutputStartRowNumber);
        var sequenceTemplateCell = sequenceTemplateRow == null
            ? FindCell(templateRow, SequenceColumn)
            : FindCell(sequenceTemplateRow, SequenceColumn) ?? FindCell(templateRow, SequenceColumn);

        context = new BbbTemplateWorksheetContext
        {
            SheetName = worksheet.SheetName,
            EntryPath = worksheet.EntryPath,
            WorksheetDocument = worksheetDocument,
            TemplateRow = new XElement(templateRow),
            TemplateCells = templateCells,
            SequenceTemplateCell = sequenceTemplateCell == null ? null : new XElement(sequenceTemplateCell)
        };
        return true;
    }

    private static void WriteRows(
        BbbTemplateWorksheetContext context,
        BbbWorkbookStyleContext styleContext,
        IReadOnlyList<BbbWorkbookOutputRow> rows)
    {
        var sheetData = context.WorksheetDocument.Root?.Element(Sm + "sheetData");
        if (sheetData == null)
            throw new InvalidOperationException("工作表为空。");

        for (var index = 0; index < context.WritableRows.Count; index++)
        {
            var rowNumber = context.WritableRows[index];
            var outputRow = index < rows.Count ? rows[index] : null;

            var rowElement = GetOrCreateRow(sheetData, rowNumber);
            ApplyTemplateRowAttributes(context.TemplateRow, rowElement, rowNumber);

            for (var columnNumber = TemplateStartColumn; columnNumber <= TemplateEndColumn; columnNumber++)
            {
                var newCell = BuildTemplateCell(context, styleContext, columnNumber, rowNumber);
                ReplaceCell(rowElement, newCell, columnNumber);
            }

            var nameCell = FindCell(rowElement, DeviceNameColumn) ?? throw new InvalidOperationException("未能创建设备名称单元格。");
            var quantityCell = FindCell(rowElement, QuantityColumn) ?? throw new InvalidOperationException("未能创建数量单元格。");
            var sequenceCell = BuildSequenceCell(context, styleContext, rowNumber, index + 1);
            ReplaceCell(rowElement, sequenceCell, SequenceColumn);

            SetCellText(nameCell, outputRow?.DeviceName ?? "");
            SetCellNumberOrText(quantityCell, outputRow?.QuantityText ?? "");
        }
    }

    private static XElement BuildTemplateCell(
        BbbTemplateWorksheetContext context,
        BbbWorkbookStyleContext styleContext,
        int columnNumber,
        int targetRowNumber)
    {
        var templateCell = context.TemplateCells.TryGetValue(columnNumber, out var cell)
            ? new XElement(cell)
            : new XElement(Sm + "c");

        templateCell.SetAttributeValue("r", BuildCellReference(columnNumber, targetRowNumber));
        ApplySongtiBoldStyle(styleContext, templateCell);

        var formulaElement = templateCell.Element(Sm + "f");
        if (formulaElement != null)
            NormalizeFormulaElement(formulaElement, targetRowNumber - TemplateSourceRowNumber);

        return templateCell;
    }

    private static XElement BuildSequenceCell(
        BbbTemplateWorksheetContext context,
        BbbWorkbookStyleContext styleContext,
        int targetRowNumber,
        int sequenceNumber)
    {
        var templateCell = context.SequenceTemplateCell == null
            ? new XElement(Sm + "c")
            : new XElement(context.SequenceTemplateCell);

        templateCell.SetAttributeValue("r", BuildCellReference(SequenceColumn, targetRowNumber));
        ApplySongtiBoldStyle(styleContext, templateCell);
        SetCellInteger(templateCell, sequenceNumber);
        return templateCell;
    }

    private static void ApplyTemplateRowAttributes(XElement templateRow, XElement rowElement, int rowNumber)
    {
        rowElement.ReplaceAttributes(
            templateRow.Attributes()
                .Where(x => x.Name.LocalName != "r")
                .Select(x => new XAttribute(x)));

        rowElement.SetAttributeValue("r", rowNumber.ToString(CultureInfo.InvariantCulture));
    }

    private static void NormalizeFormulaElement(XElement formulaElement, int rowOffset)
    {
        formulaElement.Attributes("t").Remove();
        formulaElement.Attributes("si").Remove();
        formulaElement.Attributes("ref").Remove();

        var formulaText = formulaElement.Value;
        if (formulaText.Length == 0 || rowOffset == 0)
            return;

        formulaElement.Value = ShiftFormulaRows(formulaText, rowOffset);
    }

    private static string ShiftFormulaRows(string formula, int rowOffset)
    {
        return CellReferenceRegex.Replace(formula, match =>
        {
            var columnPart = match.Groups[1].Value;
            var rowMarker = match.Groups[2].Value;
            if (rowMarker == "$")
                return match.Value;

            if (!int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowNumber))
                return match.Value;

            return columnPart + (rowNumber + rowOffset).ToString(CultureInfo.InvariantCulture);
        });
    }

    private static void ApplySongtiBoldStyle(BbbWorkbookStyleContext styleContext, XElement cell)
    {
        var baseStyleId = 0;
        var styleText = (string?)cell.Attribute("s");
        if (!string.IsNullOrWhiteSpace(styleText) &&
            int.TryParse(styleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStyleId) &&
            parsedStyleId >= 0)
        {
            baseStyleId = parsedStyleId;
        }

        var styleId = EnsureSongtiBoldStyle(styleContext, baseStyleId);
        cell.SetAttributeValue("s", styleId.ToString(CultureInfo.InvariantCulture));
    }

    private static int EnsureSongtiBoldStyle(BbbWorkbookStyleContext styleContext, int baseStyleId)
    {
        if (styleContext.SongtiBoldStyleIds.TryGetValue(baseStyleId, out var cachedStyleId))
            return cachedStyleId;

        var cellFormats = styleContext.CellFormatsElement.Elements(Sm + "xf").ToList();
        if (cellFormats.Count == 0)
        {
            var defaultFormat = new XElement(Sm + "xf",
                new XAttribute("numFmtId", "0"),
                new XAttribute("fontId", "0"),
                new XAttribute("fillId", "0"),
                new XAttribute("borderId", "0"),
                new XAttribute("xfId", "0"));
            styleContext.CellFormatsElement.Add(defaultFormat);
            cellFormats.Add(defaultFormat);
        }

        var safeBaseStyleId = Clamp(baseStyleId, 0, cellFormats.Count - 1);
        var baseFormat = cellFormats[safeBaseStyleId];
        var baseFontId = 0;
        var baseFontText = (string?)baseFormat.Attribute("fontId");
        if (!string.IsNullOrWhiteSpace(baseFontText) &&
            int.TryParse(baseFontText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFontId) &&
            parsedFontId >= 0)
        {
            baseFontId = parsedFontId;
        }

        var songtiBoldFontId = EnsureSongtiBoldFont(styleContext, baseFontId);
        var newFormat = new XElement(baseFormat);
        newFormat.SetAttributeValue("fontId", songtiBoldFontId.ToString(CultureInfo.InvariantCulture));
        newFormat.SetAttributeValue("applyFont", "1");

        styleContext.CellFormatsElement.Add(newFormat);
        UpdateCountAttribute(styleContext.CellFormatsElement);

        var newStyleId = styleContext.CellFormatsElement.Elements(Sm + "xf").Count() - 1;
        styleContext.SongtiBoldStyleIds[baseStyleId] = newStyleId;
        return newStyleId;
    }

    private static int EnsureSongtiBoldFont(BbbWorkbookStyleContext styleContext, int baseFontId)
    {
        if (styleContext.SongtiBoldFontIds.TryGetValue(baseFontId, out var cachedFontId))
            return cachedFontId;

        var fonts = styleContext.FontsElement.Elements(Sm + "font").ToList();
        if (fonts.Count == 0)
        {
            var defaultFont = new XElement(Sm + "font",
                new XElement(Sm + "sz", new XAttribute("val", "11")),
                new XElement(Sm + "name", new XAttribute("val", "Calibri")));
            styleContext.FontsElement.Add(defaultFont);
            fonts.Add(defaultFont);
        }

        var safeBaseFontId = Clamp(baseFontId, 0, fonts.Count - 1);
        var newFont = new XElement(fonts[safeBaseFontId]);
        newFont.Elements(Sm + "name").Remove();
        newFont.Elements(Sm + "b").Remove();
        newFont.Elements(Sm + "charset").Remove();
        newFont.Elements(Sm + "scheme").Remove();

        newFont.Add(new XElement(Sm + "name", new XAttribute("val", "宋体")));
        newFont.Add(new XElement(Sm + "charset", new XAttribute("val", "134")));
        newFont.AddFirst(new XElement(Sm + "b"));

        styleContext.FontsElement.Add(newFont);
        UpdateCountAttribute(styleContext.FontsElement);

        var newFontId = styleContext.FontsElement.Elements(Sm + "font").Count() - 1;
        styleContext.SongtiBoldFontIds[baseFontId] = newFontId;
        return newFontId;
    }

    private static bool TryLoadStyleContext(
        ZipArchive zip,
        out BbbWorkbookStyleContext styleContext,
        out string? error)
    {
        styleContext = new BbbWorkbookStyleContext();
        error = null;

        var entry = zip.GetEntry(StylesEntryPath);
        if (entry == null)
        {
            error = "Excel 模板缺少 styles.xml，无法应用宋体加粗样式。";
            return false;
        }

        XDocument document;
        using (var stream = entry.Open())
            document = XDocument.Load(stream);

        var root = document.Root;
        if (root == null)
        {
            error = "Excel 模板样式为空。";
            return false;
        }

        var fontsElement = root.Element(Sm + "fonts");
        if (fontsElement == null)
        {
            fontsElement = new XElement(Sm + "fonts", new XAttribute("count", "0"));
            root.AddFirst(fontsElement);
        }

        var cellFormatsElement = root.Element(Sm + "cellXfs");
        if (cellFormatsElement == null)
        {
            cellFormatsElement = new XElement(Sm + "cellXfs", new XAttribute("count", "0"));
            root.Add(cellFormatsElement);
        }

        styleContext = new BbbWorkbookStyleContext
        {
            StylesDocument = document,
            FontsElement = fontsElement,
            CellFormatsElement = cellFormatsElement
        };
        return true;
    }

    private static void SaveStyles(ZipArchive zip, BbbWorkbookStyleContext styleContext)
    {
        UpdateCountAttribute(styleContext.FontsElement);
        UpdateCountAttribute(styleContext.CellFormatsElement);

        var existingEntry = zip.GetEntry(StylesEntryPath);
        existingEntry?.Delete();

        var newEntry = zip.CreateEntry(StylesEntryPath, CompressionLevel.Optimal);
        using var stream = newEntry.Open();
        styleContext.StylesDocument.Save(stream);
    }

    private static void SaveWorksheet(ZipArchive zip, BbbTemplateWorksheetContext context)
    {
        var existingEntry = zip.GetEntry(context.EntryPath);
        existingEntry?.Delete();

        var newEntry = zip.CreateEntry(context.EntryPath, CompressionLevel.Optimal);
        using var stream = newEntry.Open();
        context.WorksheetDocument.Save(stream);
    }

    private static List<BbbWorksheetDescriptor> GetWorksheetDescriptors(ZipArchive zip)
    {
        var result = new List<BbbWorksheetDescriptor>();
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        var relationsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry == null || relationsEntry == null)
        {
            var fallback = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (fallback != null)
            {
                result.Add(new BbbWorksheetDescriptor
                {
                    SheetName = "Sheet1",
                    EntryPath = fallback.FullName
                });
            }

            return result;
        }

        XDocument workbookDocument;
        using (var stream = workbookEntry.Open())
            workbookDocument = XDocument.Load(stream);

        XDocument relationsDocument;
        using (var stream = relationsEntry.Open())
            relationsDocument = XDocument.Load(stream);

        var targetsById = relationsDocument.Root?
            .Elements(Pr + "Relationship")
            .ToDictionary(
                x => (string?)x.Attribute("Id") ?? "",
                x =>
                {
                    var target = ((string?)x.Attribute("Target") ?? "").Replace('\\', '/').TrimStart('/');
                    return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
                },
                StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var sheets = workbookDocument.Root?.Element(Sm + "sheets")?.Elements(Sm + "sheet") ?? Enumerable.Empty<XElement>();
        foreach (var sheet in sheets)
        {
            var relationshipId = (string?)sheet.Attribute(Rel + "id");
            if (string.IsNullOrWhiteSpace(relationshipId))
                continue;

            var relationshipKey = relationshipId!;
            if (!targetsById.TryGetValue(relationshipKey, out var entryPath))
                continue;

            if (zip.GetEntry(entryPath) == null)
                continue;

            result.Add(new BbbWorksheetDescriptor
            {
                SheetName = ((string?)sheet.Attribute("name") ?? "").Trim(),
                EntryPath = entryPath
            });
        }

        return result;
    }

    private static void ReplaceCell(XElement rowElement, XElement newCell, int columnNumber)
    {
        var existingCell = FindCell(rowElement, columnNumber);
        if (existingCell != null)
        {
            existingCell.ReplaceWith(newCell);
            return;
        }

        var insertBefore = rowElement.Elements(Sm + "c")
            .FirstOrDefault(x => GetCellColumnNumber(x) > columnNumber);
        if (insertBefore != null)
            insertBefore.AddBeforeSelf(newCell);
        else
            rowElement.Add(newCell);
    }

    private static void ShiftRows(XElement sheetData, int startRowNumber, int offset)
    {
        if (offset == 0)
            return;

        var rows = sheetData.Elements(Sm + "row")
            .Select(x => new { Element = x, RowNumber = ParseRowNumber(x) })
            .Where(x => x.RowNumber >= startRowNumber)
            .OrderByDescending(x => x.RowNumber)
            .ToList();

        foreach (var row in rows)
        {
            var newRowNumber = row.RowNumber + offset;
            row.Element.SetAttributeValue("r", newRowNumber.ToString(CultureInfo.InvariantCulture));

            foreach (var cell in row.Element.Elements(Sm + "c"))
            {
                var columnNumber = GetCellColumnNumber(cell);
                if (columnNumber == int.MaxValue)
                    continue;

                cell.SetAttributeValue("r", BuildCellReference(columnNumber, newRowNumber));

                var formulaElement = cell.Element(Sm + "f");
                if (formulaElement != null)
                    NormalizeFormulaElement(formulaElement, offset);
            }
        }

        SortRows(sheetData);
    }

    private static void DeleteRow(XElement sheetData, int rowNumber)
    {
        var row = FindRow(sheetData, rowNumber);
        row?.Remove();
        ShiftRows(sheetData, rowNumber + 1, -1);
    }

    private static void SortRows(XElement sheetData)
    {
        var orderedRows = sheetData.Elements(Sm + "row")
            .OrderBy(ParseRowNumber)
            .Select(x => new XElement(x))
            .ToList();

        sheetData.ReplaceNodes(orderedRows);
    }

    private static XElement GetOrCreateRow(XElement sheetData, int rowNumber)
    {
        var existingRow = FindRow(sheetData, rowNumber);
        if (existingRow != null)
            return existingRow;

        var newRow = new XElement(Sm + "row", new XAttribute("r", rowNumber.ToString(CultureInfo.InvariantCulture)));
        var insertBefore = sheetData.Elements(Sm + "row")
            .FirstOrDefault(x => ParseRowNumber(x) > rowNumber);

        if (insertBefore != null)
            insertBefore.AddBeforeSelf(newRow);
        else
            sheetData.Add(newRow);

        return newRow;
    }

    private static XElement? FindRow(XElement sheetData, int rowNumber)
    {
        foreach (var rowElement in sheetData.Elements(Sm + "row"))
        {
            if (ParseRowNumber(rowElement) == rowNumber)
                return rowElement;
        }

        return null;
    }

    private static int ParseRowNumber(XElement rowElement)
    {
        var attr = (string?)rowElement.Attribute("r");
        return int.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowNumber)
            ? rowNumber
            : int.MaxValue;
    }

    private static XElement? FindCell(XElement rowElement, int columnNumber)
    {
        foreach (var cell in rowElement.Elements(Sm + "c"))
        {
            if (GetCellColumnNumber(cell) == columnNumber)
                return cell;
        }

        return null;
    }

    private static int GetCellColumnNumber(XElement cell)
    {
        var reference = (string?)cell.Attribute("r");
        return !string.IsNullOrWhiteSpace(reference) && TryParseCellRef(reference!, out var columnNumber, out _)
            ? columnNumber
            : int.MaxValue;
    }

    private static void SetCellText(XElement cell, string value)
    {
        ClearCellValue(cell);
        if (value.Length == 0)
            return;

        cell.SetAttributeValue("t", "inlineStr");
        var textElement = new XElement(Sm + "t", value);
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
            textElement.SetAttributeValue(XNamespace.Xml + "space", "preserve");

        cell.Add(new XElement(Sm + "is", textElement));
    }

    private static void SetCellNumberOrText(XElement cell, string value)
    {
        ClearCellValue(cell);
        if (value.Length == 0)
            return;

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariantNumber) ||
            double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out invariantNumber))
        {
            cell.Attribute("t")?.Remove();
            cell.Add(new XElement(Sm + "v", invariantNumber.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        SetCellText(cell, value);
    }

    private static void SetCellInteger(XElement cell, int value)
    {
        ClearCellValue(cell);
        cell.Attribute("t")?.Remove();
        cell.Add(new XElement(Sm + "v", value.ToString(CultureInfo.InvariantCulture)));
    }

    private static void ClearCellValue(XElement cell)
    {
        cell.Attribute("t")?.Remove();
        cell.Elements(Sm + "v").Remove();
        cell.Elements(Sm + "is").Remove();
        cell.Elements(Sm + "f").Remove();
    }

    private static void UpdateCountAttribute(XElement element)
    {
        element.SetAttributeValue("count", element.Elements().Count().ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildCellReference(int columnNumber, int rowNumber)
    {
        var columnText = "";
        var value = columnNumber;
        while (value > 0)
        {
            value--;
            columnText = (char)('A' + (value % 26)) + columnText;
            value /= 26;
        }

        return columnText + rowNumber.ToString(CultureInfo.InvariantCulture);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;

        return value > max ? max : value;
    }

    private static bool TryParseCellRef(string reference, out int col, out int row)
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
}

internal sealed class BbbWorksheetDescriptor
{
    internal string SheetName { get; set; } = "";
    internal string EntryPath { get; set; } = "";
}

internal sealed class BbbTemplateWorksheetContext
{
    internal string SheetName { get; set; } = "";
    internal string EntryPath { get; set; } = "";
    internal XDocument WorksheetDocument { get; set; } = new();
    internal XElement TemplateRow { get; set; } = new(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"));
    internal Dictionary<int, XElement> TemplateCells { get; set; } = new();
    internal XElement? SequenceTemplateCell { get; set; }
    internal List<int> WritableRows { get; set; } = new();
}

internal sealed class BbbWorkbookStyleContext
{
    internal XDocument StylesDocument { get; set; } = new();
    internal XElement FontsElement { get; set; } = new(XName.Get("fonts", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"));
    internal XElement CellFormatsElement { get; set; } = new(XName.Get("cellXfs", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"));
    internal Dictionary<int, int> SongtiBoldStyleIds { get; } = new();
    internal Dictionary<int, int> SongtiBoldFontIds { get; } = new();
}
