using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using CadColor = Autodesk.AutoCAD.Colors.Color;

namespace C_toolsPlugin;

internal sealed class CadObjectPropertiesSnapshot
{
    internal string DocumentName { get; init; } = "—";

    internal string Layer { get; init; } = "—";

    internal string Color { get; init; } = "—";

    internal string Linetype { get; init; } = "—";

    internal string LinetypeScale { get; init; } = "—";

    internal string Lineweight { get; init; } = "—";

    internal string DimStyle { get; init; } = "—";

    internal string TextStyle { get; init; } = "—";

    internal string MLeaderStyle { get; init; } = "—";

    internal static CadObjectPropertiesSnapshot CreateUnavailable(string documentName) => new()
    {
        DocumentName = NormalizeText(documentName)
    };

    private static string NormalizeText(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length == 0 ? "—" : trimmed;
    }
}

internal static class CadObjectPropertiesSnapshotService
{
    internal static CadObjectPropertiesSnapshot CaptureActiveDocument()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return CadObjectPropertiesSnapshot.CreateUnavailable("无活动图纸");

        return CadDatabaseScope.Read(
            doc,
            (db, tr) => Capture(doc, db, tr),
            requireDocumentLock: true);
    }

    internal static CadObjectPropertiesSnapshot Capture(Document doc, Database db, Transaction tr)
    {
        var dimStyle = CurrentDimStyleSync.TryGetCurrentStyleName(db, tr, out var dimStyleName)
            ? FormatCadDisplayText(dimStyleName)
            : "—";

        return new CadObjectPropertiesSnapshot
        {
            DocumentName = FormatDocumentName(doc),
            Layer = ReadCurrentLayerName(db, tr),
            Color = FormatColorValue(CadSystemVariableService.TryGetValue("CECOLOR")),
            Linetype = FormatCadDisplayText(CadSystemVariableService.TryGetValue("CELTYPE")?.ToString()),
            LinetypeScale = FormatNumericValue(CadSystemVariableService.TryGetValue("CELTSCALE")),
            Lineweight = FormatLineWeightValue(CadSystemVariableService.TryGetValue("CELWEIGHT")),
            DimStyle = dimStyle,
            TextStyle = ReadCurrentTextStyleName(db, tr),
            MLeaderStyle = FormatCadDisplayText(MLeaderStyleHelper.TryGetCurrentStyleName(doc))
        };
    }

    private static string ReadCurrentLayerName(Database db, Transaction tr)
    {
        if (db.Clayer.IsNull || !db.Clayer.IsValid || db.Clayer.IsErased)
            return "—";

        return CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, db.Clayer, OpenMode.ForRead, out var record) &&
               record != null
            ? FormatCadDisplayText(record.Name)
            : "—";
    }

    private static string ReadCurrentTextStyleName(Database db, Transaction tr)
    {
        if (db.Textstyle.IsNull || !db.Textstyle.IsValid || db.Textstyle.IsErased)
            return "—";

        return CadDatabaseScope.TryOpenAs<TextStyleTableRecord>(tr, db.Textstyle, OpenMode.ForRead, out var record) &&
               record != null
            ? FormatCadDisplayText(record.Name)
            : "—";
    }

    private static string FormatDocumentName(Document doc)
    {
        try
        {
            var fileName = Path.GetFileName(doc.Name ?? "");
            return string.IsNullOrWhiteSpace(fileName) ? "未命名图纸" : fileName;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("对象特性：格式化当前图纸名称失败（参数错误）", ex);
            return "当前活动图纸";
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("对象特性：格式化当前图纸名称失败（路径过长）", ex);
            return "当前活动图纸";
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("对象特性：格式化当前图纸名称失败（不支持）", ex);
            return "当前活动图纸";
        }
    }

    private static string FormatCadDisplayText(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length == 0 ? "—" : trimmed;
    }

    private static string FormatColorValue(object? value)
    {
        return value switch
        {
            null => "—",
            CadColor color => FormatCadDisplayText(MLeaderToolCadColor.ToUiString(color)),
            _ => FormatCadDisplayText(value.ToString())
        };
    }

    private static string FormatNumericValue(object? value)
    {
        switch (value)
        {
            case null:
                return "—";
            case double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue):
                return doubleValue.ToString("0.####", CultureInfo.InvariantCulture);
            case float floatValue when !float.IsNaN(floatValue) && !float.IsInfinity(floatValue):
                return floatValue.ToString("0.####", CultureInfo.InvariantCulture);
            case decimal decimalValue:
                return decimalValue.ToString("0.####", CultureInfo.InvariantCulture);
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—";
            default:
                return FormatCadDisplayText(value.ToString());
        }
    }

    private static string FormatLineWeightValue(object? value)
    {
        if (value == null)
            return "—";

        if (TryConvertToLineWeight(value, out var lineWeight))
            return FormatLineWeight(lineWeight);

        return FormatCadDisplayText(value.ToString());
    }

    private static bool TryConvertToLineWeight(object value, out LineWeight lineWeight)
    {
        switch (value)
        {
            case LineWeight direct:
                lineWeight = direct;
                return true;
            case short shortValue:
                lineWeight = (LineWeight)shortValue;
                return true;
            case int intValue:
                lineWeight = (LineWeight)intValue;
                return true;
            default:
                if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    lineWeight = (LineWeight)parsed;
                    return true;
                }

                lineWeight = default;
                return false;
        }
    }

    private static string FormatLineWeight(LineWeight lineWeight)
    {
        if (lineWeight == LineWeight.ByLayer)
            return "BYLAYER";
        if (lineWeight == LineWeight.ByBlock)
            return "BYBLOCK";
        if (lineWeight == LineWeight.ByLineWeightDefault)
            return "默认";

        var enumName = lineWeight.ToString();
        const string prefix = "LineWeight";
        if (!enumName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return enumName;

        var numericText = enumName.Substring(prefix.Length);
        if (!int.TryParse(numericText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hundredths))
            return enumName;

        return (hundredths / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " mm";
    }
}
