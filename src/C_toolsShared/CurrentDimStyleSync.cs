using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

public static class CurrentDimStyleSync
{
    private const string DimStyleSystemVariableName = "DIMSTYLE";

    public static bool TrySyncFromDatabase(Document? doc, string operationTag, out string styleName)
    {
        styleName = "";
        if (doc == null)
            return false;

        try
        {
            var syncState = CadDatabaseScope.Read(
                doc,
                (db, tr) =>
                {
                    if (!TryGetCurrentStyleRecord(db, tr, out var styleRecord, out var resolvedStyleName))
                        return (Success: false, StyleName: "");

                    db.SetDimstyleData(styleRecord);
                    return (Success: true, StyleName: resolvedStyleName);
                });

            if (!syncState.Success)
                return false;

            styleName = syncState.StyleName;
            return TrySyncToSystemVariable(styleName, operationTag);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationTag} 同步当前标注样式失败（无效操作）", ex);
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationTag} 同步当前标注样式失败（CAD）", ex);
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationTag} 同步当前标注样式失败（参数）", ex);
            return false;
        }
    }

    public static bool TrySyncToSystemVariable(string? styleName, string operationTag)
    {
        var trimmedName = styleName?.Trim() ?? "";
        if (trimmedName.Length == 0)
            return false;

        if (CadSystemVariableService.TrySetValue(DimStyleSystemVariableName, trimmedName))
            return true;

        C_toolsDiagnostics.LogNonFatal($"{operationTag} 回写当前标注样式失败", null);
        return false;
    }

    public static bool TryGetCurrentStyleName(Database db, Transaction tr, out string styleName)
    {
        styleName = "";
        if (!TryGetCurrentStyleRecord(db, tr, out _, out var resolvedStyleName))
            return false;

        styleName = resolvedStyleName;
        return true;
    }

    private static bool TryGetCurrentStyleRecord(
        Database db,
        Transaction tr,
        out DimStyleTableRecord styleRecord,
        out string styleName)
    {
        styleRecord = null!;
        styleName = "";
        if (db == null || tr == null)
            return false;

        var styleId = db.Dimstyle;
        if (styleId.IsNull || !styleId.IsValid || styleId.IsErased)
            return false;

        if (!CadDatabaseScope.TryOpenAs<DimStyleTableRecord>(tr, styleId, OpenMode.ForRead, out var record) ||
            record == null)
        {
            return false;
        }

        var trimmedName = record.Name?.Trim() ?? "";
        if (trimmedName.Length == 0)
            return false;

        styleRecord = record;
        styleName = trimmedName;
        return true;
    }
}
