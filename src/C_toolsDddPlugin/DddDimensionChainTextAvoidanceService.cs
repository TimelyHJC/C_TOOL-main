using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using C_toolsShared;

namespace C_toolsDddPlugin;

internal static class DddDimensionChainTextAvoidanceService
{
    private const double PointTolerance = 1e-6;
    private const double PositionComparisonTolerance = 1e-4;
    private const double ParallelDotTolerance = 0.999;
    private const double EstimatedCharWidthFactor = 0.82;
    private const double LaneGapByTextHeightFactor = 0.35;
    private const double LaneGapByDimGapFactor = 2.0;
    private const double PlacementGapByTextHeightFactor = 0.08;
    private const double PlacementGapByDimGapFactor = 0.25;
    private const double AxisShiftLimitByLaneSpacingFactor = 1.5;
    private const double CircleAllowanceByTextHeightFactor = 1.5;
    private const int MaxLaneIndex = 2;

    internal static bool TryApplyAlignedChain(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        double signedOffset,
        out string error)
    {
        return TryApplyRows(
            doc,
            dimensionIds,
            DddPluginCommandIds.DimAligned,
            preferCurrentPlacement: false,
            out _,
            out _,
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
        return TryApplyRows(
            doc,
            dimensionIds,
            DddPluginCommandIds.DimLinear,
            preferCurrentPlacement: false,
            out _,
            out _,
            out error);
    }

    internal static bool TryApplySameRow(
        Document doc,
        ObjectId seedId,
        string commandTag,
        out int rowCount,
        out int adjustedCount,
        out string error)
    {
        return TryApplyRows(
            doc,
            new List<ObjectId> { seedId },
            commandTag,
            preferCurrentPlacement: false,
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
        return TryApplyRows(
            doc,
            dimensionIds,
            commandTag,
            preferCurrentPlacement: false,
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
        return TryApplyRows(
            doc,
            dimensionIds,
            commandTag,
            preferCurrentPlacement,
            out rowCount,
            out adjustedCount,
            out error);
    }

    private static bool TryApplyRows(
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
                if (!TryBuildExpandedRows(tr, dimensionIds, out var rowGroups, out error))
                    return false;

                foreach (var rowGroup in rowGroups)
                {
                    rowCount += rowGroup.Items.Count;
                    adjustedCount += ApplyRowGroup(commandTag, rowGroup, preferCurrentPlacement);
                }

                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} text avoidance failed (invalid operation)", ex);
            error = ex.Message;
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} text avoidance failed (CAD)", ex);
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandTag} text avoidance failed (argument)", ex);
            error = ex.Message;
            return false;
        }
    }

    private static bool TryBuildExpandedRows(
        Transaction tr,
        IList<ObjectId> seedIds,
        out List<DimensionRowGroup> rowGroups,
        out string error)
    {
        rowGroups = new List<DimensionRowGroup>();
        error = string.Empty;
        var validSeedIds = new HashSet<ObjectId>();

        foreach (var seedId in seedIds)
        {
            if (seedId.IsNull || !seedId.IsValid || seedId.IsErased || !validSeedIds.Add(seedId))
                continue;

            var dbObject = tr.GetObject(seedId, OpenMode.ForRead, false);
            if (dbObject is not Dimension dimension)
                continue;

            if (!TryBuildPlacementContext(dimension, out var context, out error))
                return false;

            if (FindMatchingRow(rowGroups, context) != null)
                continue;

            rowGroups.Add(new DimensionRowGroup(context));
        }

        foreach (var rowGroup in rowGroups)
        {
            if (!TryPopulateRow(tr, rowGroup, validSeedIds, out error))
                return false;
        }

        return true;
    }

    private static bool TryPopulateRow(
        Transaction tr,
        DimensionRowGroup rowGroup,
        HashSet<ObjectId> seedIds,
        out string error)
    {
        error = string.Empty;
        var ownerRecord = (BlockTableRecord)tr.GetObject(rowGroup.OwnerId, OpenMode.ForRead);
        foreach (ObjectId entityId in ownerRecord)
        {
            if (entityId.IsNull || !entityId.IsValid || entityId.IsErased)
                continue;

            var dbObject = tr.GetObject(entityId, OpenMode.ForWrite, false);
            if (dbObject is not Dimension dimension)
                continue;

            if (!TryBuildPlacementContext(dimension, out var context, out _))
                continue;
            if (!CanShareRow(rowGroup, context))
                continue;

            context = context.WithRowAxes(rowGroup.Axis, rowGroup.ReferenceNormal);
            if (!TryBuildRowItem(
                    context,
                    rowGroup.OutwardNormal,
                    seedIds.Contains(entityId),
                    out var item,
                    out error))
            {
                return false;
            }

            rowGroup.Add(item, context.RowTolerance);
        }

        return true;
    }

    private static DimensionRowGroup? FindMatchingRow(
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
        if (context.OwnerId != rowGroup.OwnerId || context.DimensionKind != rowGroup.DimensionKind)
            return false;
        if (!AreParallel(rowGroup.Axis, context.Axis))
            return false;

        var rowDistance = Math.Abs(Dot2d(context.LineAnchor - rowGroup.LineAnchor, rowGroup.ReferenceNormal));
        return rowDistance <= Math.Max(rowGroup.RowTolerance, context.RowTolerance);
    }

    private static bool TryBuildRowItem(
        DimensionPlacementContext context,
        Vector3d outwardNormal,
        bool isSeed,
        out DimensionRowItem item,
        out string error)
    {
        item = null!;
        error = string.Empty;

        try
        {
            context.Dimension.RecomputeDimensionBlock(true);
            var textPosition = GetEffectiveTextPosition(context.Dimension, context.LineAnchor);
            if (!TryBuildProjectedTextBounds(
                    context.Dimension,
                    textPosition,
                    context.Axis,
                    outwardNormal,
                    out var bounds))
            {
                error = "无法读取标注文字范围。";
                return false;
            }

            var textHeight = Math.Max(bounds.NormalSpan, Math.Max(context.Dimension.Dimtxt, PointTolerance));
            var laneGap = Math.Max(
                context.Dimension.Dimgap * LaneGapByDimGapFactor,
                textHeight * LaneGapByTextHeightFactor);
            var laneSpacing = Math.Max(textHeight + laneGap, PointTolerance);
            var placementGap = Math.Max(
                context.Dimension.Dimgap * PlacementGapByDimGapFactor,
                textHeight * PlacementGapByTextHeightFactor);
            var circleCenterAxis = context.LineAnchor.GetAsVector().DotProduct(context.Axis);
            var lineNormal = context.LineAnchor.GetAsVector().DotProduct(outwardNormal);
            var originalRadius = Math.Sqrt(bounds.MaxCornerDistanceSquared(circleCenterAxis, lineNormal));
            var maxRadius = originalRadius + (textHeight * CircleAllowanceByTextHeightFactor);

            item = new DimensionRowItem(
                context.Dimension,
                textPosition,
                bounds,
                laneSpacing,
                placementGap,
                circleCenterAxis,
                lineNormal,
                maxRadius,
                TryReadUsingDefaultTextPosition(context.Dimension),
                isSeed);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            C_toolsDiagnostics.LogNonFatal("构建标注文字避让项失败（无效操作）", ex);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            error = ex.Message;
            C_toolsDiagnostics.LogNonFatal("构建标注文字避让项失败（CAD）", ex);
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            C_toolsDiagnostics.LogNonFatal("构建标注文字避让项失败（参数）", ex);
            return false;
        }
    }

    private static int ApplyRowGroup(
        string commandTag,
        DimensionRowGroup rowGroup,
        bool preferCurrentPlacement)
    {
        rowGroup.Items.Sort(static (left, right) =>
        {
            var startCompare = left.Bounds.AxisStart.CompareTo(right.Bounds.AxisStart);
            if (startCompare != 0)
                return startCompare;
            return left.Bounds.AxisEnd.CompareTo(right.Bounds.AxisEnd);
        });

        var laneSpacing = ResolveRowLaneSpacing(rowGroup.Items);
        var placementGap = ResolveRowPlacementGap(rowGroup.Items);
        var maxAxisShift = laneSpacing * AxisShiftLimitByLaneSpacingFactor;
        var planItems = new List<DddDimensionTextAvoidancePlanner.PlanItem>(rowGroup.Items.Count);
        foreach (var item in rowGroup.Items)
        {
            var preferAnchor = !item.UsesDefaultTextPosition ||
                               (preferCurrentPlacement && item.IsSeed);
            planItems.Add(new DddDimensionTextAvoidancePlanner.PlanItem(
                item.Bounds,
                preferAnchor,
                item.CircleCenterAxis,
                item.LineNormal,
                item.MaxRadius));
        }

        var plan = DddDimensionTextAvoidancePlanner.Plan(
            planItems,
            laneSpacing,
            MaxLaneIndex,
            maxAxisShift,
            placementGap);
        LogAvoidanceSnapshot(commandTag, "before", rowGroup, plan.ConflictFlags);
        if (!plan.HasConflicts)
        {
            LogAvoidanceSnapshot(commandTag, "after", rowGroup, plan.ConflictFlags);
            return 0;
        }

        var adjustedCount = 0;
        for (var index = 0; index < rowGroup.Items.Count; index++)
        {
            var item = rowGroup.Items[index];
            var axisOffset = plan.AxisOffsets[index];
            var normalOffset = plan.NormalOffsets[index];
            var laneOffset = plan.LaneOffsets[index];
            if (Math.Abs(axisOffset) <= PointTolerance && Math.Abs(normalOffset) <= PointTolerance)
            {
                LogPlacementDecision(commandTag, item, axisOffset, normalOffset, laneOffset, item.CurrentTextPosition);
                continue;
            }

            var target = item.CurrentTextPosition +
                         (rowGroup.Axis * axisOffset) +
                         (rowGroup.OutwardNormal * normalOffset);
            if (ApplyCustomTargetIfNeeded(item, target))
                adjustedCount++;
            LogPlacementDecision(commandTag, item, axisOffset, normalOffset, laneOffset, target);
        }

        LogAvoidanceSnapshot(commandTag, "after", rowGroup, plan.ConflictFlags);
        return adjustedCount;
    }

    private static double ResolveRowLaneSpacing(List<DimensionRowItem> items)
    {
        var laneSpacing = PointTolerance;
        foreach (var item in items)
            laneSpacing = Math.Max(laneSpacing, item.LaneSpacing);
        return laneSpacing;
    }

    private static double ResolveRowPlacementGap(List<DimensionRowItem> items)
    {
        var placementGap = 0.0;
        foreach (var item in items)
            placementGap = Math.Max(placementGap, item.PlacementGap);
        return placementGap;
    }

    private static bool ApplyCustomTargetIfNeeded(DimensionRowItem item, Point3d target)
    {
        if (item.CurrentTextPosition.DistanceTo(target) <= PositionComparisonTolerance)
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
                DimensionKind.Linear,
                rotated,
                rotated.XLine1Point,
                rotated.XLine2Point,
                rotated.DimLinePoint,
                new Vector3d(Math.Cos(rotated.Rotation), Math.Sin(rotated.Rotation), 0.0),
                out context,
                out error),
            AlignedDimension aligned => TryBuildPlacementContext(
                DimensionKind.Aligned,
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
        DimensionKind dimensionKind,
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
            dimensionKind,
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

    private static bool TryBuildProjectedTextBounds(
        Dimension dimension,
        Point3d textPosition,
        Vector3d axis,
        Vector3d normal,
        out DddDimensionTextAvoidancePlanner.ProjectedTextBounds bounds)
    {
        bounds = default;
        if (TryGetDimensionTextExtents(dimension, out var extents) &&
            TryProjectExtents(extents, axis, normal, out bounds))
        {
            return true;
        }

        var textWidth = EstimateTextWidth(dimension);
        var textHeight = Math.Max(dimension.Dimtxt, PointTolerance);
        var axisCenter = textPosition.GetAsVector().DotProduct(axis);
        var normalCenter = textPosition.GetAsVector().DotProduct(normal);
        bounds = new DddDimensionTextAvoidancePlanner.ProjectedTextBounds(
            axisCenter - (textWidth * 0.5),
            axisCenter + (textWidth * 0.5),
            normalCenter - (textHeight * 0.5),
            normalCenter + (textHeight * 0.5));
        return true;
    }

    private static bool TryProjectExtents(
        Extents3d extents,
        Vector3d axis,
        Vector3d normal,
        out DddDimensionTextAvoidancePlanner.ProjectedTextBounds bounds)
    {
        bounds = default;
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

        bounds = new DddDimensionTextAvoidancePlanner.ProjectedTextBounds(
            minAxis,
            maxAxis,
            minNormal,
            maxNormal);
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
                    if (dbObject is not Entity entity || (entity is not DBText && entity is not MText))
                        continue;
                    if (!TryGetEntityExtents(entity, out var currentExtents))
                        continue;

                    if (!hasText)
                    {
                        extents = currentExtents;
                        hasText = true;
                    }
                    else
                    {
                        extents.AddExtents(currentExtents);
                    }
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
        return Math.Max(dimension.Dimtxt, Math.Max(text.Length, 1) * dimension.Dimtxt * EstimatedCharWidthFactor);
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
            if (IsFiniteValue(textPosition.X) && IsFiniteValue(textPosition.Y) && IsFiniteValue(textPosition.Z))
                return textPosition;
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

    private static void LogAvoidanceSnapshot(
        string commandTag,
        string stage,
        DimensionRowGroup rowGroup,
        bool[] conflictFlags)
    {
        C_toolsDiagnostics.LogNonFatal(
            $"{commandTag} 调试 {stage} rowCount={rowGroup.Items.Count} " +
            $"axis={FormatDebugVector(rowGroup.Axis)} outward={FormatDebugVector(rowGroup.OutwardNormal)}");

        for (var index = 0; index < rowGroup.Items.Count; index++)
        {
            var item = rowGroup.Items[index];
            var conflict = index < conflictFlags.Length && conflictFlags[index] ? "1" : "0";
            C_toolsDiagnostics.LogNonFatal(
                $"{commandTag} 调试 {stage} idx={index} conflict={conflict} " +
                $"bounds=({item.Bounds.AxisStart:0.###},{item.Bounds.AxisEnd:0.###};" +
                $"{item.Bounds.NormalStart:0.###},{item.Bounds.NormalEnd:0.###}) " +
                $"text={FormatDebugPoint(item.CurrentTextPosition)} state={DescribeDimensionState(item.Dimension)}");
        }
    }

    private static void LogPlacementDecision(
        string commandTag,
        DimensionRowItem item,
        double axisOffset,
        double normalOffset,
        int laneOffset,
        Point3d target)
    {
        C_toolsDiagnostics.LogNonFatal(
            $"{commandTag} 调试 placement axisShift={axisOffset:0.###} " +
            $"normalShift={normalOffset:0.###} lane={laneOffset} " +
            $"targetText={FormatDebugPoint(target)} state={DescribeDimensionState(item.Dimension)}");
    }

    private static string DescribeDimensionState(Dimension dimension) =>
        $"id={TryGetObjectIdText(dimension)} type={dimension.GetType().Name} " +
        $"UsingDefaultTextPosition={TryGetUsingDefaultTextPositionText(dimension)} " +
        $"TextPosition={TryGetTextPositionText(dimension)} DimLinePoint={TryGetDimLinePointText(dimension)}";

    private static string TryGetObjectIdText(DBObject dbObject)
    {
        try
        {
            return dbObject.ObjectId.IsValid ? dbObject.ObjectId.Handle.ToString() : "null";
        }
        catch (InvalidOperationException)
        {
            return "error";
        }
        catch (AcadRuntimeException)
        {
            return "error";
        }
        catch (ArgumentException)
        {
            return "error";
        }
    }

    private static string TryGetUsingDefaultTextPositionText(Dimension dimension)
    {
        try
        {
            return dimension.UsingDefaultTextPosition ? "true" : "false";
        }
        catch (InvalidOperationException)
        {
            return "error";
        }
        catch (AcadRuntimeException)
        {
            return "error";
        }
        catch (ArgumentException)
        {
            return "error";
        }
    }

    private static string TryGetTextPositionText(Dimension dimension)
    {
        try
        {
            return FormatDebugPoint(dimension.TextPosition);
        }
        catch (InvalidOperationException)
        {
            return "error";
        }
        catch (AcadRuntimeException)
        {
            return "error";
        }
        catch (ArgumentException)
        {
            return "error";
        }
    }

    private static string TryGetDimLinePointText(Dimension dimension)
    {
        try
        {
            return dimension switch
            {
                RotatedDimension rotated => FormatDebugPoint(rotated.DimLinePoint),
                AlignedDimension aligned => FormatDebugPoint(aligned.DimLinePoint),
                _ => "n/a"
            };
        }
        catch (InvalidOperationException)
        {
            return "error";
        }
        catch (AcadRuntimeException)
        {
            return "error";
        }
        catch (ArgumentException)
        {
            return "error";
        }
    }

    private static string FormatDebugPoint(Point3d point) =>
        $"({point.X:0.###},{point.Y:0.###},{point.Z:0.###})";

    private static string FormatDebugVector(Vector3d vector) =>
        $"({vector.X:0.###},{vector.Y:0.###},{vector.Z:0.###})";

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

    private static Vector3d ResolveOutwardNormal(
        Vector3d referenceNormal,
        Point3d midPoint,
        Point3d lineAnchor)
    {
        var lineOffset = Dot2d(lineAnchor - midPoint, referenceNormal);
        if (Math.Abs(lineOffset) > PointTolerance)
            return lineOffset >= 0.0 ? referenceNormal : -referenceNormal;
        return referenceNormal;
    }

    private static Point3d ProjectPointOntoLine(Point3d point, Point3d linePoint, Vector3d axis)
    {
        var offset = point - linePoint;
        return linePoint + (axis * offset.DotProduct(axis));
    }

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static double Dot2d(Vector3d left, Vector3d right) =>
        (left.X * right.X) + (left.Y * right.Y);

    private static bool IsFiniteValue(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private enum DimensionKind
    {
        Linear,
        Aligned
    }

    private readonly struct DimensionPlacementContext
    {
        internal DimensionPlacementContext(
            DimensionKind dimensionKind,
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
            DimensionKind = dimensionKind;
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

        internal DimensionKind DimensionKind { get; }

        internal Dimension Dimension { get; }

        internal ObjectId OwnerId { get; }

        internal Vector3d Axis { get; }

        internal Vector3d ReferenceNormal { get; }

        internal Point3d FirstPoint { get; }

        internal Point3d SecondPoint { get; }

        internal Point3d MidPoint { get; }

        internal Point3d LineAnchor { get; }

        internal double RowTolerance { get; }

        internal DimensionPlacementContext WithRowAxes(Vector3d axis, Vector3d referenceNormal) =>
            new(
                DimensionKind,
                Dimension,
                OwnerId,
                axis,
                referenceNormal,
                FirstPoint,
                SecondPoint,
                MidPoint,
                LineAnchor,
                RowTolerance);
    }

    private sealed class DimensionRowGroup
    {
        private readonly HashSet<ObjectId> _itemIds = new();

        internal DimensionRowGroup(DimensionPlacementContext seed)
        {
            DimensionKind = seed.DimensionKind;
            OwnerId = seed.OwnerId;
            Axis = seed.Axis;
            ReferenceNormal = seed.ReferenceNormal;
            OutwardNormal = ResolveOutwardNormal(seed.ReferenceNormal, seed.MidPoint, seed.LineAnchor);
            LineAnchor = seed.LineAnchor;
            RowTolerance = seed.RowTolerance;
            Items = new List<DimensionRowItem>();
        }

        internal DimensionKind DimensionKind { get; }

        internal ObjectId OwnerId { get; }

        internal Vector3d Axis { get; }

        internal Vector3d ReferenceNormal { get; }

        internal Vector3d OutwardNormal { get; }

        internal Point3d LineAnchor { get; }

        internal double RowTolerance { get; private set; }

        internal List<DimensionRowItem> Items { get; }

        internal void Add(DimensionRowItem item, double rowTolerance)
        {
            if (!_itemIds.Add(item.Dimension.ObjectId))
                return;

            Items.Add(item);
            RowTolerance = Math.Max(RowTolerance, rowTolerance);
        }
    }

    private sealed class DimensionRowItem
    {
        internal DimensionRowItem(
            Dimension dimension,
            Point3d currentTextPosition,
            DddDimensionTextAvoidancePlanner.ProjectedTextBounds bounds,
            double laneSpacing,
            double placementGap,
            double circleCenterAxis,
            double lineNormal,
            double maxRadius,
            bool usesDefaultTextPosition,
            bool isSeed)
        {
            Dimension = dimension;
            CurrentTextPosition = currentTextPosition;
            Bounds = bounds;
            LaneSpacing = laneSpacing;
            PlacementGap = placementGap;
            CircleCenterAxis = circleCenterAxis;
            LineNormal = lineNormal;
            MaxRadius = maxRadius;
            UsesDefaultTextPosition = usesDefaultTextPosition;
            IsSeed = isSeed;
        }

        internal Dimension Dimension { get; }

        internal Point3d CurrentTextPosition { get; }

        internal DddDimensionTextAvoidancePlanner.ProjectedTextBounds Bounds { get; }

        internal double LaneSpacing { get; }

        internal double PlacementGap { get; }

        internal double CircleCenterAxis { get; }

        internal double LineNormal { get; }

        internal double MaxRadius { get; }

        internal bool UsesDefaultTextPosition { get; }

        internal bool IsSeed { get; }
    }
}
