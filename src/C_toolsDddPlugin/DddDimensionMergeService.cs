using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddDimensionMergeService
{
    private const double PointTolerance = 1e-6;
    private const double RotationTolerance = 1e-6;
    private const double ChainRowTolerance = 1e-4;
    private const double ParallelDotTolerance = 0.999;

    internal static void Run(Document doc)
    {
        var ed = doc.Editor;
        if (!TryResolveMergePlan(doc, out var plan, out var error, out var canceled))
        {
            if (!canceled && !string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        if (!TryMergeDimensions(doc, plan, out var mergedMeasurement, out error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        var mergedCount = plan.RangeEndIndex - plan.RangeStartIndex + 1;
        ed.WriteMessage(
            $"\nC_TOOL：F_DV 合并 {mergedCount} 个标注为 1 个总尺寸（{FormatMeasurement(mergedMeasurement)}）。");
    }

    private static bool TryResolveMergePlan(
        Document doc,
        out MergePlan plan,
        out string error,
        out bool canceled)
    {
        plan = default;
        error = string.Empty;
        canceled = false;

        var ed = doc.Editor;
        if (!TryGetRequestedDimensionIds(ed, out var selectedDimensionIds, out error, out canceled))
            return false;

        if (!TryReadMergeContext(doc, selectedDimensionIds[0], out var context, out error))
            return false;

        if (context.DimensionIds.Count < 2)
        {
            error = "所选标注所在同排只有 1 条标注，无法合并。";
            return false;
        }

        if (selectedDimensionIds.Count == 1)
        {
            plan = new MergePlan(context, 0, context.DimensionIds.Count - 1);
            return true;
        }

        return TryResolveSelectedRange(context, selectedDimensionIds, out plan, out error);
    }

    private static bool TryResolveSelectedRange(
        MergeContext context,
        IReadOnlyList<ObjectId> selectedDimensionIds,
        out MergePlan plan,
        out string error)
    {
        plan = default;
        error = string.Empty;

        var selectedIndexes = new List<int>(selectedDimensionIds.Count);
        var seen = new HashSet<ObjectId>();
        foreach (var selectedId in selectedDimensionIds)
        {
            if (!seen.Add(selectedId))
                continue;

            var index = IndexOfObjectId(context.DimensionIds, selectedId);
            if (index < 0)
            {
                error = "所选标注不在同一排连续标注链中，请重新选择。";
                return false;
            }

            selectedIndexes.Add(index);
        }

        if (selectedIndexes.Count < 2)
        {
            error = "请至少选择两条同排连续标注，或只选一条标注合并整排。";
            return false;
        }

        selectedIndexes.Sort();
        for (var i = 1; i < selectedIndexes.Count; i++)
        {
            if (selectedIndexes[i] == selectedIndexes[i - 1] + 1)
                continue;

            error = "请只选择同排连续标注；F_DV 只合并实际选中的连续尺寸。";
            return false;
        }

        plan = new MergePlan(context, selectedIndexes[0], selectedIndexes[^1]);
        return true;
    }

    private static bool TryMergeDimensions(
        Document doc,
        MergePlan plan,
        out double mergedMeasurement,
        out string error)
    {
        return plan.Context.Kind == DimensionChainKind.Linear
            ? TryMergeLinearDimensions(doc, plan, out mergedMeasurement, out error)
            : TryMergeAlignedDimensions(doc, plan, out mergedMeasurement, out error);
    }

    private static bool TryMergeLinearDimensions(
        Document doc,
        MergePlan plan,
        out double mergedMeasurement,
        out string error)
    {
        mergedMeasurement = 0.0;
        error = string.Empty;

        var context = plan.Context;
        var mergedFirstPoint = context.Points[plan.RangeStartIndex];
        var mergedSecondPoint = context.Points[plan.RangeEndIndex + 1];
        if (!TryBuildLinearDimensionLinePoint(
                mergedFirstPoint,
                mergedSecondPoint,
                context.Orientation,
                context.DimLineOrdinate,
                out var dimLinePoint,
                out error))
        {
            return false;
        }

        ObjectId createdDimensionId = ObjectId.Null;
        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var dbObject = tr.GetObject(context.SeedDimensionId, OpenMode.ForRead, false);
                if (dbObject is not RotatedDimension sourceDimension || sourceDimension.IsErased)
                {
                    error = "起始线性标注已失效，请重新选择。";
                    return false;
                }

                var mergedDimension = sourceDimension.Clone() as RotatedDimension;
                if (mergedDimension == null)
                {
                    error = "无法创建合并后的线性标注。";
                    return false;
                }

                using (mergedDimension)
                {
                    mergedDimension.XLine1Point = mergedFirstPoint;
                    mergedDimension.XLine2Point = mergedSecondPoint;
                    mergedDimension.DimLinePoint = dimLinePoint;
                    PrepareCreatedDimension(mergedDimension);
                    mergedDimension.RecomputeDimensionBlock(true);
                    currentSpace.AppendEntity(mergedDimension);
                    tr.AddNewlyCreatedDBObject(mergedDimension, true);
                    createdDimensionId = mergedDimension.ObjectId;
                }

                EraseDimensionRange(tr, context.DimensionIds, plan.RangeStartIndex, plan.RangeEndIndex);
                tr.Commit();
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 合并线性标注失败（无效操作）", ex);
            error = $"合并线性标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 合并线性标注失败（CAD）", ex);
            error = $"合并线性标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 合并线性标注失败（参数）", ex);
            error = $"合并线性标注失败：{ex.Message}";
            return false;
        }

        var updatedPoints = BuildMergedPointList(context.Points, plan.RangeStartIndex, plan.RangeEndIndex);
        var updatedDimensionIds = BuildMergedDimensionIdList(context.DimensionIds, createdDimensionId, plan.RangeStartIndex, plan.RangeEndIndex);
        if (!DddDimensionTextAvoidanceService.TryApplyLinearChain(
                doc,
                updatedPoints,
                updatedDimensionIds,
                context.Orientation == LinearDimensionOrientation.Horizontal,
                context.DimLineOrdinate,
                out error))
        {
            WriteAvoidanceFailure(doc, error);
            error = string.Empty;
        }

        mergedMeasurement = GetLinearMeasurement(context.Orientation, mergedFirstPoint, mergedSecondPoint);
        return true;
    }

    private static bool TryMergeAlignedDimensions(
        Document doc,
        MergePlan plan,
        out double mergedMeasurement,
        out string error)
    {
        mergedMeasurement = 0.0;
        error = string.Empty;

        var context = plan.Context;
        var mergedFirstPoint = context.Points[plan.RangeStartIndex];
        var mergedSecondPoint = context.Points[plan.RangeEndIndex + 1];
        if (!TryBuildAlignedDimensionLinePoint(
                mergedFirstPoint,
                mergedSecondPoint,
                context.SignedOffset,
                out var dimLinePoint,
                out error))
        {
            return false;
        }

        ObjectId createdDimensionId = ObjectId.Null;
        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var dbObject = tr.GetObject(context.SeedDimensionId, OpenMode.ForRead, false);
                if (dbObject is not AlignedDimension sourceDimension || sourceDimension.IsErased)
                {
                    error = "起始对齐标注已失效，请重新选择。";
                    return false;
                }

                var mergedDimension = sourceDimension.Clone() as AlignedDimension;
                if (mergedDimension == null)
                {
                    error = "无法创建合并后的对齐标注。";
                    return false;
                }

                using (mergedDimension)
                {
                    mergedDimension.XLine1Point = mergedFirstPoint;
                    mergedDimension.XLine2Point = mergedSecondPoint;
                    mergedDimension.DimLinePoint = dimLinePoint;
                    PrepareCreatedDimension(mergedDimension);
                    mergedDimension.RecomputeDimensionBlock(true);
                    currentSpace.AppendEntity(mergedDimension);
                    tr.AddNewlyCreatedDBObject(mergedDimension, true);
                    createdDimensionId = mergedDimension.ObjectId;
                }

                EraseDimensionRange(tr, context.DimensionIds, plan.RangeStartIndex, plan.RangeEndIndex);
                tr.Commit();
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 合并对齐标注失败（无效操作）", ex);
            error = $"合并对齐标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 合并对齐标注失败（CAD）", ex);
            error = $"合并对齐标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 合并对齐标注失败（参数）", ex);
            error = $"合并对齐标注失败：{ex.Message}";
            return false;
        }

        var updatedPoints = BuildMergedPointList(context.Points, plan.RangeStartIndex, plan.RangeEndIndex);
        var updatedDimensionIds = BuildMergedDimensionIdList(context.DimensionIds, createdDimensionId, plan.RangeStartIndex, plan.RangeEndIndex);
        if (!DddDimensionTextAvoidanceService.TryApplyAlignedChain(
                doc,
                updatedPoints,
                updatedDimensionIds,
                context.SignedOffset,
                out error))
        {
            WriteAvoidanceFailure(doc, error);
            error = string.Empty;
        }

        mergedMeasurement = GetAlignedMeasurement(mergedFirstPoint, mergedSecondPoint);
        return true;
    }

    private static bool TryReadMergeContext(
        Document doc,
        ObjectId dimensionId,
        out MergeContext context,
        out string error)
    {
        context = default!;
        error = string.Empty;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var dbObject = tr.GetObject(dimensionId, OpenMode.ForRead, false);
            switch (dbObject)
            {
                case RotatedDimension linearDimension:
                    if (!TryBuildLinearContext(tr, dimensionId, linearDimension, out context, out error))
                        return false;
                    break;
                case AlignedDimension alignedDimension:
                    if (!TryBuildAlignedContext(tr, dimensionId, alignedDimension, out context, out error))
                        return false;
                    break;
                default:
                    error = "F_DV 仅支持线性标注与对齐标注。";
                    return false;
            }

            tr.Commit();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 读取同排标注链失败（无效操作）", ex);
            error = $"读取同排标注链失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 读取同排标注链失败（CAD）", ex);
            error = $"读取同排标注链失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DV 读取同排标注链失败（参数）", ex);
            error = $"读取同排标注链失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryBuildLinearContext(
        Transaction tr,
        ObjectId dimensionId,
        RotatedDimension selectedDimension,
        out MergeContext context,
        out string error)
    {
        context = default!;
        error = string.Empty;

        if (!TryGetLinearDimensionSettings(selectedDimension, out var orientation, out var dimLineOrdinate, out error))
            return false;

        var selectedFirstPoint = selectedDimension.XLine1Point;
        var selectedSecondPoint = selectedDimension.XLine2Point;
        NormalizeLinearContinuationDirection(orientation, ref selectedFirstPoint, ref selectedSecondPoint);

        var points = new List<Point3d>();
        var dimensionIds = new List<ObjectId>();
        var segments = new List<DddDimensionChainSegment>
        {
            new(selectedFirstPoint, selectedSecondPoint, dimensionId)
        };
        var ownerRecord = (BlockTableRecord)tr.GetObject(selectedDimension.OwnerId, OpenMode.ForRead);

        var added = true;
        while (added)
        {
            added = false;
            foreach (ObjectId entityId in ownerRecord)
            {
                if (!entityId.IsValid || entityId == dimensionId || entityId.IsErased)
                    continue;

                var dbObject = tr.GetObject(entityId, OpenMode.ForRead, false);
                if (dbObject is not RotatedDimension candidate)
                    continue;

                if (!TryGetLinearDimensionSettings(candidate, out var candidateOrientation, out var candidateOrdinate, out _))
                    continue;

                if (candidateOrientation != orientation || Math.Abs(candidateOrdinate - dimLineOrdinate) > PointTolerance * 10.0)
                    continue;

                var candidateFirstPoint = candidate.XLine1Point;
                var candidateSecondPoint = candidate.XLine2Point;
                NormalizeLinearContinuationDirection(candidateOrientation, ref candidateFirstPoint, ref candidateSecondPoint);

                if (!DddDimensionChainHelper.SharesAnyEndpoint(candidateFirstPoint, candidateSecondPoint, segments, PointTolerance))
                    continue;

                if (DddDimensionChainHelper.ContainsSegment(segments, candidateFirstPoint, candidateSecondPoint, PointTolerance))
                    continue;

                segments.Add(new DddDimensionChainSegment(candidateFirstPoint, candidateSecondPoint, entityId));
                added = true;
            }
        }

        AddUniquePoint(points, selectedFirstPoint);
        AddUniquePoint(points, selectedSecondPoint);
        foreach (var segment in segments)
        {
            AddUniquePoint(points, segment.First);
            AddUniquePoint(points, segment.Second);
        }

        points.Sort((left, right) => GetLinearCoordinate(orientation, left).CompareTo(GetLinearCoordinate(orientation, right)));
        if (points.Count < 2)
        {
            error = "未能解析所选线性标注所在的连续标注链。";
            return false;
        }

        DddDimensionChainHelper.AddDimensionIdsInOrder(
            segments,
            dimensionIds,
            (left, right) => GetLinearCoordinate(orientation, left.First).CompareTo(GetLinearCoordinate(orientation, right.First)));

        var selectedIndex = IndexOfObjectId(dimensionIds, dimensionId);
        if (selectedIndex < 0)
        {
            error = "未能定位所选线性标注在当前标注链中的位置。";
            return false;
        }

        context = new MergeContext(
            DimensionChainKind.Linear,
            dimensionId,
            points,
            dimensionIds,
            selectedIndex,
            orientation,
            dimLineOrdinate,
            0.0);
        return true;
    }

    private static bool TryBuildAlignedContext(
        Transaction tr,
        ObjectId dimensionId,
        AlignedDimension selectedDimension,
        out MergeContext context,
        out string error)
    {
        context = default!;
        error = string.Empty;

        var selectedFirstPoint = selectedDimension.XLine1Point;
        var selectedSecondPoint = selectedDimension.XLine2Point;
        NormalizeAlignedContinuationDirection(ref selectedFirstPoint, ref selectedSecondPoint);
        if (!TryComputeSignedOffset(selectedFirstPoint, selectedSecondPoint, selectedDimension.DimLinePoint, out var signedOffset))
        {
            error = "无法读取所选对齐标注的基线偏移。";
            return false;
        }

        if (!TryNormalizePlanar(selectedSecondPoint - selectedFirstPoint, out var selectedAxis))
        {
            error = "所选对齐标注方向无效，无法合并。";
            return false;
        }

        var referenceNormal = new Vector3d(-selectedAxis.Y, selectedAxis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "所选对齐标注法向无效，无法合并。";
            return false;
        }

        var selectedAnchor = BuildAlignedLineAnchor(selectedFirstPoint, selectedSecondPoint, signedOffset);
        var points = new List<Point3d>();
        var dimensionIds = new List<ObjectId>();
        var segments = new List<DddDimensionChainSegment>
        {
            new(selectedFirstPoint, selectedSecondPoint, dimensionId)
        };
        var ownerRecord = (BlockTableRecord)tr.GetObject(selectedDimension.OwnerId, OpenMode.ForRead);

        var added = true;
        while (added)
        {
            added = false;
            foreach (ObjectId entityId in ownerRecord)
            {
                if (!entityId.IsValid || entityId == dimensionId || entityId.IsErased)
                    continue;

                var dbObject = tr.GetObject(entityId, OpenMode.ForRead, false);
                if (dbObject is not AlignedDimension candidate)
                    continue;

                var candidateFirstPoint = candidate.XLine1Point;
                var candidateSecondPoint = candidate.XLine2Point;
                NormalizeAlignedContinuationDirection(ref candidateFirstPoint, ref candidateSecondPoint);

                if (!TryNormalizePlanar(candidateSecondPoint - candidateFirstPoint, out var candidateAxis) ||
                    !AreParallel(selectedAxis, candidateAxis))
                {
                    continue;
                }

                if (!TryComputeSignedOffset(candidateFirstPoint, candidateSecondPoint, candidate.DimLinePoint, out var candidateOffset))
                    continue;

                var candidateAnchor = BuildAlignedLineAnchor(candidateFirstPoint, candidateSecondPoint, candidateOffset);
                var rowDistance = Math.Abs((candidateAnchor - selectedAnchor).DotProduct(referenceNormal));
                if (rowDistance > ChainRowTolerance)
                    continue;

                if (!DddDimensionChainHelper.SharesAnyEndpoint(candidateFirstPoint, candidateSecondPoint, segments, PointTolerance))
                    continue;

                if (DddDimensionChainHelper.ContainsSegment(segments, candidateFirstPoint, candidateSecondPoint, PointTolerance))
                    continue;

                segments.Add(new DddDimensionChainSegment(candidateFirstPoint, candidateSecondPoint, entityId));
                added = true;
            }
        }

        AddUniquePoint(points, selectedFirstPoint);
        AddUniquePoint(points, selectedSecondPoint);
        foreach (var segment in segments)
        {
            AddUniquePoint(points, segment.First);
            AddUniquePoint(points, segment.Second);
        }

        points.Sort((left, right) => AxisParameter(left, selectedFirstPoint, selectedAxis).CompareTo(AxisParameter(right, selectedFirstPoint, selectedAxis)));
        if (points.Count < 2)
        {
            error = "未能解析所选对齐标注所在的连续标注链。";
            return false;
        }

        DddDimensionChainHelper.AddDimensionIdsInOrder(
            segments,
            dimensionIds,
            (left, right) => AxisParameter(left.First, selectedFirstPoint, selectedAxis).CompareTo(AxisParameter(right.First, selectedFirstPoint, selectedAxis)));

        var selectedIndex = IndexOfObjectId(dimensionIds, dimensionId);
        if (selectedIndex < 0)
        {
            error = "未能定位所选对齐标注在当前标注链中的位置。";
            return false;
        }

        context = new MergeContext(
            DimensionChainKind.Aligned,
            dimensionId,
            points,
            dimensionIds,
            selectedIndex,
            default,
            0.0,
            signedOffset);
        return true;
    }

    private static bool TryGetRequestedDimensionIds(
        Editor ed,
        out List<ObjectId> dimensionIds,
        out string error,
        out bool canceled)
    {
        error = string.Empty;
        canceled = false;

        if (TryGetImpliedDimensionIds(ed, out dimensionIds))
            return true;

        var options = new PromptSelectionOptions
        {
            AllowDuplicates = false,
            MessageForAdding = "\nC_TOOL：选择要合并的尺寸（选 1 个合并整排，选多个只合并所选连续尺寸）："
        };
        var result = ed.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null)
        {
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_DV_Cancelled}");
            dimensionIds = new List<ObjectId>();
            canceled = true;
            return false;
        }

        return TryCollectSupportedDimensionIds(result.Value.GetObjectIds(), out dimensionIds, out error);
    }

    private static bool TryGetImpliedDimensionIds(Editor ed, out List<ObjectId> dimensionIds)
    {
        dimensionIds = new List<ObjectId>();

        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = implied.Value.GetObjectIds();
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        return TryCollectSupportedDimensionIds(ids, out dimensionIds, out _);
    }

    private static bool TryCollectSupportedDimensionIds(
        IReadOnlyList<ObjectId> ids,
        out List<ObjectId> dimensionIds,
        out string error)
    {
        dimensionIds = new List<ObjectId>();
        error = string.Empty;
        if (ids.Count == 0)
            return false;

        var seen = new HashSet<ObjectId>();
        var rotatedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(RotatedDimension));
        var alignedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(AlignedDimension));
        foreach (var id in ids)
        {
            if (!seen.Add(id))
                continue;

            var objectClass = id.ObjectClass;
            if (objectClass == null ||
                (!objectClass.IsDerivedFrom(rotatedClass) && !objectClass.IsDerivedFrom(alignedClass)))
            {
                error = "F_DV 仅支持线性标注与对齐标注。";
                return false;
            }

            dimensionIds.Add(id);
        }

        return dimensionIds.Count > 0;
    }

    private static void EraseDimensionRange(Transaction tr, IList<ObjectId> dimensionIds, int rangeStartIndex, int rangeEndIndex)
    {
        for (var index = rangeStartIndex; index <= rangeEndIndex; index++)
        {
            var dimensionId = dimensionIds[index];
            if (!dimensionId.IsValid || dimensionId.IsErased)
                continue;

            var dbObject = tr.GetObject(dimensionId, OpenMode.ForWrite, false);
            if (dbObject is Entity entity && !entity.IsErased)
                entity.Erase();
        }
    }

    private static List<Point3d> BuildMergedPointList(IList<Point3d> points, int rangeStartIndex, int rangeEndIndex)
    {
        var mergedPoints = new List<Point3d>(Math.Max(2, points.Count - (rangeEndIndex - rangeStartIndex)));
        for (var index = 0; index < points.Count; index++)
        {
            if (index > rangeStartIndex && index <= rangeEndIndex)
                continue;

            mergedPoints.Add(points[index]);
        }

        return mergedPoints;
    }

    private static List<ObjectId> BuildMergedDimensionIdList(
        IList<ObjectId> dimensionIds,
        ObjectId mergedDimensionId,
        int rangeStartIndex,
        int rangeEndIndex)
    {
        var mergedIds = new List<ObjectId>(Math.Max(1, dimensionIds.Count - (rangeEndIndex - rangeStartIndex)));
        for (var index = 0; index < dimensionIds.Count; index++)
        {
            if (index == rangeStartIndex)
            {
                mergedIds.Add(mergedDimensionId);
                index = rangeEndIndex;
                continue;
            }

            if (index < rangeStartIndex || index > rangeEndIndex)
                mergedIds.Add(dimensionIds[index]);
        }

        return mergedIds;
    }

    private static void PrepareCreatedDimension(Dimension dimension)
    {
        dimension.DimensionText = string.Empty;
        dimension.UsingDefaultTextPosition = true;
        dimension.Dimatfit = 3;
        dimension.Dimtix = true;
        dimension.Dimtofl = true;
        dimension.Dimtmove = 2;
    }

    private static bool TryGetLinearDimensionSettings(
        RotatedDimension dimension,
        out LinearDimensionOrientation orientation,
        out double dimLineOrdinate,
        out string error)
    {
        error = string.Empty;
        var normalizedRotation = NormalizeRotation(dimension.Rotation);

        if (Math.Abs(Math.Sin(normalizedRotation)) <= RotationTolerance)
        {
            orientation = LinearDimensionOrientation.Horizontal;
            dimLineOrdinate = dimension.DimLinePoint.Y;
            return true;
        }

        if (Math.Abs(Math.Cos(normalizedRotation)) <= RotationTolerance)
        {
            orientation = LinearDimensionOrientation.Vertical;
            dimLineOrdinate = dimension.DimLinePoint.X;
            return true;
        }

        orientation = default;
        dimLineOrdinate = 0.0;
        error = "所选线性标注方向不是水平/垂直，无法合并。";
        return false;
    }

    private static void NormalizeLinearContinuationDirection(
        LinearDimensionOrientation orientation,
        ref Point3d firstPoint,
        ref Point3d secondPoint)
    {
        var shouldSwap = orientation == LinearDimensionOrientation.Horizontal
            ? secondPoint.X < firstPoint.X
            : secondPoint.Y < firstPoint.Y;

        if (!shouldSwap)
            return;

        (firstPoint, secondPoint) = (secondPoint, firstPoint);
    }

    private static bool TryBuildLinearDimensionLinePoint(
        Point3d firstPoint,
        Point3d secondPoint,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        out Point3d dimLinePoint,
        out string error)
    {
        dimLinePoint = default;
        error = string.Empty;

        var midPoint = MidPoint(firstPoint, secondPoint);
        if (orientation == LinearDimensionOrientation.Horizontal)
        {
            if (Math.Abs(secondPoint.X - firstPoint.X) < PointTolerance)
            {
                error = "当前两点在水平方向上过近，无法生成总尺寸。";
                return false;
            }

            dimLinePoint = new Point3d(midPoint.X, dimLineOrdinate, midPoint.Z);
            return true;
        }

        if (Math.Abs(secondPoint.Y - firstPoint.Y) < PointTolerance)
        {
            error = "当前两点在垂直方向上过近，无法生成总尺寸。";
            return false;
        }

        dimLinePoint = new Point3d(dimLineOrdinate, midPoint.Y, midPoint.Z);
        return true;
    }

    private static double GetLinearCoordinate(LinearDimensionOrientation orientation, Point3d point) =>
        orientation == LinearDimensionOrientation.Horizontal ? point.X : point.Y;

    private static double GetLinearMeasurement(LinearDimensionOrientation orientation, Point3d firstPoint, Point3d secondPoint) =>
        Math.Abs(GetLinearCoordinate(orientation, secondPoint) - GetLinearCoordinate(orientation, firstPoint));

    private static void NormalizeAlignedContinuationDirection(ref Point3d firstPoint, ref Point3d secondPoint)
    {
        var delta = secondPoint - firstPoint;
        var absX = Math.Abs(delta.X);
        var absY = Math.Abs(delta.Y);

        var shouldSwap = absX >= absY
            ? delta.X < -PointTolerance || (Math.Abs(delta.X) <= PointTolerance && delta.Y < -PointTolerance)
            : delta.Y < -PointTolerance;

        if (!shouldSwap)
            return;

        (firstPoint, secondPoint) = (secondPoint, firstPoint);
    }

    private static bool TryBuildAlignedDimensionLinePoint(
        Point3d firstPoint,
        Point3d secondPoint,
        double signedOffset,
        out Point3d dimLinePoint,
        out string error)
    {
        dimLinePoint = default;
        error = string.Empty;

        if (!TryGetPerpendicularUnit(firstPoint, secondPoint, out var perpendicular))
        {
            error = "两测点过近，无法生成总尺寸。";
            return false;
        }

        if (Math.Abs(signedOffset) < PointTolerance)
        {
            error = "连续标注基线偏移量无效，请重新执行 F_DA 或 F_DV。";
            return false;
        }

        dimLinePoint = MidPoint(firstPoint, secondPoint) + (perpendicular * signedOffset);
        return true;
    }

    private static Point3d BuildAlignedLineAnchor(Point3d firstPoint, Point3d secondPoint, double signedOffset)
    {
        TryGetPerpendicularUnit(firstPoint, secondPoint, out var perpendicular);
        return MidPoint(firstPoint, secondPoint) + (perpendicular * signedOffset);
    }

    private static bool TryComputeSignedOffset(
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d dimLinePoint,
        out double signedOffset)
    {
        signedOffset = 0.0;
        if (!TryGetPerpendicularUnit(firstPoint, secondPoint, out var perpendicular))
            return false;

        var midPoint = MidPoint(firstPoint, secondPoint);
        var delta = dimLinePoint - midPoint;
        signedOffset = Dot2d(delta, perpendicular);
        return Math.Abs(signedOffset) >= PointTolerance;
    }

    private static bool TryGetPerpendicularUnit(Point3d firstPoint, Point3d secondPoint, out Vector3d perpendicular)
    {
        perpendicular = default;
        var direction = secondPoint - firstPoint;
        var length2d = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        if (length2d < PointTolerance)
            return false;

        perpendicular = new Vector3d(-direction.Y / length2d, direction.X / length2d, 0.0);
        return true;
    }

    private static bool TryNormalizePlanar(Vector3d vector, out Vector3d normalized)
    {
        normalized = new Vector3d(vector.X, vector.Y, 0.0);
        if (normalized.Length <= PointTolerance)
            return false;

        normalized = normalized.GetNormal();
        return true;
    }

    private static bool AreParallel(Vector3d left, Vector3d right) =>
        Math.Abs(left.DotProduct(right)) >= ParallelDotTolerance;

    private static double AxisParameter(Point3d point, Point3d axisOrigin, Vector3d axisDirection) =>
        Dot2d(point - axisOrigin, axisDirection);

    private static double GetAlignedMeasurement(Point3d firstPoint, Point3d secondPoint)
    {
        var delta = secondPoint - firstPoint;
        return Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
    }

    private static double Dot2d(Vector3d left, Vector3d right) => (left.X * right.X) + (left.Y * right.Y);

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static void AddUniquePoint(List<Point3d> points, Point3d point)
    {
        foreach (var existing in points)
        {
            if (PointsCoincide(existing, point))
                return;
        }

        points.Add(point);
    }

    private static bool PointsCoincide(Point3d left, Point3d right) =>
        left.DistanceTo(right) < PointTolerance;

    private static int IndexOfObjectId(IList<ObjectId> dimensionIds, ObjectId dimensionId)
    {
        for (var index = 0; index < dimensionIds.Count; index++)
        {
            if (dimensionIds[index] == dimensionId)
                return index;
        }

        return -1;
    }

    private static double NormalizeRotation(double rotation)
    {
        var normalized = rotation % (Math.PI * 2.0);
        if (normalized < 0.0)
            normalized += Math.PI * 2.0;
        return normalized;
    }

    private static string FormatMeasurement(double measurement) => measurement.ToString("0.###");

    private static void WriteAvoidanceFailure(Document doc, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            doc.Editor.WriteMessage($"\nC_TOOL：{error}");
    }

    private enum DimensionChainKind
    {
        Linear,
        Aligned
    }

    private enum LinearDimensionOrientation
    {
        Horizontal,
        Vertical
    }

    private sealed class MergeContext
    {
        internal MergeContext(
            DimensionChainKind kind,
            ObjectId seedDimensionId,
            List<Point3d> points,
            List<ObjectId> dimensionIds,
            int selectedIndex,
            LinearDimensionOrientation orientation,
            double dimLineOrdinate,
            double signedOffset)
        {
            Kind = kind;
            SeedDimensionId = seedDimensionId;
            Points = points;
            DimensionIds = dimensionIds;
            SelectedIndex = selectedIndex;
            Orientation = orientation;
            DimLineOrdinate = dimLineOrdinate;
            SignedOffset = signedOffset;
        }

        internal DimensionChainKind Kind { get; }

        internal ObjectId SeedDimensionId { get; }

        internal List<Point3d> Points { get; }

        internal List<ObjectId> DimensionIds { get; }

        internal int SelectedIndex { get; }

        internal LinearDimensionOrientation Orientation { get; }

        internal double DimLineOrdinate { get; }

        internal double SignedOffset { get; }
    }

    private readonly struct MergePlan
    {
        internal MergePlan(MergeContext context, int rangeStartIndex, int rangeEndIndex)
        {
            Context = context;
            RangeStartIndex = rangeStartIndex;
            RangeEndIndex = rangeEndIndex;
        }

        internal MergeContext Context { get; }

        internal int RangeStartIndex { get; }

        internal int RangeEndIndex { get; }
    }
}
