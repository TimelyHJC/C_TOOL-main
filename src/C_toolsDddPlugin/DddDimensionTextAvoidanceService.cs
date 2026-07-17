using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using C_toolsPlugin;
using C_toolsShared;

namespace C_toolsDddPlugin;

internal static class DddDimensionTextAvoidanceService
{
    internal static bool TryApplyAlignedChain(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        double signedOffset,
        out string error)
    {
        return DddDimensionChainTextAvoidanceService.TryApplyAlignedChain(
            doc,
            points,
            dimensionIds,
            signedOffset,
            out error);
    }

    internal static bool TryApplyLinearChain(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        bool isHorizontal,
        double dimLineOrdinate,
        out string error)
    {
        return DddDimensionChainTextAvoidanceService.TryApplyLinearChain(
            doc,
            points,
            dimensionIds,
            isHorizontal,
            dimLineOrdinate,
            out error);
    }

    internal static bool TryApplyDimensionIds(
        Document doc,
        IList<ObjectId> dimensionIds,
        string commandTag,
        out int rowCount,
        out int adjustedCount,
        out string error)
    {
        return DddDimensionChainTextAvoidanceService.TryApplyDimensionIds(
            doc,
            dimensionIds,
            commandTag,
            out rowCount,
            out adjustedCount,
            out error);
    }

    internal static bool TryApplyDimensionIds(
        Document doc,
        IList<ObjectId> dimensionIds,
        string commandTag,
        bool preferCurrentPlacement,
        out int rowCount,
        out int adjustedCount,
        out string error)
    {
        return DddDimensionChainTextAvoidanceService.TryApplyDimensionIds(
            doc,
            dimensionIds,
            commandTag,
            preferCurrentPlacement,
            out rowCount,
            out adjustedCount,
            out error);
    }

    internal static void Run(Document doc)
    {
        var ed = doc.Editor;
        if (!TryGetSeedDimensionId(doc, out var seedId))
            return;

        if (!TryApplyToSeed(doc, seedId, out var rowCount, out var adjustedCount, out var error))
        {
            ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        if (rowCount == 0)
        {
            ed.WriteMessage("\nC_TOOL：无同排标注可处理。");
            return;
        }

        if (adjustedCount > 0)
        {
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}F_DF 整理 {rowCount} 个标注，{adjustedCount} 个文字调整。");
        }
        else
        {
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}F_DF 检查 {rowCount} 个标注，无需调整。");
        }
    }

    internal static bool TryApplyToSeed(
        Document doc,
        ObjectId seedId,
        out int rowCount,
        out int movedCount,
        out string error)
    {
        rowCount = 0;
        movedCount = 0;
        error = string.Empty;

        try
        {
            return DddDimensionChainTextAvoidanceService.TryApplySameRow(
                doc,
                seedId,
                DddPluginCommandIds.DimTextAvoid,
                out rowCount,
                out movedCount,
                out error);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DF 自动整理标注文字失败（无效操作）", ex);
            error = $"F_DF 执行失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DF 自动整理标注文字失败（CAD）", ex);
            error = $"F_DF 执行失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DF 自动整理标注文字失败（参数）", ex);
            error = $"F_DF 执行失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryGetSeedDimensionId(Document doc, out ObjectId dimensionId)
    {
        var ed = doc.Editor;
        dimensionId = ObjectId.Null;

        if (TryGetImpliedDimensionId(ed, out dimensionId))
            return true;

        var options = new PromptEntityOptions("\nC_TOOL：选择同排线性/对齐标注：")
        {
            AllowNone = true
        };
        options.SetRejectMessage("\nC_TOOL：仅支持线性或对齐标注。");
        DddDimensionSelectionFilter.AddLinearAlignedAllowedClasses(options);

        var result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
        {
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_DF_Cancelled}");
            return false;
        }

        dimensionId = result.ObjectId;
        return true;
    }

    private static bool TryGetImpliedDimensionId(Editor ed, out ObjectId dimensionId)
    {
        dimensionId = ObjectId.Null;

        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = implied.Value.GetObjectIds();
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        if (ids.Length != 1)
            return false;

        if (!DddDimensionSelectionFilter.IsLinearOrAlignedDimension(ids[0]))
            return false;

        dimensionId = ids[0];
        return true;
    }
}
