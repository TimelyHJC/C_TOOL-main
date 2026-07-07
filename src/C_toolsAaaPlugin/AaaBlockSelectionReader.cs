using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockSelectionReader
{
    internal static List<AaaBlockExportItem> ReadSelection(
        Database database,
        SelectionSet selectionSet,
        out int selectedCount)
    {
        var blocks = new List<AaaBlockExportItem>();
        selectedCount = 0;

        var countedSelection = 0;
        blocks = CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                var exportItems = new List<AaaBlockExportItem>();
                foreach (SelectedObject? selectedObject in selectionSet)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                        continue;

                    countedSelection++;
                    if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, selectedObject.ObjectId, OpenMode.ForRead, out var blockReference) ||
                        blockReference == null)
                    {
                        continue;
                    }

                    if (!TryCreateExportItem(blockReference, transaction, out var exportItem) || exportItem == null)
                        continue;

                    exportItems.Add(exportItem);
                }

                return exportItems;
            });

        selectedCount = countedSelection;
        return blocks;
    }

    private static bool TryCreateExportItem(
        BlockReference blockReference,
        Transaction transaction,
        out AaaBlockExportItem? exportItem)
    {
        exportItem = null;

        var blockRecordId = blockReference.BlockTableRecord;
        if (blockRecordId.IsInvalid())
            return false;

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, blockRecordId, OpenMode.ForRead, out var blockRecord) ||
            blockRecord == null)
        {
            return false;
        }

        if (blockRecord.IsLayout || blockRecord.IsFromExternalReference || blockRecord.IsFromOverlayReference)
            return false;

        exportItem = new AaaBlockExportItem
        {
            BlockReferenceId = blockReference.ObjectId,
            BlockHandle = blockReference.Handle.ToString(),
            DisplayName = GetDisplayBlockName(blockReference, transaction),
            BasePoint = TryGetBasePoint(blockReference),
            Rotation = TryGetRotation(blockReference),
            ScaleX = TryGetScale(blockReference, scale => scale.X),
            ScaleY = TryGetScale(blockReference, scale => scale.Y),
            ScaleZ = TryGetScale(blockReference, scale => scale.Z),
            LayerName = TryGetLayerName(blockReference)
        };
        return true;
    }

    private static Point3d TryGetBasePoint(BlockReference blockReference)
    {
        try
        {
            return blockReference.Position;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块插入点失败", ex);
            return Point3d.Origin;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块插入点失败（无效操作）", ex);
            return Point3d.Origin;
        }
    }

    private static string GetDisplayBlockName(BlockReference blockReference, Transaction transaction)
    {
        try
        {
            if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull)
            {
                var dynamicName = TryGetBlockName(blockReference.DynamicBlockTableRecord, transaction);
                if (!string.IsNullOrWhiteSpace(dynamicName) && !dynamicName.StartsWith("*", StringComparison.Ordinal))
                    return dynamicName;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块名失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块名失败（无效操作）", ex);
        }

        try
        {
            if (!blockReference.AnonymousBlockTableRecord.IsNull)
            {
                var anonymousName = TryGetBlockName(blockReference.AnonymousBlockTableRecord, transaction);
                if (!string.IsNullOrWhiteSpace(anonymousName) && !anonymousName.StartsWith("*", StringComparison.Ordinal))
                    return anonymousName;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取匿名图块名失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取匿名图块名失败（无效操作）", ex);
        }

        var directName = TryGetBlockName(blockReference.BlockTableRecord, transaction);
        return string.IsNullOrWhiteSpace(directName) ? "<匿名块>" : directName;
    }

    private static double TryGetRotation(BlockReference blockReference)
    {
        try
        {
            return blockReference.Rotation;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块旋转角失败", ex);
            return 0d;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块旋转角失败（无效操作）", ex);
            return 0d;
        }
    }

    private static double TryGetScale(BlockReference blockReference, Func<Scale3d, double> selector)
    {
        try
        {
            var value = selector(blockReference.ScaleFactors);
            return Math.Abs(value) < 1e-9 ? 1d : value;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块比例失败", ex);
            return 1d;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块比例失败（无效操作）", ex);
            return 1d;
        }
    }

    private static string TryGetLayerName(BlockReference blockReference)
    {
        try
        {
            return (blockReference.Layer ?? "").Trim();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块图层失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块图层失败（无效操作）", ex);
        }

        return "";
    }

    private static string TryGetBlockName(ObjectId blockTableRecordId, Transaction transaction)
    {
        if (blockTableRecordId.IsInvalid())
            return "";

        return CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, blockTableRecordId, OpenMode.ForRead, out var blockTableRecord) &&
               blockTableRecord != null
            ? (blockTableRecord.Name ?? "").Trim()
            : "";
    }
}
