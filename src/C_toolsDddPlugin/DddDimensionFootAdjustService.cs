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

internal static class DddDimensionFootAdjustService
{
    private const double PointTolerance = 1e-6;

    internal static void Run(Document doc)
    {
        var ed = doc.Editor;
        if (!TryResolveAdjustmentPlan(doc, out var plan, out var error, out var canceled))
        {
            if (!canceled && !string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        while (true)
        {
            var pointOptions = new PromptPointOptions("\nC_TOOL：点取新的标注脚位置：")
            {
                BasePoint = plan.CurrentPoint,
                UseBasePoint = true,
                AllowNone = true
            };

            var pointResult = ed.GetPoint(pointOptions);
            if (pointResult.Status != PromptStatus.OK)
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_DDE_Cancelled}");
                return;
            }

            if (!TryBuildMovedPoint(plan, pointResult.Value, out var movedPoint, out error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                continue;
            }

            if (PointsCoincide(movedPoint, plan.CurrentPoint))
            {
                ed.WriteMessage("\nC_TOOL：位置重合，已忽略。");
                continue;
            }

            if (!TryApplyAdjustment(doc, plan, movedPoint, out var summary, out error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                continue;
            }

            ed.WriteMessage(
                $"\nC_TOOL：F_DDE 已将连续尺寸 {FormatMeasurement(summary.BeforeLeft)}+{FormatMeasurement(summary.BeforeRight)} 调整为 {FormatMeasurement(summary.AfterLeft)}+{FormatMeasurement(summary.AfterRight)}。");
            return;
        }
    }

    private static bool TryResolveAdjustmentPlan(
        Document doc,
        out AdjustmentPlan plan,
        out string error,
        out bool canceled)
    {
        plan = default;
        error = string.Empty;
        canceled = false;

        var ed = doc.Editor;
        while (true)
        {
            var entityOptions = new PromptEntityOptions("\nC_TOOL：选择要调整的连续标注脚（请点共享标注脚所在尺寸）：")
            {
                AllowNone = true
            };
            entityOptions.SetRejectMessage("\nC_TOOL：只能选择线性标注或对齐标注。");
            entityOptions.AddAllowedClass(typeof(RotatedDimension), true);
            entityOptions.AddAllowedClass(typeof(AlignedDimension), true);

            var entityResult = ed.GetEntity(entityOptions);
            if (entityResult.Status != PromptStatus.OK)
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_DDE_Cancelled}");
                canceled = true;
                return false;
            }

            if (TryCreatePlan(doc, entityResult.ObjectId, entityResult.PickedPoint, out plan, out error))
                return true;

            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
        }
    }

    private static bool TryCreatePlan(
        Document doc,
        ObjectId dimensionId,
        Point3d pickedPoint,
        out AdjustmentPlan plan,
        out string error)
    {
        plan = default;
        error = string.Empty;

        if (dimensionId.ObjectClass == null)
        {
            error = "所选标注无效，请重新选择。";
            return false;
        }

        var alignedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(AlignedDimension));
        if (dimensionId.ObjectClass.IsDerivedFrom(alignedClass))
            return TryCreateAlignedPlan(doc, dimensionId, pickedPoint, out plan, out error);

