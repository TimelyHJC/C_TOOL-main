using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddDimensionChainTextAvoidanceService
{
    private const double PointTolerance = 1e-6;
    private const double LayoutComparisonTolerance = 1e-4;
    private const double ParallelDotTolerance = 0.999;
    private const double EstimatedCharWidthFactor = 0.82;
    private const double LaneSpacingFactor = 1.2;
    private const double HorizontalShiftLimitFactor = 1.25;
    private const double HorizontalPlacementGapFactor = 0.8;
    private const double TextBoundsExpansionFactor = 1.5;
    private const double MinimumTextGapByHeightFactor = 0.6;
    private const double MinimumTextGapByDimGapFactor = 2.5;

    private delegate LineAnchorBuildResult LineAnchorBuilder(Point3d firstPoint, Point3d secondPoint);

    internal static bool TryApplyAlignedChain(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        double signedOffset,
        out string error)
    {
        return TryApplyDimensionIds(doc, dimensionIds, DddPluginCommandIds.DimAligned, out _, out _, out error);
    }

    internal static bool TryApplyLinearChain(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        bool isHorizontal,
        double dimLineOrdinate,
        out string error)
    {
        return TryApplyDimensionIds(doc, dimensionIds, DddPluginCommandIds.DimLinear, out _, out _, out error);
    }

    internal static bool TryApplySameRow(
        Document doc,
        ObjectId seedId,
        string commandTag,
        out int rowCount,
        out int adjustedCount,
        out string error)
    {
        rowCount = 0;
        adjustedCount = 0;
        error = string.Empty;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (!TryBuildSameRowGroup(tr, seedId, out var rowGroup, out error))
                    return false;

                rowCount = rowGroup.Items.Count;
                adjustedCount = ApplyRowGroup(commandTag, rowGroup);
                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（无效操作）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（CAD）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（参数）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
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
        return TryApplyDimensionIdsCore(
            doc,
            dimensionIds,
            commandTag,
            preferCurrentPlacement,
            out rowCount,
            out adjustedCount,
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
        return TryApplyDimensionIdsCore(
            doc,
            dimensionIds,
            commandTag,
            preferCurrentPlacement: false,
            out rowCount,
            out adjustedCount,
            out error);
    }

    private static bool TryApplyDimensionIdsCore(
        Document doc,
        IList<ObjectId> dimensionIds,
        string commandTag,
        bool preferCurrentPlacement,
        out int rowCount,
        out int adjustedCount,
        out string error)
    {
        rowCount = 0;
        adjustedCount = 0;
        error = string.Empty;

        if (dimensionIds == null || dimensionIds.Count == 0)
            return true;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (!TryBuildRowGroups(tr, dimensionIds, preferCurrentPlacement, out var rowGroups, out error))
                    return false;

                foreach (var rowGroup in rowGroups)
                {
                    rowCount += rowGroup.Items.Count;
                    adjustedCount += ApplyRowGroup(commandTag, rowGroup);
                }

                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（无效操作）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（CAD）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（参数）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
    }

    private static int ApplyRowGroup(string commandTag, DimensionRowGroup rowGroup)
    {
        rowGroup.Items.Sort(static (left, right) =>
        {
            var compare = left.IntervalStart.CompareTo(right.IntervalStart);
            if (compare != 0)
                return compare;
            return left.IntervalEnd.CompareTo(right.IntervalEnd);
        });

        var conflicted = BuildConflictFlags(rowGroup.Items);
        LogAvoidanceSnapshot(commandTag, "before", rowGroup.Axis, rowGroup.ReferenceNormal, rowGroup.Items, conflicted);
        var adjustedCount = ApplyNormalizedRowItems(commandTag, rowGroup.Items, rowGroup.Axis, conflicted);
        LogAvoidanceSnapshot(commandTag, "after", rowGroup.Axis, rowGroup.ReferenceNormal, rowGroup.Items, conflicted);
        return adjustedCount;
    }

    private static bool TryBuildSameRowGroup(
        Transaction tr,
        ObjectId seedId,
        out DimensionRowGroup rowGroup,
        out string error)
    {
        rowGroup = null!;
        error = string.Empty;

        var seedObject = tr.GetObject(seedId, OpenMode.ForWrite, false);
        if (seedObject is not Dimension seedDimension)
        {
            error = "所选对象不是标注。";
            return false;
        }

        if (!TryBuildPlacementContext(seedDimension, out var seedContext, out error))
            return false;

        rowGroup = new DimensionRowGroup(
            seedDimension.OwnerId,
            seedContext.Axis,
            seedContext.ReferenceNormal,
            seedContext.LineAnchor,
            seedContext.RowTolerance);

        var ownerRecord = (BlockTableRecord)tr.GetObject(seedDimension.OwnerId, OpenMode.ForRead);
        foreach (ObjectId entityId in ownerRecord)
        {
            if (!entityId.IsValid || entityId.IsErased)
                continue;

            var dbObject = tr.GetObject(entityId, OpenMode.ForWrite, false);
            if (dbObject is not Dimension dimension)
                continue;

            if (!TryBuildPlacementContext(dimension, out var context, out _))
                continue;

            if (!CanShareRow(rowGroup, context))
                continue;

            if (!TryBuildUnifiedRowItem(context, out var rowItem, out error))
                return false;

            rowGroup.Add(rowItem, context.RowTolerance);
        }

        return true;
    }

    private static bool TryBuildRowGroups(
        Transaction tr,
        IList<ObjectId> dimensionIds,
        bool preferCurrentPlacement,
        out List<DimensionRowGroup> rowGroups,
        out string error)
    {
        rowGroups = new List<DimensionRowGroup>();
        error = string.Empty;
        var seen = new HashSet<ObjectId>();

        foreach (var dimensionId in dimensionIds)
        {
            if (!dimensionId.IsValid || dimensionId.IsErased || !seen.Add(dimensionId))
                continue;

            var dbObject = tr.GetObject(dimensionId, OpenMode.ForWrite, false);
            if (dbObject is not Dimension dimension)
                continue;

            if (!TryBuildPlacementContext(dimension, out var context, out error))
                return false;

            if (!TryBuildUnifiedRowItem(context, preferCurrentPlacement, out var rowItem, out error))
                return false;

            var targetGroup = FindMatchingRowGroup(rowGroups, context);
            if (targetGroup == null)
            {
                targetGroup = new DimensionRowGroup(
                    dimension.OwnerId,
                    context.Axis,
                    context.ReferenceNormal,
                    context.LineAnchor,
                    context.RowTolerance);
                rowGroups.Add(targetGroup);
            }

            targetGroup.Add(rowItem, context.RowTolerance);
        }

        return true;
    }

    private static DimensionRowGroup? FindMatchingRowGroup(
        List<DimensionRowGroup> rowGroups,
        DimensionPlacementContext context)
    {
        foreach (var rowGroup in rowGroups)
        {
            if (CanShareRow(rowGroup, context))
                return rowGroup;
        }

        return null;
    }

    private static bool CanShareRow(DimensionRowGroup rowGroup, DimensionPlacementContext context)
    {
        if (context.OwnerId != rowGroup.OwnerId)
            return false;

        if (!AreParallel(rowGroup.Axis, context.Axis))
            return false;

        var rowDistance = Math.Abs(Dot2d(context.LineAnchor - rowGroup.LineAnchor, rowGroup.ReferenceNormal));
        return rowDistance <= Math.Max(rowGroup.RowTolerance, context.RowTolerance);
    }

    private static bool TryBuildUnifiedRowItem(
        DimensionPlacementContext context,
        out DimensionRowItem rowItem,
        out string error)
    {
        return TryBuildUnifiedRowItem(
            context,
            preferCurrentPlacement: false,
            out rowItem,
            out error);
    }

    private static bool TryBuildUnifiedRowItem(
        DimensionPlacementContext context,
        bool preferCurrentPlacement,
        out DimensionRowItem rowItem,
        out string error)
    {
        rowItem = null!;
        error = string.Empty;

        if (!TryBuildTextLayoutSnapshot(context, useDefaultPlacement: false, out var currentLayout, out error))
            return false;

        if (!TryBuildTextLayoutSnapshot(context, useDefaultPlacement: true, out var preferredLayout, out error))
            return false;

        var anchorLayout = preferCurrentPlacement ? currentLayout : preferredLayout;
        var usesAnchorPlacement = preferCurrentPlacement || currentLayout.UsesDefaultTextPosition;
        rowItem = new DimensionRowItem(
            context.Dimension,
            context.LineAnchor,
            anchorLayout.TextPosition,
            anchorLayout.IntervalStart,
            anchorLayout.IntervalEnd,
            anchorLayout.Center,
            anchorLayout.VerticalCenter,
            Math.Max(anchorLayout.HalfHeight, currentLayout.HalfHeight),
            currentLayout.LaneIndex,
            Math.Max(anchorLayout.LaneSpacing, currentLayout.LaneSpacing),
            anchorLayout.OutwardNormal,
            Math.Max(anchorLayout.HorizontalClearance, currentLayout.HorizontalClearance),
            usesAnchorPlacement,
            currentLayout.TextPosition);
        return true;
    }

    private static bool TryBuildPlacementContext(
        Dimension dimension,
        out DimensionPlacementContext context,
        out string error)
    {
        context = default;
        error = string.Empty;

        return dimension switch
        {
            RotatedDimension rotated => TryBuildPlacementContext(
                rotated,
                rotated.XLine1Point,
                rotated.XLine2Point,
                rotated.DimLinePoint,
                new Vector3d(Math.Cos(rotated.Rotation), Math.Sin(rotated.Rotation), 0.0),
                out context,
                out error),
            AlignedDimension aligned => TryBuildPlacementContext(
                aligned,
                aligned.XLine1Point,
                aligned.XLine2Point,
                aligned.DimLinePoint,
                aligned.XLine2Point - aligned.XLine1Point,
                out context,
                out error),
            _ => UnsupportedDimension(out context, out error)
        };
    }

    private static bool TryBuildPlacementContext(
        Dimension dimension,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d dimLinePoint,
        Vector3d axisSource,
        out DimensionPlacementContext context,
        out string error)
    {
        context = default;
        error = string.Empty;

        if (!TryNormalizePlanar(axisSource, out var axis))
        {
            error = "标注方向无效，无法整理文字避让。";
            return false;
        }

        var referenceNormal = new Vector3d(-axis.Y, axis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "标注法向无效，无法整理文字避让。";
            return false;
        }

        var midPoint = MidPoint(firstPoint, secondPoint);
        var lineAnchor = ProjectPointOntoLine(midPoint, dimLinePoint, axis);
        var rowTolerance = Math.Max(dimension.Dimgap * 2.0, dimension.Dimtxt * 0.5);

        context = new DimensionPlacementContext(
            dimension,
            dimension.OwnerId,
            axis,
            referenceNormal,
            firstPoint,
            secondPoint,
            midPoint,
            lineAnchor,
            rowTolerance);
        return true;
    }

    private static bool UnsupportedDimension(out DimensionPlacementContext context, out string error)
    {
        context = default;
        error = "仅支持线性标注与对齐标注。";
        return false;
    }

    private static bool TryBuildTextLayoutSnapshot(
        DimensionPlacementContext context,
        bool useDefaultPlacement,
        out TextLayoutSnapshot snapshot,
        out string error)
    {
        snapshot = default;
        error = string.Empty;

        Dimension? clonedDimension = null;
        try
        {
            var workingDimension = context.Dimension;
            var currentUsesDefault = TryReadUsingDefaultTextPosition(context.Dimension);
            if (useDefaultPlacement && !currentUsesDefault)
            {
                clonedDimension = context.Dimension.Clone() as Dimension;
                if (clonedDimension == null)
                {
                    error = "无法创建标注文字布局副本。";
                    return false;
                }

                workingDimension = clonedDimension;
                workingDimension.UsingDefaultTextPosition = true;
            }

            workingDimension.RecomputeDimensionBlock(true);

            var textPosition = GetEffectiveTextPosition(workingDimension, context.LineAnchor);
            var outwardNormal = ResolveTextAvoidanceNormal(
                context.ReferenceNormal,
                context.MidPoint,
                context.LineAnchor,
                textPosition);

            var textWidth = EstimateTextWidth(workingDimension);
            var textHeight = Math.Max(workingDimension.Dimtxt, PointTolerance);
            var horizontalPadding = Math.Max(workingDimension.Dimgap * 2.0, textHeight * 0.35) * TextBoundsExpansionFactor;
            var center = textPosition.GetAsVector().DotProduct(context.Axis);
            var halfSpan = (textWidth + horizontalPadding) * 0.5;
            var verticalCenter = Math.Abs(Dot2d(textPosition - context.LineAnchor, outwardNormal));
            var halfHeight = (textHeight + horizontalPadding) * 0.5;

            if (TryProjectDimensionTextBounds(
                    workingDimension,
                    context.Axis,
                    outwardNormal,
                    out var actualCenterPoint,
                    out var intervalStart,
                    out var intervalEnd,
                    out var normalCenter,
                    out var actualHalfHeight,
                    out var actualTextHeight))
            {
                textPosition = actualCenterPoint;
                center = actualCenterPoint.GetAsVector().DotProduct(context.Axis);
                halfSpan = Math.Max((intervalEnd - intervalStart) * 0.5, PointTolerance);
                verticalCenter = Math.Abs(normalCenter - context.LineAnchor.GetAsVector().DotProduct(outwardNormal));
                halfHeight = Math.Max(actualHalfHeight + (horizontalPadding * 0.5), PointTolerance);
                textHeight = Math.Max(actualTextHeight, textHeight);
            }

            var laneSpacing = Math.Max(textHeight * LaneSpacingFactor, workingDimension.Dimgap + textHeight);
            var laneIndex = verticalCenter <= PointTolerance
                ? 0
                : Math.Max(0, (int)Math.Round(verticalCenter / laneSpacing, MidpointRounding.AwayFromZero));
            var horizontalClearance = ResolveHorizontalClearance(workingDimension, textHeight, horizontalPadding);

            snapshot = new TextLayoutSnapshot(
                useDefaultPlacement || currentUsesDefault,
                textPosition,
                center - halfSpan,
                center + halfSpan,
                center,
                verticalCenter,
                halfHeight,
                laneIndex,
                laneSpacing,
                outwardNormal,
                horizontalClearance);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            C_toolsDiagnostics.LogNonFatal("文字布局快照失败（无效操作）", ex);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            error = ex.Message;
            C_toolsDiagnostics.LogNonFatal("文字布局快照失败（CAD）", ex);
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            C_toolsDiagnostics.LogNonFatal("文字布局快照失败（参数）", ex);
            return false;
        }
        finally
        {
            clonedDimension?.Dispose();
        }
    }

    private static bool TryApplyChain(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        Vector3d unitAxis,
        Vector3d referenceNormal,
        LineAnchorBuilder lineAnchorBuilder,
        string commandTag,
        out string error)
    {
        error = string.Empty;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!TryBuildRowItems(
                        tr,
                        points,
                        dimensionIds,
                        unitAxis,
                        referenceNormal,
                        lineAnchorBuilder,
                        out var rowItems,
                        out error))
                {
                    return false;
                }

                rowItems.Sort(static (left, right) =>
                {
                    var compare = left.IntervalStart.CompareTo(right.IntervalStart);
                    if (compare != 0)
                        return compare;
                    return left.IntervalEnd.CompareTo(right.IntervalEnd);
                });

                var conflicted = BuildConflictFlags(rowItems);
                LogAvoidanceSnapshot(commandTag, "before", unitAxis, referenceNormal, rowItems, conflicted);
                if (!HasAnyConflict(conflicted))
                {
                    LogAvoidanceSnapshot(commandTag, "after", unitAxis, referenceNormal, rowItems, conflicted);
                    tr.Commit();
                    return true;
                }

                ApplyRowItems(commandTag, rowItems, conflicted, unitAxis);
                LogAvoidanceSnapshot(commandTag, "after", unitAxis, referenceNormal, rowItems, conflicted);
                tr.Commit();
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（无效操作）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（CAD）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} 自动整理文字避让失败（参数）", ex);
            error = $"文字避让失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryBuildRowItems(
        Transaction tr,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        Vector3d unitAxis,
        Vector3d referenceNormal,
        LineAnchorBuilder lineAnchorBuilder,
        out List<DimensionRowItem> rowItems,
        out string error)
    {
        rowItems = new List<DimensionRowItem>();
        error = string.Empty;

        var count = Math.Min(points.Count - 1, dimensionIds.Count);
        for (var index = 0; index < count; index++)
        {
            var dbObject = tr.GetObject(dimensionIds[index], OpenMode.ForWrite, false);
            if (dbObject is not Dimension dimension)
                continue;

            var firstPoint = points[index];
            var secondPoint = points[index + 1];
            var lineAnchorResult = lineAnchorBuilder(firstPoint, secondPoint);
            if (!lineAnchorResult.Success)
            {
                error = lineAnchorResult.Error;
                return false;
            }

            var midpoint = MidPoint(firstPoint, secondPoint);
            var lineAnchor = lineAnchorResult.LineAnchor;
            var defaultTextPosition = GetEffectiveTextPosition(dimension, lineAnchor);
            var outwardNormal = ResolveTextAvoidanceNormal(referenceNormal, midpoint, lineAnchor, defaultTextPosition);
            var centerPoint = midpoint;
            var center = centerPoint.GetAsVector().DotProduct(unitAxis);
            var textWidth = EstimateTextWidth(dimension);
            var textHeight = Math.Max(dimension.Dimtxt, PointTolerance);
            var horizontalPadding = Math.Max(dimension.Dimgap * 2.0, textHeight * 0.35) * TextBoundsExpansionFactor;
            var laneSpacing = Math.Max(textHeight * LaneSpacingFactor, dimension.Dimgap + textHeight);
            var halfSpan = (textWidth + horizontalPadding) * 0.5;
            var verticalCenter = 0.0;
            var halfHeight = (textHeight + horizontalPadding) * 0.5;
            var currentLaneIndex = 0;

            dimension.RecomputeDimensionBlock(true);
            if (TryProjectDimensionTextBounds(
                    dimension,
                    unitAxis,
                    outwardNormal,
                    out var actualCenterPoint,
                    out var intervalStart,
                    out var intervalEnd,
                    out var normalCenter,
                    out var actualHalfHeight,
                    out var actualTextHeight))
            {
                centerPoint = actualCenterPoint;
                center = centerPoint.GetAsVector().DotProduct(unitAxis);
                halfSpan = Math.Max(((intervalEnd - intervalStart) + horizontalPadding) * 0.5, PointTolerance);
                verticalCenter = Math.Abs(normalCenter - lineAnchor.GetAsVector().DotProduct(outwardNormal));
                halfHeight = Math.Max(actualHalfHeight + (horizontalPadding * 0.5), PointTolerance);
                textHeight = Math.Max(actualTextHeight, textHeight);
                laneSpacing = Math.Max(textHeight * LaneSpacingFactor, dimension.Dimgap + textHeight);
                if (verticalCenter > PointTolerance)
                {
                    currentLaneIndex = Math.Max(0, (int)Math.Round(verticalCenter / laneSpacing, MidpointRounding.AwayFromZero));
                }
            }

            var horizontalClearance = ResolveHorizontalClearance(dimension, textHeight, horizontalPadding);

            rowItems.Add(new DimensionRowItem(
                dimension,
                lineAnchor,
                defaultTextPosition,
                center - halfSpan,
                center + halfSpan,
                center,
                verticalCenter,
                halfHeight,
                currentLaneIndex,
                laneSpacing,
                outwardNormal,
                horizontalClearance));
        }

        return true;
    }

    private static int ApplyNormalizedRowItems(
        string commandTag,
        List<DimensionRowItem> rowItems,
        Vector3d unitAxis,
        bool[] conflicted)
    {
        if (rowItems.Count == 0)
            return 0;

        if (!HasAnyConflict(conflicted))
            return 0;

        if (TryBuildHorizontalReflowTargets(rowItems, out var reflowedIntervals))
        {
            var adjustedByReflow = 0;
            for (var index = 0; index < rowItems.Count; index++)
            {
                var item = rowItems[index];
                var interval = reflowedIntervals[index];
                var targetCenter = (interval.Start + interval.End) * 0.5;
                var horizontalShift = targetCenter - item.Center;
                var target = item.DefaultTextPosition + (unitAxis * (targetCenter - item.Center));
                var changed = false;
                if (Math.Abs(horizontalShift) > PointTolerance)
                {
                    changed = ApplyCustomTargetIfNeeded(item, target);
                }
                else if (index < conflicted.Length && conflicted[index])
                {
                    changed = ApplyDefaultPlacementIfNeeded(item);
                }

                if (changed)
                    adjustedByReflow++;

                LogLaneDecision(commandTag, item, item.CurrentLaneIndex, 0, item.OutwardNormal, target);
            }

            return adjustedByReflow;
        }

        var laneOccupancies = new List<List<OccupiedInterval>>();
        var baseLane = EnsureLaneOccupancy(laneOccupancies, 0);
        var rowLaneSpacing = ResolveRowLaneSpacing(rowItems);
        var rowBaseNormalDistance = ResolveRowBaseNormalDistance(rowItems);
        var adjustedCount = 0;

        for (var index = 0; index < rowItems.Count; index++)
        {
            var item = rowItems[index];
            if (IsIntervalFree(baseLane, item.IntervalStart, item.IntervalEnd, item.HorizontalClearance))
            {
                AddOccupiedInterval(baseLane, item.IntervalStart, item.IntervalEnd, item.HorizontalClearance);
                var shouldResetToDefault = index < conflicted.Length && conflicted[index];
                if (shouldResetToDefault && ApplyDefaultPlacementIfNeeded(item))
                    adjustedCount++;

                LogLaneDecision(
                    commandTag,
                    item,
                    item.CurrentLaneIndex,
                    0,
                    item.OutwardNormal,
                    shouldResetToDefault ? item.DefaultTextPosition : null);
                continue;
            }

            if (TryBuildHorizontalTarget(
                    item,
                    unitAxis,
                    baseLane,
                    out var horizontalTarget,
                    out var horizontalStart,
                    out var horizontalEnd))
            {
                AddOccupiedInterval(baseLane, horizontalStart, horizontalEnd, item.HorizontalClearance);
                if (ApplyCustomTargetIfNeeded(item, horizontalTarget))
                    adjustedCount++;

                LogLaneDecision(commandTag, item, item.CurrentLaneIndex, 0, item.OutwardNormal, horizontalTarget);
                continue;
            }

            var laneIndex = FindAvailableLane(laneOccupancies, 1, item.IntervalStart, item.IntervalEnd, item.HorizontalClearance);
            var target = BuildLaneTarget(item, laneIndex, rowLaneSpacing, rowBaseNormalDistance);
            AddOccupiedInterval(EnsureLaneOccupancy(laneOccupancies, laneIndex), item.IntervalStart, item.IntervalEnd, item.HorizontalClearance);
            if (ApplyCustomTargetIfNeeded(item, target))
                adjustedCount++;

            LogLaneDecision(commandTag, item, item.CurrentLaneIndex, laneIndex, item.OutwardNormal, target);
        }

        return adjustedCount;
    }

    private static bool ApplyDefaultPlacementIfNeeded(DimensionRowItem item)
    {
        if (item.UsesDefaultTextPosition && PointsNearlyEqual(item.CurrentTextPosition, item.DefaultTextPosition))
            return false;

        item.Dimension.UsingDefaultTextPosition = true;
        item.Dimension.RecomputeDimensionBlock(true);
        return true;
    }

    private static bool ApplyCustomTargetIfNeeded(DimensionRowItem item, Point3d target)
    {
        if (!item.UsesDefaultTextPosition && PointsNearlyEqual(item.CurrentTextPosition, target))
            return false;

        item.Dimension.Dimatfit = 3;
        item.Dimension.Dimtix = false;
        item.Dimension.Dimtofl = true;
        item.Dimension.Dimtmove = 2;
        item.Dimension.UsingDefaultTextPosition = false;
        item.Dimension.TextPosition = target;
        item.Dimension.RecomputeDimensionBlock(true);
        return true;
    }

    private static bool PointsNearlyEqual(Point3d left, Point3d right) =>
        left.DistanceTo(right) <= LayoutComparisonTolerance;

    private static void ApplyRowItems(string commandTag, List<DimensionRowItem> rowItems, bool[] conflicted, Vector3d unitAxis)
    {
        var laneOccupancies = new List<List<OccupiedInterval>>();
        var rowLaneSpacing = ResolveRowLaneSpacing(rowItems);
        var rowBaseNormalDistance = ResolveRowBaseNormalDistance(rowItems);

        for (var index = 0; index < rowItems.Count; index++)
        {
            var item = rowItems[index];
            var currentLaneIndex = Math.Max(0, item.CurrentLaneIndex);
            var currentLane = EnsureLaneOccupancy(laneOccupancies, currentLaneIndex);
            if (IsIntervalFree(currentLane, item.IntervalStart, item.IntervalEnd, item.HorizontalClearance))
            {
                LogLaneDecision(commandTag, item, currentLaneIndex, currentLaneIndex, item.OutwardNormal, null);
                AddOccupiedInterval(currentLane, item.IntervalStart, item.IntervalEnd, item.HorizontalClearance);
                continue;
            }

            if (TryBuildHorizontalTarget(
                    item,
                    unitAxis,
                    currentLane,
                    out var horizontalTarget,
                    out var horizontalStart,
                    out var horizontalEnd))
            {
                ApplyItemTarget(item, horizontalTarget);
                AddOccupiedInterval(currentLane, horizontalStart, horizontalEnd, item.HorizontalClearance);
                LogLaneDecision(commandTag, item, currentLaneIndex, currentLaneIndex, item.OutwardNormal, horizontalTarget);
                continue;
            }

            var laneIndex = FindAvailableLane(laneOccupancies, currentLaneIndex + 1, item.IntervalStart, item.IntervalEnd, item.HorizontalClearance);
            var target = BuildLaneTarget(item, laneIndex, rowLaneSpacing, rowBaseNormalDistance);
            ApplyItemTarget(item, target);
            AddOccupiedInterval(EnsureLaneOccupancy(laneOccupancies, laneIndex), item.IntervalStart, item.IntervalEnd, item.HorizontalClearance);
            LogLaneDecision(commandTag, item, currentLaneIndex, laneIndex, item.OutwardNormal, target);
        }
    }

    private static bool TryBuildHorizontalReflowTargets(
        List<DimensionRowItem> rowItems,
        out List<ReflowedInterval> reflowedIntervals)
    {
        reflowedIntervals = new List<ReflowedInterval>(rowItems.Count);
        foreach (var item in rowItems)
            reflowedIntervals.Add(new ReflowedInterval(item.IntervalStart, item.IntervalEnd));

        if (rowItems.Count < 2)
            return true;

        var passLimit = Math.Max(1, rowItems.Count * rowItems.Count);
        for (var pass = 0; pass < passLimit; pass++)
        {
            if (!TryFindFirstHorizontalConflict(rowItems, reflowedIntervals, out var conflictIndex))
                return true;

            var startIndex = conflictIndex;
            var endIndex = conflictIndex + 1;
            var expanded = true;
            List<ReflowedInterval> candidateIntervals = null!;

            while (expanded)
            {
                if (!TryBuildReflowRange(rowItems, startIndex, endIndex, out candidateIntervals))
                    return false;

                expanded = false;
                if (startIndex > 0 &&
                    IntervalsConflict(
                        rowItems[startIndex - 1],
                        reflowedIntervals[startIndex - 1],
                        rowItems[startIndex],
                        candidateIntervals[0]))
                {
                    startIndex--;
                    expanded = true;
                }

                if (endIndex < rowItems.Count - 1 &&
                    IntervalsConflict(
                        rowItems[endIndex],
                        candidateIntervals[candidateIntervals.Count - 1],
                        rowItems[endIndex + 1],
                        reflowedIntervals[endIndex + 1]))
                {
                    endIndex++;
                    expanded = true;
                }
            }

            for (var index = startIndex; index <= endIndex; index++)
                reflowedIntervals[index] = candidateIntervals[index - startIndex];
        }

        return !TryFindFirstHorizontalConflict(rowItems, reflowedIntervals, out _);
    }

    private static bool TryFindFirstHorizontalConflict(
        List<DimensionRowItem> rowItems,
        List<ReflowedInterval> intervals,
        out int conflictIndex)
    {
        conflictIndex = -1;
        for (var index = 0; index < rowItems.Count - 1; index++)
        {
            if (!IntervalsConflict(rowItems[index], intervals[index], rowItems[index + 1], intervals[index + 1]))
                continue;

            conflictIndex = index;
            return true;
        }

        return false;
    }

    private static bool TryBuildReflowRange(
        List<DimensionRowItem> rowItems,
        int startIndex,
        int endIndex,
        out List<ReflowedInterval> intervals)
    {
        intervals = new List<ReflowedInterval>(Math.Max(0, endIndex - startIndex + 1));
        if (startIndex < 0 || endIndex >= rowItems.Count || startIndex > endIndex)
            return false;

        var relativeStart = 0.0;
        var minAnchor = double.PositiveInfinity;
        var maxAnchor = double.NegativeInfinity;
        var relativeStarts = new List<double>(endIndex - startIndex + 1);
        for (var index = startIndex; index <= endIndex; index++)
        {
            var item = rowItems[index];
            if (index > startIndex)
            {
                var previous = rowItems[index - 1];
                relativeStart += GetIntervalWidth(previous) + GetRequiredHorizontalGap(previous, item);
            }

            relativeStarts.Add(relativeStart);
            var relativeCenter = relativeStart + (GetIntervalWidth(item) * 0.5);
            var anchor = item.Center - relativeCenter;
            minAnchor = Math.Min(minAnchor, anchor);
            maxAnchor = Math.Max(maxAnchor, anchor);
        }

        if (!IsFiniteValue(minAnchor) || !IsFiniteValue(maxAnchor))
            return false;

        var groupStart = (minAnchor + maxAnchor) * 0.5;
        for (var offset = 0; offset < relativeStarts.Count; offset++)
        {
            var item = rowItems[startIndex + offset];
            var start = groupStart + relativeStarts[offset];
            var end = start + GetIntervalWidth(item);
            var targetCenter = (start + end) * 0.5;
            if (Math.Abs(targetCenter - item.Center) > GetMaximumHorizontalShift(item) + PointTolerance)
                return false;

            intervals.Add(new ReflowedInterval(start, end));
        }

        return true;
    }

    private static bool IntervalsConflict(
        DimensionRowItem left,
        ReflowedInterval leftInterval,
        DimensionRowItem right,
        ReflowedInterval rightInterval)
    {
        if (Math.Abs(left.VerticalCenter - right.VerticalCenter) >
            (left.HalfHeight + right.HalfHeight + PointTolerance))
        {
            return false;
        }

        var requiredGap = GetRequiredHorizontalGap(left, right);
        return rightInterval.Start < leftInterval.End + requiredGap - PointTolerance;
    }

    private static double GetIntervalWidth(DimensionRowItem item) =>
        Math.Max(item.IntervalEnd - item.IntervalStart, PointTolerance);

    private static double GetMaximumHorizontalShift(DimensionRowItem item) =>
        Math.Max(item.LaneSpacing * HorizontalShiftLimitFactor, PointTolerance);

    private static bool TryBuildHorizontalTarget(
        DimensionRowItem item,
        Vector3d unitAxis,
        List<OccupiedInterval> occupiedIntervals,
        out Point3d target,
        out double intervalStart,
        out double intervalEnd)
    {
        target = default;
        intervalStart = 0.0;
        intervalEnd = 0.0;

        var width = item.IntervalEnd - item.IntervalStart;
        var maxShift = Math.Max(item.LaneSpacing * HorizontalShiftLimitFactor, PointTolerance);
        var horizontalClearance = Math.Max(item.HorizontalClearance, PointTolerance);
        var hasLeft = TryFindLeftPlacement(item.IntervalStart, width, maxShift, horizontalClearance, occupiedIntervals, out var leftStart, out var leftEnd);
        var hasRight = TryFindRightPlacement(item.IntervalStart, width, maxShift, horizontalClearance, occupiedIntervals, out var rightStart, out var rightEnd);

        if (!hasLeft && !hasRight)
            return false;

        var leftShift = hasLeft ? Math.Abs(((leftStart + leftEnd) * 0.5) - item.Center) : double.PositiveInfinity;
        var rightShift = hasRight ? Math.Abs(((rightStart + rightEnd) * 0.5) - item.Center) : double.PositiveInfinity;
        if (hasLeft && leftShift <= rightShift + PointTolerance)
        {
            intervalStart = leftStart;
            intervalEnd = leftEnd;
        }
        else
        {
            intervalStart = rightStart;
            intervalEnd = rightEnd;
        }

        var targetCenter = (intervalStart + intervalEnd) * 0.5;
        target = item.DefaultTextPosition + (unitAxis * (targetCenter - item.Center));
        return true;
    }

    private static bool TryFindLeftPlacement(
        double originalStart,
        double width,
        double maxShift,
        double horizontalClearance,
        List<OccupiedInterval> occupiedIntervals,
        out double targetStart,
        out double targetEnd)
    {
        targetStart = 0.0;
        targetEnd = 0.0;

        var found = false;
        var bestStart = 0.0;
        var bestShift = double.PositiveInfinity;
        var gapStart = double.NegativeInfinity;

        foreach (var interval in occupiedIntervals)
        {
            var requiredGap = GetRequiredHorizontalGap(horizontalClearance, interval);
            var candidateStart = Math.Min(originalStart, interval.Start - requiredGap - width);
            var candidateShift = originalStart - candidateStart;
            if (candidateShift > PointTolerance &&
                candidateShift <= maxShift + PointTolerance &&
                candidateStart >= gapStart - PointTolerance &&
                candidateShift < bestShift)
            {
                bestStart = candidateStart;
                bestShift = candidateShift;
                found = true;
            }

            gapStart = interval.End + requiredGap;
        }

        if (!found)
            return false;

        targetStart = bestStart;
        targetEnd = bestStart + width;
        return true;
    }

    private static bool TryFindRightPlacement(
        double originalStart,
        double width,
        double maxShift,
        double horizontalClearance,
        List<OccupiedInterval> occupiedIntervals,
        out double targetStart,
        out double targetEnd)
    {
        targetStart = originalStart;
        targetEnd = originalStart + width;

        foreach (var interval in occupiedIntervals)
        {
            var requiredGap = GetRequiredHorizontalGap(horizontalClearance, interval);
            if (interval.End + requiredGap < targetStart - PointTolerance)
                continue;

            if (interval.Start - requiredGap > targetEnd + PointTolerance)
                break;

            targetStart = interval.End + requiredGap;
            targetEnd = targetStart + width;
        }

        var shift = targetStart - originalStart;
        if (shift <= PointTolerance || shift > maxShift + PointTolerance)
            return false;

        return true;
    }

    private static int FindAvailableLane(
        List<List<OccupiedInterval>> laneOccupancies,
        int startLaneIndex,
        double intervalStart,
        double intervalEnd,
        double horizontalClearance)
    {
        var laneIndex = Math.Max(0, startLaneIndex);
        while (true)
        {
            var lane = EnsureLaneOccupancy(laneOccupancies, laneIndex);
            if (IsIntervalFree(lane, intervalStart, intervalEnd, horizontalClearance))
                return laneIndex;

            laneIndex++;
        }
    }

    private static List<OccupiedInterval> EnsureLaneOccupancy(List<List<OccupiedInterval>> laneOccupancies, int laneIndex)
    {
        while (laneOccupancies.Count <= laneIndex)
            laneOccupancies.Add(new List<OccupiedInterval>());
        return laneOccupancies[laneIndex];
    }

    private static bool IsIntervalFree(List<OccupiedInterval> occupiedIntervals, double intervalStart, double intervalEnd, double horizontalClearance)
    {
        foreach (var interval in occupiedIntervals)
        {
            var requiredGap = GetRequiredHorizontalGap(horizontalClearance, interval);
            if (intervalEnd + requiredGap <= interval.Start + PointTolerance)
                return true;

            if (intervalStart < interval.End + requiredGap - PointTolerance &&
                intervalEnd > interval.Start - requiredGap + PointTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddOccupiedInterval(List<OccupiedInterval> occupiedIntervals, double intervalStart, double intervalEnd, double horizontalClearance)
    {
        var inserted = false;
        for (var index = 0; index < occupiedIntervals.Count; index++)
        {
            if (intervalStart < occupiedIntervals[index].Start - PointTolerance)
            {
                occupiedIntervals.Insert(index, new OccupiedInterval(intervalStart, intervalEnd, horizontalClearance));
                inserted = true;
                break;
            }
        }

        if (!inserted)
            occupiedIntervals.Add(new OccupiedInterval(intervalStart, intervalEnd, horizontalClearance));
    }

    private static bool[] BuildConflictFlags(List<DimensionRowItem> rowItems)
    {
        var conflicted = new bool[rowItems.Count];
        for (var leftIndex = 0; leftIndex < rowItems.Count; leftIndex++)
        {
            var left = rowItems[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < rowItems.Count; rightIndex++)
            {
                var right = rowItems[rightIndex];
                var requiredGap = GetRequiredHorizontalGap(left, right);
                if (right.IntervalStart > left.IntervalEnd + requiredGap + PointTolerance)
                    break;

                if (Math.Abs(left.VerticalCenter - right.VerticalCenter) >
                    (left.HalfHeight + right.HalfHeight + PointTolerance))
                {
                    continue;
                }

                conflicted[leftIndex] = true;
                conflicted[rightIndex] = true;
            }
        }

        return conflicted;
    }

    private static bool HasAnyConflict(bool[] conflicted)
    {
        foreach (var item in conflicted)
        {
            if (item)
                return true;
        }

        return false;
    }

    private static void LogAvoidanceSnapshot(
        string commandTag,
        string stage,
        Vector3d unitAxis,
        Vector3d referenceNormal,
        List<DimensionRowItem> rowItems,
        bool[] conflicted)
    {
        C_toolsDiagnostics.LogNonFatal(
            $"{commandTag} 调试 {stage} rowCount={rowItems.Count} axis={FormatDebugVector(unitAxis)} referenceNormal={FormatDebugVector(referenceNormal)}");

        for (var index = 0; index < rowItems.Count; index++)
        {
            var item = rowItems[index];
            var conflictedFlag = index < conflicted.Length && conflicted[index] ? "1" : "0";
            C_toolsDiagnostics.LogNonFatal(
                $"{commandTag} 调试 {stage} idx={index} conflict={conflictedFlag} lane={item.CurrentLaneIndex} spacing={item.LaneSpacing:0.###} lineAnchor={FormatDebugPoint(item.LineAnchor)} defaultText={FormatDebugPoint(item.DefaultTextPosition)} outwardNormal={FormatDebugVector(item.OutwardNormal)} state={DescribeDimensionState(item.Dimension)}");
        }
    }

    private static void LogLaneDecision(
        string commandTag,
        DimensionRowItem item,
        int currentLaneIndex,
        int targetLaneIndex,
        Vector3d outwardNormal,
        Point3d? target)
    {
        var targetText = target.HasValue ? FormatDebugPoint(target.Value) : "keep";
        C_toolsDiagnostics.LogNonFatal(
            $"{commandTag} 调试 lane current={currentLaneIndex} target={targetLaneIndex} outwardNormal={FormatDebugVector(outwardNormal)} targetText={targetText} state={DescribeDimensionState(item.Dimension)}");
    }

    private static double ResolveRowLaneSpacing(List<DimensionRowItem> rowItems)
    {
        var laneSpacing = PointTolerance;
        foreach (var item in rowItems)
            laneSpacing = Math.Max(laneSpacing, item.LaneSpacing);

        return laneSpacing;
    }

    private static double ResolveRowBaseNormalDistance(List<DimensionRowItem> rowItems)
    {
        var distance = PointTolerance;
        foreach (var item in rowItems)
            distance = Math.Max(distance, item.VerticalCenter);

        return distance;
    }

    private static Point3d BuildLaneTarget(
        DimensionRowItem item,
        int targetLaneIndex,
        double laneSpacing,
        double baseNormalDistance)
    {
        if (targetLaneIndex <= 0)
            return item.DefaultTextPosition;

        var currentNormalDistance = Dot2d(item.DefaultTextPosition - item.LineAnchor, item.OutwardNormal);
        var targetNormalDistance = Math.Max(baseNormalDistance, PointTolerance) +
                                   (laneSpacing * Math.Max(0, targetLaneIndex - 1));
        return item.DefaultTextPosition + (item.OutwardNormal * (targetNormalDistance - currentNormalDistance));
    }

    private static string DescribeDimensionState(Dimension dimension)
    {
        var idText = TryGetObjectIdText(dimension);
        var usingDefaultTextPosition = TryGetUsingDefaultTextPosition(dimension);
        var textPosition = TryGetTextPositionText(dimension);
        var dimLinePoint = TryGetDimLinePointText(dimension);
        return $"id={idText} type={dimension.GetType().Name} UsingDefaultTextPosition={usingDefaultTextPosition} TextPosition={textPosition} DimLinePoint={dimLinePoint}";
    }

    private static string TryGetObjectIdText(DBObject dbObject)
    {
        try
        {
            return dbObject.ObjectId.IsValid ? dbObject.ObjectId.Handle.ToString() : "null";
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 ObjectId 失败（无效操作）", ex);
            return "error";
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 ObjectId 失败（CAD）", ex);
            return "error";
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 ObjectId 失败（参数）", ex);
            return "error";
        }
    }

    private static string TryGetUsingDefaultTextPosition(Dimension dimension)
    {
        try
        {
            return dimension.UsingDefaultTextPosition ? "true" : "false";
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 UsingDefaultTextPosition 失败（无效操作）", ex);
            return "error";
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 UsingDefaultTextPosition 失败（CAD）", ex);
            return "error";
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 UsingDefaultTextPosition 失败（参数）", ex);
            return "error";
        }
    }

    private static string TryGetTextPositionText(Dimension dimension)
    {
        try
        {
            return FormatDebugPoint(dimension.TextPosition);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 TextPosition 失败（无效操作）", ex);
            return "error";
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 TextPosition 失败（CAD）", ex);
            return "error";
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 TextPosition 失败（参数）", ex);
            return "error";
        }
    }

    private static string TryGetDimLinePointText(Dimension dimension)
    {
        try
        {
            return dimension switch
            {
                AlignedDimension alignedDimension => FormatDebugPoint(alignedDimension.DimLinePoint),
                RotatedDimension rotatedDimension => FormatDebugPoint(rotatedDimension.DimLinePoint),
                _ => "n/a"
            };
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 DimLinePoint 失败（无效操作）", ex);
            return "error";
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 DimLinePoint 失败（CAD）", ex);
            return "error";
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("调试读取 DimLinePoint 失败（参数）", ex);
            return "error";
        }
    }

    private static string FormatDebugPoint(Point3d point) =>
        $"({point.X:0.###},{point.Y:0.###},{point.Z:0.###})";

    private static string FormatDebugVector(Vector3d vector) =>
        $"({vector.X:0.###},{vector.Y:0.###},{vector.Z:0.###})";

    private static void ApplyItemTarget(DimensionRowItem item, Point3d target)
    {
        item.Dimension.Dimatfit = 3;
        item.Dimension.Dimtix = false;
        item.Dimension.Dimtofl = true;
        item.Dimension.Dimtmove = 2;
        item.Dimension.UsingDefaultTextPosition = false;
        item.Dimension.TextPosition = target;
        item.Dimension.RecomputeDimensionBlock(true);
    }

    private static LineAnchorBuildResult BuildAlignedLineAnchor(Point3d firstPoint, Point3d secondPoint, double signedOffset)
    {
        if (!TryGetPerpendicularUnit(firstPoint, secondPoint, out var perpendicular))
            return LineAnchorBuildResult.Failure("标注链段过短，无法整理文字避让。");

        return LineAnchorBuildResult.CreateSuccess(MidPoint(firstPoint, secondPoint) + (perpendicular * signedOffset));
    }

    private static LineAnchorBuildResult BuildLinearLineAnchor(
        Point3d firstPoint,
        Point3d secondPoint,
        bool isHorizontal,
        double dimLineOrdinate)
    {
        var midpoint = MidPoint(firstPoint, secondPoint);
        var lineAnchor = isHorizontal
            ? new Point3d(midpoint.X, dimLineOrdinate, midpoint.Z)
            : new Point3d(dimLineOrdinate, midpoint.Y, midpoint.Z);
        return LineAnchorBuildResult.CreateSuccess(lineAnchor);
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

    private static Vector3d ResolveTextAvoidanceNormal(
        Vector3d referenceNormal,
        Point3d midPoint,
        Point3d lineAnchor,
        Point3d textPosition)
    {
        var textOffset = Dot2d(textPosition - lineAnchor, referenceNormal);
        if (Math.Abs(textOffset) > PointTolerance)
            return textOffset >= 0.0 ? -referenceNormal : referenceNormal;

        var lineOffset = Dot2d(lineAnchor - midPoint, referenceNormal);
        if (Math.Abs(lineOffset) > PointTolerance)
            return lineOffset >= 0.0 ? -referenceNormal : referenceNormal;

        return referenceNormal;
    }

    private static double EstimateTextWidth(Dimension dimension)
    {
        string text;
        try
        {
            text = string.IsNullOrWhiteSpace(dimension.DimensionText)
                ? dimension.FormatMeasurement(dimension.Measurement, string.Empty)
                : dimension.DimensionText;
        }
        catch (InvalidOperationException)
        {
            text = dimension.Measurement.ToString("0.###", CultureInfo.InvariantCulture);
        }
        catch (AcadRuntimeException)
        {
            text = dimension.Measurement.ToString("0.###", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            text = dimension.Measurement.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(text))
            text = dimension.Measurement.ToString("0.###", CultureInfo.InvariantCulture);

        text = text.Replace("<>", dimension.Measurement.ToString("0.###", CultureInfo.InvariantCulture)).Trim();
        var charCount = Math.Max(text.Length, 1);
        return Math.Max(dimension.Dimtxt, charCount * dimension.Dimtxt * EstimatedCharWidthFactor);
    }

    private static bool TryReadUsingDefaultTextPosition(Dimension dimension)
    {
        try
        {
            return dimension.UsingDefaultTextPosition;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (AcadRuntimeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Point3d GetEffectiveTextPosition(Dimension dimension, Point3d fallback)
    {
        try
        {
            var textPosition = dimension.TextPosition;
            if (IsFiniteValue(textPosition.X) &&
                IsFiniteValue(textPosition.Y) &&
                IsFiniteValue(textPosition.Z))
            {
                return textPosition;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (AcadRuntimeException)
        {
        }
        catch (ArgumentException)
        {
        }

        return fallback;
    }

    private static Point3d ProjectPointOntoLine(Point3d point, Point3d linePoint, Vector3d axis)
    {
        var offset = point - linePoint;
        return linePoint + (axis * offset.DotProduct(axis));
    }

    private static bool TryProjectDimensionTextBounds(
        Dimension dimension,
        Vector3d axis,
        Vector3d normal,
        out Point3d textCenterPoint,
        out double intervalStart,
        out double intervalEnd,
        out double normalCenter,
        out double halfHeight,
        out double textHeight)
    {
        textCenterPoint = default;
        intervalStart = 0.0;
        intervalEnd = 0.0;
        normalCenter = 0.0;
        halfHeight = 0.0;
        textHeight = 0.0;

        if (!TryGetDimensionTextExtents(dimension, out var extents))
            return false;

        var corners = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z)
        };

        var minAxis = double.PositiveInfinity;
        var maxAxis = double.NegativeInfinity;
        var minNormal = double.PositiveInfinity;
        var maxNormal = double.NegativeInfinity;
        foreach (var corner in corners)
        {
            var axisValue = corner.GetAsVector().DotProduct(axis);
            var normalValue = corner.GetAsVector().DotProduct(normal);
            minAxis = Math.Min(minAxis, axisValue);
            maxAxis = Math.Max(maxAxis, axisValue);
            minNormal = Math.Min(minNormal, normalValue);
            maxNormal = Math.Max(maxNormal, normalValue);
        }

        if (!IsFiniteValue(minAxis) ||
            !IsFiniteValue(maxAxis) ||
            !IsFiniteValue(minNormal) ||
            !IsFiniteValue(maxNormal))
        {
            return false;
        }

        intervalStart = minAxis;
        intervalEnd = maxAxis;
        normalCenter = (minNormal + maxNormal) * 0.5;
        halfHeight = Math.Max((maxNormal - minNormal) * 0.5, PointTolerance);
        textHeight = Math.Max(maxNormal - minNormal, PointTolerance);
        textCenterPoint = new Point3d(
            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
            (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
        return true;
    }

    private static bool TryGetDimensionTextExtents(Dimension dimension, out Extents3d extents)
    {
        extents = default;

        try
        {
            var hasText = false;
            using var explodedObjects = new DBObjectCollection();
            dimension.Explode(explodedObjects);
            foreach (DBObject dbObject in explodedObjects)
            {
                try
                {
                    if (dbObject is not Entity entity)
                        continue;

                    // 避让范围只取尺寸文字，避免把尺寸线、界线或标注脚当成文字障碍。
                    if (entity is not DBText && entity is not MText)
                        continue;

                    if (!TryGetEntityExtents(entity, out var currentExtents))
                        continue;

                    if (!hasText)
                    {
                        extents = currentExtents;
                        hasText = true;
                        continue;
                    }

                    extents.AddExtents(currentExtents);
                }
                finally
                {
                    dbObject.Dispose();
                }
            }

            return hasText;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (AcadRuntimeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetEntityExtents(Entity entity, out Extents3d extents)
    {
        extents = default;

        try
        {
            extents = entity.GeometricExtents;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (AcadRuntimeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static double GetRequiredHorizontalGap(DimensionRowItem left, DimensionRowItem right) =>
        Math.Max(left.HorizontalClearance, right.HorizontalClearance);

    private static double GetRequiredHorizontalGap(double horizontalClearance, OccupiedInterval interval) =>
        Math.Max(horizontalClearance, interval.HorizontalClearance);

    private static double ResolveHorizontalClearance(Dimension dimension, double textHeight, double horizontalPadding)
    {
        var gapByPadding = horizontalPadding * HorizontalPlacementGapFactor;
        var gapByTextHeight = textHeight * MinimumTextGapByHeightFactor;
        var gapByDimGap = dimension.Dimgap * MinimumTextGapByDimGapFactor;
        return Math.Max(gapByPadding, Math.Max(gapByTextHeight, gapByDimGap));
    }

    private static double Dot2d(Vector3d left, Vector3d right) => (left.X * right.X) + (left.Y * right.Y);

    private static bool IsFiniteValue(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private readonly struct LineAnchorBuildResult
    {
        private LineAnchorBuildResult(bool success, Point3d lineAnchor, string error)
        {
            Success = success;
            LineAnchor = lineAnchor;
            Error = error;
        }

        internal bool Success { get; }

        internal Point3d LineAnchor { get; }

        internal string Error { get; }

        internal static LineAnchorBuildResult CreateSuccess(Point3d lineAnchor) => new(true, lineAnchor, string.Empty);

        internal static LineAnchorBuildResult Failure(string error) => new(false, default, error);
    }

    private readonly struct DimensionPlacementContext
    {
        internal DimensionPlacementContext(
            Dimension dimension,
            ObjectId ownerId,
            Vector3d axis,
            Vector3d referenceNormal,
            Point3d firstPoint,
            Point3d secondPoint,
            Point3d midPoint,
            Point3d lineAnchor,
            double rowTolerance)
        {
            Dimension = dimension;
            OwnerId = ownerId;
            Axis = axis;
            ReferenceNormal = referenceNormal;
            FirstPoint = firstPoint;
            SecondPoint = secondPoint;
            MidPoint = midPoint;
            LineAnchor = lineAnchor;
            RowTolerance = rowTolerance;
        }

        internal Dimension Dimension { get; }

        internal ObjectId OwnerId { get; }

        internal Vector3d Axis { get; }

        internal Vector3d ReferenceNormal { get; }

        internal Point3d FirstPoint { get; }

        internal Point3d SecondPoint { get; }

        internal Point3d MidPoint { get; }

        internal Point3d LineAnchor { get; }

        internal double RowTolerance { get; }
    }

    private readonly struct TextLayoutSnapshot
    {
        internal TextLayoutSnapshot(
            bool usesDefaultTextPosition,
            Point3d textPosition,
            double intervalStart,
            double intervalEnd,
            double center,
            double verticalCenter,
            double halfHeight,
            int laneIndex,
            double laneSpacing,
            Vector3d outwardNormal,
            double horizontalClearance)
        {
            UsesDefaultTextPosition = usesDefaultTextPosition;
            TextPosition = textPosition;
            IntervalStart = intervalStart;
            IntervalEnd = intervalEnd;
            Center = center;
            VerticalCenter = verticalCenter;
            HalfHeight = halfHeight;
            LaneIndex = laneIndex;
            LaneSpacing = laneSpacing;
            OutwardNormal = outwardNormal;
            HorizontalClearance = horizontalClearance;
        }

        internal bool UsesDefaultTextPosition { get; }

        internal Point3d TextPosition { get; }

        internal double IntervalStart { get; }

        internal double IntervalEnd { get; }

        internal double Center { get; }

        internal double VerticalCenter { get; }

        internal double HalfHeight { get; }

        internal int LaneIndex { get; }

        internal double LaneSpacing { get; }

        internal Vector3d OutwardNormal { get; }

        internal double HorizontalClearance { get; }
    }

    private sealed class DimensionRowGroup
    {
        internal DimensionRowGroup(
            ObjectId ownerId,
            Vector3d axis,
            Vector3d referenceNormal,
            Point3d lineAnchor,
            double rowTolerance)
        {
            OwnerId = ownerId;
            Axis = axis;
            ReferenceNormal = referenceNormal;
            LineAnchor = lineAnchor;
            RowTolerance = rowTolerance;
            Items = new List<DimensionRowItem>();
        }

        internal ObjectId OwnerId { get; }

        internal Vector3d Axis { get; }

        internal Vector3d ReferenceNormal { get; }

        internal Point3d LineAnchor { get; }

        internal double RowTolerance { get; private set; }

        internal List<DimensionRowItem> Items { get; }

        internal void Add(DimensionRowItem item, double rowTolerance)
        {
            Items.Add(item);
            RowTolerance = Math.Max(RowTolerance, rowTolerance);
        }
    }

    private sealed class DimensionRowItem
    {
        internal DimensionRowItem(
            Dimension dimension,
            Point3d lineAnchor,
            Point3d defaultTextPosition,
            double intervalStart,
            double intervalEnd,
            double center,
            double verticalCenter,
            double halfHeight,
            int currentLaneIndex,
            double laneSpacing,
            Vector3d outwardNormal,
            double horizontalClearance,
            bool usesDefaultTextPosition = true,
            Point3d currentTextPosition = default)
        {
            Dimension = dimension;
            LineAnchor = lineAnchor;
            DefaultTextPosition = defaultTextPosition;
            IntervalStart = intervalStart;
            IntervalEnd = intervalEnd;
            Center = center;
            VerticalCenter = verticalCenter;
            HalfHeight = halfHeight;
            CurrentLaneIndex = currentLaneIndex;
            LaneSpacing = laneSpacing;
            OutwardNormal = outwardNormal;
            HorizontalClearance = horizontalClearance;
            UsesDefaultTextPosition = usesDefaultTextPosition;
            CurrentTextPosition = currentTextPosition;
        }

        internal Dimension Dimension { get; }

        internal Point3d LineAnchor { get; }

        internal Point3d DefaultTextPosition { get; }

        internal double IntervalStart { get; }

        internal double IntervalEnd { get; }

        internal double Center { get; }

        internal double VerticalCenter { get; }

        internal double HalfHeight { get; }

        internal int CurrentLaneIndex { get; }

        internal double LaneSpacing { get; }

        internal Vector3d OutwardNormal { get; }

        internal double HorizontalClearance { get; }

        internal bool UsesDefaultTextPosition { get; }

        internal Point3d CurrentTextPosition { get; }
    }

    private readonly struct ReflowedInterval
    {
        internal ReflowedInterval(double start, double end)
        {
            Start = Math.Min(start, end);
            End = Math.Max(start, end);
        }

        internal double Start { get; }

        internal double End { get; }
    }

    private readonly struct OccupiedInterval
    {
        internal OccupiedInterval(double start, double end, double horizontalClearance)
        {
            Start = start;
            End = end;
            HorizontalClearance = horizontalClearance;
        }

        internal double Start { get; }

        internal double End { get; }

        internal double HorizontalClearance { get; }
    }
}