        return TryCreateLinearPlan(doc, dimensionId, pickedPoint, out plan, out error);
    }

    private static bool TryCreateLinearPlan(
        Document doc,
        ObjectId dimensionId,
        Point3d pickedPoint,
        out AdjustmentPlan plan,
        out string error)
    {
        plan = default;
        error = string.Empty;

        if (!DddLinearDimensionService.TryReadSelectedDimensionContinuationState(
                doc,
                dimensionId,
                out var points,
                out var dimensionIds,
                out var selectedFirstPoint,
                out var selectedSecondPoint,
                out var orientation,
                out var dimLineOrdinate,
                out error))
        {
            return false;
        }

        if (points.Count < 3 || dimensionIds.Count < 2)
        {
            error = "所选标注不在连续标注链中，无法快调标注脚。";
            return false;
        }

        var sharedPoint = ResolveClickedEndpoint(pickedPoint, selectedFirstPoint, selectedSecondPoint);
        if (!TryBuildSharedPointPlan(points, dimensionIds, sharedPoint, out var sharedPointIndex, out error))
            return false;

        plan = new AdjustmentPlan(
            AdjustmentKind.Linear,
            points,
            dimensionIds,
            sharedPointIndex,
            orientation,
            dimLineOrdinate,
            0.0);
        return true;
    }

    private static bool TryCreateAlignedPlan(
        Document doc,
        ObjectId dimensionId,
        Point3d pickedPoint,
        out AdjustmentPlan plan,
        out string error)
    {
        plan = default;
        error = string.Empty;

        if (!DddAlignedDimensionService.TryReadSelectedDimensionContinuationState(
                doc,
                dimensionId,
                out var points,
                out var dimensionIds,
                out var selectedFirstPoint,
                out var selectedSecondPoint,
                out var signedOffset,
                out error))
        {
            return false;
        }

        if (points.Count < 3 || dimensionIds.Count < 2)
        {
            error = "所选标注不在连续标注链中，无法快调标注脚。";
            return false;
        }

        var sharedPoint = ResolveClickedEndpoint(pickedPoint, selectedFirstPoint, selectedSecondPoint);
        if (!TryBuildSharedPointPlan(points, dimensionIds, sharedPoint, out var sharedPointIndex, out error))
            return false;

        plan = new AdjustmentPlan(
            AdjustmentKind.Aligned,
            points,
            dimensionIds,
            sharedPointIndex,
            default,
            0.0,
            signedOffset);
        return true;
    }

    private static bool TryBuildSharedPointPlan(
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        Point3d sharedPoint,
        out int sharedPointIndex,
        out string error)
    {
        sharedPointIndex = FindPointIndex(points, sharedPoint);
        error = string.Empty;

        if (sharedPointIndex < 0)
        {
            error = "未能定位所点击的标注脚，请重新选择。";
            return false;
        }

        if (sharedPointIndex == 0 || sharedPointIndex >= points.Count - 1)
        {
            error = "请点击两段连续标注共用的中间标注脚。";
            return false;
        }

        if (sharedPointIndex > dimensionIds.Count - 1)
        {
            error = "当前连续标注链数据异常，请重新选择。";
            return false;
        }

        return true;
    }

    private static bool TryBuildMovedPoint(
        AdjustmentPlan plan,
        Point3d pickedPoint,
        out Point3d movedPoint,
        out string error)
    {
        error = string.Empty;
        switch (plan.Kind)
        {
            case AdjustmentKind.Linear:
                return TryBuildMovedLinearPoint(plan, pickedPoint, out movedPoint, out error);
            case AdjustmentKind.Aligned:
                return TryBuildMovedAlignedPoint(plan, pickedPoint, out movedPoint, out error);
            default:
                movedPoint = default;
                error = "当前标注类型暂不支持 F_DDE。";
                return false;
        }
    }

    private static bool TryBuildMovedLinearPoint(
        AdjustmentPlan plan,
        Point3d pickedPoint,
        out Point3d movedPoint,
        out string error)
    {
        movedPoint = default;
        error = string.Empty;

        var leftPoint = plan.LeftPoint;
        var rightPoint = plan.RightPoint;
        if (plan.LinearOrientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal)
        {
            var totalDelta = rightPoint.X - leftPoint.X;
            if (Math.Abs(totalDelta) < PointTolerance)
            {
                error = "当前连续标注方向无效，无法快调标注脚。";
                return false;
            }

            var factor = (pickedPoint.X - leftPoint.X) / totalDelta;
            movedPoint = new Point3d(
                pickedPoint.X,
                leftPoint.Y + ((rightPoint.Y - leftPoint.Y) * factor),
                leftPoint.Z + ((rightPoint.Z - leftPoint.Z) * factor));
        }
        else
        {
            var totalDelta = rightPoint.Y - leftPoint.Y;
            if (Math.Abs(totalDelta) < PointTolerance)
            {
                error = "当前连续标注方向无效，无法快调标注脚。";
                return false;
            }

            var factor = (pickedPoint.Y - leftPoint.Y) / totalDelta;
            movedPoint = new Point3d(
                leftPoint.X + ((rightPoint.X - leftPoint.X) * factor),
                pickedPoint.Y,
                leftPoint.Z + ((rightPoint.Z - leftPoint.Z) * factor));
        }

        if (!IsPointInsideLinearSpan(plan.LinearOrientation, leftPoint, rightPoint, movedPoint))
        {
            error = "新的标注脚位置必须落在左右相邻标注点之间。";
            return false;
        }

        return true;
    }

    private static bool TryBuildMovedAlignedPoint(
        AdjustmentPlan plan,
        Point3d pickedPoint,
        out Point3d movedPoint,
        out string error)
    {
        error = string.Empty;
        if (!TryProjectPointToAxis(plan.LeftPoint, plan.RightPoint - plan.LeftPoint, pickedPoint, out movedPoint, out var factor))
        {
            error = "当前连续标注方向无效，无法快调标注脚。";
            return false;
        }

        if (factor <= PointTolerance || factor >= 1.0 - PointTolerance)
        {
            error = "新的标注脚位置必须落在左右相邻标注点之间。";
            return false;
        }

        return true;
    }

    private static bool TryApplyAdjustment(
        Document doc,
        AdjustmentPlan plan,
        Point3d movedPoint,
        out AdjustmentSummary summary,
        out string error)
    {
        summary = default;
        error = string.Empty;

        return plan.Kind == AdjustmentKind.Linear
            ? TryApplyLinearAdjustment(doc, plan, movedPoint, out summary, out error)
            : TryApplyAlignedAdjustment(doc, plan, movedPoint, out summary, out error);
    }

    private static bool TryApplyLinearAdjustment(
        Document doc,
        AdjustmentPlan plan,
        Point3d movedPoint,
        out AdjustmentSummary summary,
        out string error)
    {
        summary = BuildSummary(plan, movedPoint);
        error = string.Empty;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!TryUpdateLinearDimension(
                        tr,
                        plan.DimensionIds[plan.SharedPointIndex - 1],
                        plan.CurrentPoint,
                        movedPoint,
                        plan.LinearOrientation,
                        plan.DimLineOrdinate,
                        out error))
                {
                    return false;
                }

                if (!TryUpdateLinearDimension(
                        tr,
                        plan.DimensionIds[plan.SharedPointIndex],
                        plan.CurrentPoint,
                        movedPoint,
                        plan.LinearOrientation,
                        plan.DimLineOrdinate,
                        out error))
                {
                    return false;
                }

                tr.Commit();
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDE 调整线性标注脚失败（无效操作）", ex);
            error = $"调整线性标注脚失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDE 调整线性标注脚失败（CAD）", ex);
            error = $"调整线性标注脚失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDE 调整线性标注脚失败（参数）", ex);
            error = $"调整线性标注脚失败：{ex.Message}";
            return false;
        }

        var updatedPoints = new List<Point3d>(plan.Points);
        updatedPoints[plan.SharedPointIndex] = movedPoint;
        if (!DddDimensionChainTextAvoidanceService.TryApplyLinearChain(
                doc,
                updatedPoints,
                plan.DimensionIds,
                plan.LinearOrientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal,
                plan.DimLineOrdinate,
                out error))
        {
            WriteAvoidanceFailure(doc, error);
            error = string.Empty;
        }

        return true;
    }

    private static bool TryApplyAlignedAdjustment(
        Document doc,
        AdjustmentPlan plan,
        Point3d movedPoint,
        out AdjustmentSummary summary,
        out string error)
    {
        summary = BuildSummary(plan, movedPoint);
        error = string.Empty;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!TryUpdateAlignedDimension(
                        tr,
                        plan.DimensionIds[plan.SharedPointIndex - 1],
                        plan.CurrentPoint,
                        movedPoint,
                        plan.SignedOffset,
                        out error))
                {
                    return false;
                }

                if (!TryUpdateAlignedDimension(
                        tr,
                        plan.DimensionIds[plan.SharedPointIndex],
                        plan.CurrentPoint,
                        movedPoint,
                        plan.SignedOffset,
                        out error))
                {
                    return false;
                }

                tr.Commit();
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDE 调整对齐标注脚失败（无效操作）", ex);
            error = $"调整对齐标注脚失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDE 调整对齐标注脚失败（CAD）", ex);
            error = $"调整对齐标注脚失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDE 调整对齐标注脚失败（参数）", ex);
            error = $"调整对齐标注脚失败：{ex.Message}";
            return false;
        }

        var updatedPoints = new List<Point3d>(plan.Points);
        updatedPoints[plan.SharedPointIndex] = movedPoint;
        if (!DddDimensionChainTextAvoidanceService.TryApplyAlignedChain(
                doc,
                updatedPoints,
                plan.DimensionIds,
                plan.SignedOffset,
                out error))
        {
            WriteAvoidanceFailure(doc, error);
            error = string.Empty;
        }

        return true;
    }

    private static bool TryUpdateLinearDimension(
        Transaction tr,
        ObjectId dimensionId,
        Point3d oldSharedPoint,
        Point3d newSharedPoint,
        DddLinearDimensionService.LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        out string error)
    {
        error = string.Empty;

        var dbObject = tr.GetObject(dimensionId, OpenMode.ForWrite, false);
        if (dbObject is not RotatedDimension dimension || dimension.IsErased)
        {
            error = "所选连续标注已失效，请重新执行 F_DDE。";
            return false;
        }

        if (!TryReplaceSharedPoint(dimension, oldSharedPoint, newSharedPoint))
        {
            error = "无法定位要调整的共享标注脚，请重新选择。";
            return false;
        }

        dimension.UsingDefaultTextPosition = true;
        if (!TryBuildLinearDimensionLinePoint(
                dimension.XLine1Point,
                dimension.XLine2Point,
                orientation,
                dimLineOrdinate,
                out var dimLinePoint,
                out error))
        {
            return false;
        }

        dimension.DimLinePoint = dimLinePoint;
        dimension.RecomputeDimensionBlock(true);
        return true;
    }

    private static bool TryUpdateAlignedDimension(
        Transaction tr,
        ObjectId dimensionId,
        Point3d oldSharedPoint,
        Point3d newSharedPoint,
        double signedOffset,
        out string error)
    {
        error = string.Empty;

        var dbObject = tr.GetObject(dimensionId, OpenMode.ForWrite, false);
        if (dbObject is not AlignedDimension dimension || dimension.IsErased)
        {
            error = "所选连续标注已失效，请重新执行 F_DDE。";
            return false;
        }

        if (!TryReplaceSharedPoint(dimension, oldSharedPoint, newSharedPoint))
        {
            error = "无法定位要调整的共享标注脚，请重新选择。";
            return false;
        }

        dimension.UsingDefaultTextPosition = true;
        if (!TryBuildAlignedDimensionLinePoint(
                dimension.XLine1Point,
                dimension.XLine2Point,
                signedOffset,
                out var dimLinePoint,
                out error))
        {
            return false;
        }

        dimension.DimLinePoint = dimLinePoint;
        dimension.RecomputeDimensionBlock(true);
        return true;
    }

    private static bool TryReplaceSharedPoint(RotatedDimension dimension, Point3d oldSharedPoint, Point3d newSharedPoint)
    {
        if (PointsCoincide(dimension.XLine1Point, oldSharedPoint))
        {
            dimension.XLine1Point = newSharedPoint;
            return true;
        }

        if (PointsCoincide(dimension.XLine2Point, oldSharedPoint))
        {
            dimension.XLine2Point = newSharedPoint;
            return true;
        }

        return false;
    }

    private static bool TryReplaceSharedPoint(AlignedDimension dimension, Point3d oldSharedPoint, Point3d newSharedPoint)
    {
        if (PointsCoincide(dimension.XLine1Point, oldSharedPoint))
        {
            dimension.XLine1Point = newSharedPoint;
            return true;
        }

        if (PointsCoincide(dimension.XLine2Point, oldSharedPoint))
        {
            dimension.XLine2Point = newSharedPoint;
            return true;
        }

        return false;
    }

    private static AdjustmentSummary BuildSummary(AdjustmentPlan plan, Point3d movedPoint) =>
        new(
            Measure(plan.Kind, plan.LeftPoint, plan.CurrentPoint, plan.LinearOrientation),
            Measure(plan.Kind, plan.CurrentPoint, plan.RightPoint, plan.LinearOrientation),
            Measure(plan.Kind, plan.LeftPoint, movedPoint, plan.LinearOrientation),
            Measure(plan.Kind, movedPoint, plan.RightPoint, plan.LinearOrientation));

    private static double Measure(
        AdjustmentKind kind,
        Point3d firstPoint,
        Point3d secondPoint,
        DddLinearDimensionService.LinearDimensionOrientation orientation)
    {
        if (kind == AdjustmentKind.Linear)
        {
            return orientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal
                ? Math.Abs(secondPoint.X - firstPoint.X)
                : Math.Abs(secondPoint.Y - firstPoint.Y);
        }

        return firstPoint.DistanceTo(secondPoint);
    }

    private static Point3d ResolveClickedEndpoint(Point3d pickedPoint, Point3d firstPoint, Point3d secondPoint) =>
        pickedPoint.DistanceTo(firstPoint) <= pickedPoint.DistanceTo(secondPoint) ? firstPoint : secondPoint;

    private static bool IsPointInsideLinearSpan(
        DddLinearDimensionService.LinearDimensionOrientation orientation,
        Point3d leftPoint,
        Point3d rightPoint,
        Point3d movedPoint)
    {
        var left = orientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal ? leftPoint.X : leftPoint.Y;
        var right = orientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal ? rightPoint.X : rightPoint.Y;
        var moved = orientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal ? movedPoint.X : movedPoint.Y;
        return moved > Math.Min(left, right) + PointTolerance &&
               moved < Math.Max(left, right) - PointTolerance;
    }

    private static bool TryProjectPointToAxis(
        Point3d axisOrigin,
        Vector3d axisDirection,
        Point3d pickedPoint,
        out Point3d projectedPoint,
        out double projectedFactor)
    {
        var lengthSquared = Dot2d(axisDirection, axisDirection);
        if (lengthSquared < PointTolerance * PointTolerance)
        {
            projectedPoint = default;
            projectedFactor = 0.0;
            return false;
        }

        projectedFactor = Dot2d(pickedPoint - axisOrigin, axisDirection) / lengthSquared;
        projectedPoint = axisOrigin + (axisDirection * projectedFactor);
        return true;
    }

    private static bool TryBuildLinearDimensionLinePoint(
        Point3d firstPoint,
        Point3d secondPoint,
        DddLinearDimensionService.LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        out Point3d dimLinePoint,
        out string error)
    {
        dimLinePoint = default;
        error = string.Empty;

        var midPoint = MidPoint(firstPoint, secondPoint);
        if (orientation == DddLinearDimensionService.LinearDimensionOrientation.Horizontal)
        {
            if (Math.Abs(secondPoint.X - firstPoint.X) < PointTolerance)
            {
                error = "新的标注脚距离端点过近，无法生成线性标注。";
                return false;
            }

            dimLinePoint = new Point3d(midPoint.X, dimLineOrdinate, midPoint.Z);
            return true;
        }

        if (Math.Abs(secondPoint.Y - firstPoint.Y) < PointTolerance)
        {
            error = "新的标注脚距离端点过近，无法生成线性标注。";
            return false;
        }

        dimLinePoint = new Point3d(dimLineOrdinate, midPoint.Y, midPoint.Z);
        return true;
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
            error = "新的标注脚距离端点过近，无法生成对齐标注。";
            return false;
        }

        if (Math.Abs(signedOffset) < PointTolerance)
        {
            error = "连续标注基线偏移量无效，请重新执行 F_DDE。";
            return false;
        }

        dimLinePoint = MidPoint(firstPoint, secondPoint) + (perpendicular * signedOffset);
        return true;
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

    private static int FindPointIndex(IList<Point3d> points, Point3d targetPoint)
    {
        for (var index = 0; index < points.Count; index++)
        {
            if (PointsCoincide(points[index], targetPoint))
                return index;
        }

        return -1;
    }

    private static void WriteAvoidanceFailure(Document doc, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            doc.Editor.WriteMessage($"\nC_TOOL：{error}");
    }

    private static double Dot2d(Vector3d left, Vector3d right) => (left.X * right.X) + (left.Y * right.Y);

    private static bool PointsCoincide(Point3d left, Point3d right) =>
        left.DistanceTo(right) < PointTolerance;

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static string FormatMeasurement(double measurement) => measurement.ToString("0.###");

    private enum AdjustmentKind
    {
        Linear,
        Aligned
    }

    private readonly struct AdjustmentPlan
    {
        internal AdjustmentPlan(
            AdjustmentKind kind,
            IList<Point3d> points,
            IList<ObjectId> dimensionIds,
            int sharedPointIndex,
            DddLinearDimensionService.LinearDimensionOrientation linearOrientation,
            double dimLineOrdinate,
            double signedOffset)
        {
            Kind = kind;
            Points = points;
            DimensionIds = dimensionIds;
            SharedPointIndex = sharedPointIndex;
            LinearOrientation = linearOrientation;
            DimLineOrdinate = dimLineOrdinate;
            SignedOffset = signedOffset;
        }

        internal AdjustmentKind Kind { get; }

        internal IList<Point3d> Points { get; }

        internal IList<ObjectId> DimensionIds { get; }

        internal int SharedPointIndex { get; }

        internal DddLinearDimensionService.LinearDimensionOrientation LinearOrientation { get; }

        internal double DimLineOrdinate { get; }

        internal double SignedOffset { get; }

        internal Point3d CurrentPoint => Points[SharedPointIndex];

        internal Point3d LeftPoint => Points[SharedPointIndex - 1];

        internal Point3d RightPoint => Points[SharedPointIndex + 1];
    }

    private readonly struct AdjustmentSummary
    {
        internal AdjustmentSummary(double beforeLeft, double beforeRight, double afterLeft, double afterRight)
        {
            BeforeLeft = beforeLeft;
            BeforeRight = beforeRight;
            AfterLeft = afterLeft;
            AfterRight = afterRight;
        }

        internal double BeforeLeft { get; }

        internal double BeforeRight { get; }

        internal double AfterLeft { get; }

        internal double AfterRight { get; }
    }
}
