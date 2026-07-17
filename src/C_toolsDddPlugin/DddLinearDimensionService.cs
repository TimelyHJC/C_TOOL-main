using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddLinearDimensionService
{
    private const double PointTolerance = 1e-6;
    private const double RotationTolerance = 1e-6;
    private static readonly ConcurrentDictionary<int, PendingLinearSession> PendingSessions = new();

    internal static bool TryContinueFromSelectedDimension(Document doc, ObjectId dimensionId)
    {
        var ed = doc.Editor;
        if (!TryReadSelectedDimensionContinuationState(
                doc,
                dimensionId,
                out var chainPoints,
                out var dimensionIds,
                out var selectedFirstPoint,
                out var selectedSecondPoint,
                out var orientation,
                out var dimLineOrdinate,
                out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return true;
        }

        ed.WriteMessage("\nC_TOOL：已识别线性标注，请在左/右/内侧指定终点。");
        ContinueChainFromSelection(doc, chainPoints, dimensionIds, selectedFirstPoint, selectedSecondPoint, orientation, dimLineOrdinate);
        return true;
    }

    internal static void Run(Document doc)
    {
        var ed = doc.Editor;
        if (TryContinueFromImpliedDimension(doc))
            return;

        ed.WriteMessage("\nC_TOOL：F_DC 启动。首段用原生线性标注，后续支持连续与避让。");
        if (!TryStartInitialLinearDimension(doc, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }
    }

    internal static bool ContinueAfterNativeLinearDimension(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (!PendingSessions.TryRemove(key, out var session))
            return false;

        try
        {
            if (!TryFindNewDimensionAfter(doc, session.BeforeEntityId, out var initialDimensionId, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    doc.Editor.WriteMessage($"\nC_TOOL：{error}");
                return true;
            }

            if (!TryReadInitialDimensionState(doc, initialDimensionId, out var firstPoint, out var secondPoint, out var orientation, out var dimLineOrdinate, out error))
            {
                doc.Editor.WriteMessage($"\nC_TOOL：{error}");
                return true;
            }

            if (!TryApplyChainTextAvoidance(
                    doc,
                    new List<Point3d> { firstPoint, secondPoint },
                    new List<ObjectId> { initialDimensionId },
                    orientation,
                    dimLineOrdinate,
                    out error))
            {
                WriteAvoidanceFailure(doc, error);
            }

            doc.Editor.WriteMessage("\nC_TOOL：已创建首段，继续点终点。");
            var points = SortPoints(new List<Point3d> { firstPoint, secondPoint }, orientation);
            var dimensionIds = new List<ObjectId> { initialDimensionId };
            ContinueChain(doc, points, dimensionIds, orientation, dimLineOrdinate);
            return true;
        }
        finally
        {
            UnsubscribeNativeCommandHandlers(doc);
        }
    }

    private static bool TryContinueFromImpliedDimension(Document doc)
    {
        var ed = doc.Editor;
        if (!TryGetImpliedLinearDimensionId(ed, out var dimensionId))
            return false;

        return TryContinueFromSelectedDimension(doc, dimensionId);
    }

    private static bool TryGetImpliedLinearDimensionId(Editor ed, out ObjectId dimensionId)
    {
        dimensionId = ObjectId.Null;

        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = implied.Value.GetObjectIds();
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        if (ids.Length != 1)
            return false;

        if (!DddDimensionSelectionFilter.IsRotatedDimension(ids[0]))
            return false;

        dimensionId = ids[0];
        return true;
    }

    private static bool TryStartInitialLinearDimension(Document doc, out string error)
    {
        error = string.Empty;

        if (!TryGetCurrentSpaceLastEntityId(doc, out var beforeEntityId, out error))
            return false;

        try
        {
            var key = GetDocumentKey(doc);
            CleanupPendingSession(doc, key);
            PendingSessions[key] = new PendingLinearSession(beforeEntityId);
            doc.CommandEnded += OnNativeLinearDimensionEnded;
            doc.CommandCancelled += OnNativeLinearDimensionCancelledOrFailed;
            doc.CommandFailed += OnNativeLinearDimensionCancelledOrFailed;

            if (!TryQueueNativeLinearDimension(doc, out error))
            {
                CleanupPendingSession(doc, key);
                return false;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            CleanupPendingSession(doc, GetDocumentKey(doc));
            C_toolsDiagnostics.LogNonFatal("F_DC 启动原生 DIMLINEAR 失败（无效操作）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            CleanupPendingSession(doc, GetDocumentKey(doc));
            C_toolsDiagnostics.LogNonFatal("F_DC 启动原生 DIMLINEAR 失败（CAD）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            CleanupPendingSession(doc, GetDocumentKey(doc));
            C_toolsDiagnostics.LogNonFatal("F_DC 启动原生 DIMLINEAR 失败（参数）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryQueueNativeLinearDimension(Document doc, out string error)
    {
        error = string.Empty;

        try
        {
            doc.SendStringToExecute("_.DIMLINEAR ", true, false, false);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 SendStringToExecute 启动 DIMLINEAR 失败（无效操作），尝试回退 AcadDocument.SendCommand", ex);
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 SendStringToExecute 启动 DIMLINEAR 失败（CAD），尝试回退 AcadDocument.SendCommand", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 SendStringToExecute 启动 DIMLINEAR 失败（参数），尝试回退 AcadDocument.SendCommand", ex);
        }

        try
        {
            var acadApplication = AcAp.AcadApplication;
            if (acadApplication == null)
            {
                error = "执行原生 DIMLINEAR 失败：AcadApplication 不可用。";
                return false;
            }

            var activeDocumentProperty = acadApplication.GetType().GetProperty("ActiveDocument");
            var acadDocument = activeDocumentProperty?.GetValue(acadApplication);
            if (acadDocument == null)
            {
                error = "执行原生 DIMLINEAR 失败：AcadDocument 不可用。";
                return false;
            }

            var sendCommandMethod = acadDocument.GetType().GetMethod("SendCommand", new[] { typeof(string) });
            if (sendCommandMethod == null)
            {
                error = "执行原生 DIMLINEAR 失败：未找到 AcadDocument.SendCommand。";
                return false;
            }

            sendCommandMethod.Invoke(acadDocument, new object[] { "_.DIMLINEAR " });
            C_toolsDiagnostics.LogNonFatal("F_DC 已回退使用 AcadDocument.SendCommand 启动原生 DIMLINEAR。");
            return true;
        }
        catch (TargetInvocationException ex)
        {
            var actual = ex.InnerException ?? ex;
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 AcadDocument.SendCommand 启动 DIMLINEAR 失败（反射调用）", actual);
            error = $"执行原生 DIMLINEAR 失败：{actual.Message}";
            return false;
        }
        catch (COMException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 AcadDocument.SendCommand 启动 DIMLINEAR 失败（COM）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
        catch (MissingMethodException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 AcadDocument.SendCommand 启动 DIMLINEAR 失败（缺少方法）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 AcadDocument.SendCommand 启动 DIMLINEAR 失败（无效操作）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 使用 AcadDocument.SendCommand 启动 DIMLINEAR 失败（参数）", ex);
            error = $"执行原生 DIMLINEAR 失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryReadInitialDimensionState(
        Document doc,
        ObjectId dimensionId,
        out Point3d firstPoint,
        out Point3d secondPoint,
        out LinearDimensionOrientation orientation,
        out double dimLineOrdinate,
        out string error)
    {
        firstPoint = default;
        secondPoint = default;
        orientation = default;
        dimLineOrdinate = 0.0;
        error = string.Empty;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var dbObject = tr.GetObject(dimensionId, OpenMode.ForRead, false);
            if (dbObject is not RotatedDimension dimension)
            {
                error = "首个线性标注读取失败，请重新执行 F_DC。";
                return false;
            }

            if (!TryGetSelectedDimensionSettings(dimension, out orientation, out dimLineOrdinate, out error))
                return false;

            firstPoint = dimension.XLine1Point;
            secondPoint = dimension.XLine2Point;
            NormalizeLinearContinuationDirection(orientation, ref firstPoint, ref secondPoint);
            tr.Commit();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取首段线性标注状态失败（无效操作）", ex);
            error = $"读取首段线性标注状态失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取首段线性标注状态失败（CAD）", ex);
            error = $"读取首段线性标注状态失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取首段线性标注状态失败（参数）", ex);
            error = $"读取首段线性标注状态失败：{ex.Message}";
            return false;
        }
    }

    private static void ContinueChain(
        Document doc,
        List<Point3d> points,
        List<ObjectId> dimensionIds,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate)
    {
        var cursorPoint = points[1];
        var ed = doc.Editor;

        while (true)
        {
            var continuation = GetContinuationPoint(ed, cursorPoint, "\nNext point <exit>: ");
            if (!continuation.HasPoint)
                return;

            var projectedPoint = BuildPointOnContinuationAxis(cursorPoint, continuation.Point, orientation);
            if (DddDimensionChainHelper.PointExists(projectedPoint, points, PointTolerance))
            {
                cursorPoint = projectedPoint;
                ed.WriteMessage("\nC_TOOL：点重合，已忽略。");
                continue;
            }

            var rebuiltPoints = BuildUpdatedPointList(points, projectedPoint, orientation);
            if (!TryRebuildChain(doc, rebuiltPoints, dimensionIds, orientation, dimLineOrdinate, out var rebuiltIds, out var error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                continue;
            }

            points = rebuiltPoints;
            dimensionIds = rebuiltIds;
            cursorPoint = projectedPoint;
        }
    }

    private static void ContinueChainFromSelection(
        Document doc,
        List<Point3d> chainPoints,
        List<ObjectId> dimensionIds,
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate)
    {
        if (chainPoints.Count < 2)
            return;

        var ed = doc.Editor;
        var basePoint = MidPoint(selectedFirstPoint, selectedSecondPoint);

        while (true)
        {
            var continuation = GetContinuationPoint(
                ed,
                basePoint,
                "\nNext point <exit>: ");
            if (!continuation.HasPoint)
                return;

            if (DddDimensionChainHelper.TryResolveSplitSegment(
                    chainPoints,
                    dimensionIds,
                    (firstPoint, secondPoint) =>
                    {
                        var candidateSplitPoint = BuildPointOnSelectedSegment(firstPoint, secondPoint, continuation.Point, orientation);
                        return (
                            IsPointInsideSelectedSegment(firstPoint, secondPoint, candidateSplitPoint, orientation),
                            candidateSplitPoint);
                    },
                    out var splitDimensionId,
                    out var splitIndex,
                    out var splitFirstPoint,
                    out var splitSecondPoint,
                    out var splitPoint))
            {
                if (!TrySplitSelectedLinearDimension(
                        doc,
                        splitDimensionId,
                        splitFirstPoint,
                        splitSecondPoint,
                        splitPoint,
                        orientation,
                        dimLineOrdinate,
                        out var splitDimensionIds,
                        out var splitError))
                {
                    ed.WriteMessage($"\nC_TOOL：{splitError}");
                    continue;
                }

                ed.WriteMessage("\nC_TOOL：已打断线性标注段。");
                if (!DddDimensionChainHelper.TryReplaceSplitSegment(
                        chainPoints,
                        dimensionIds,
                        splitIndex,
                        splitPoint,
                        splitDimensionIds))
                {
                    ed.WriteMessage("\nC_TOOL：标注链状态异常，请重新选择尺寸标注后继续。");
                    return;
                }

                continue;
            }

            var continuationSplitPoint = BuildPointOnSelectedSegment(selectedFirstPoint, selectedSecondPoint, continuation.Point, orientation);
            var sideCursor = DddDimensionChainHelper.ResolveSelectedContinuationCursor(
                chainPoints,
                GetOrientationCoordinate(orientation, selectedFirstPoint),
                GetOrientationCoordinate(orientation, selectedSecondPoint),
                GetOrientationCoordinate(orientation, continuationSplitPoint));

            var nextPoint = BuildPointOnContinuationAxis(sideCursor, continuation.Point, orientation);
            var extendToMinSide = PointsCoincide(sideCursor, chainPoints[0]);
            if (PointsCoincide(nextPoint, sideCursor) || DddDimensionChainHelper.PointExists(nextPoint, chainPoints, PointTolerance))
            {
                ed.WriteMessage("\nC_TOOL：点重合，已忽略。");
            }
            else
            {
                var cursorCoordinate = GetOrientationCoordinate(orientation, sideCursor);
                var nextCoordinate = GetOrientationCoordinate(orientation, nextPoint);
                var validDirection = extendToMinSide
                    ? nextCoordinate < cursorCoordinate - PointTolerance
                    : nextCoordinate > cursorCoordinate + PointTolerance;

                if (!validDirection)
                {
                    ed.WriteMessage(extendToMinSide
                        ? "\nC_TOOL：请在所选标注左侧继续点取，标注会保持在同一排。"
                        : "\nC_TOOL：请在所选标注右侧继续点取，标注会保持在同一排。");
                }
                else
                {
                    var rebuiltPoints = BuildUpdatedPointList(chainPoints, nextPoint, orientation);
                    if (!TryRebuildChain(doc, rebuiltPoints, dimensionIds, orientation, dimLineOrdinate, out var rebuiltIds, out var error))
                    {
                        ed.WriteMessage($"\nC_TOOL：{error}");
                    }
                    else
                    {
                        chainPoints = rebuiltPoints;
                        dimensionIds = rebuiltIds;
                        ed.WriteMessage("\nC_TOOL：已追加线性标注，继续点终点。");
                    }
                }
            }
        }
    }

    private static bool IsPointInsideSelectedSegment(
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        Point3d projectedPoint,
        LinearDimensionOrientation orientation)
    {
        var startCoordinate = GetOrientationCoordinate(orientation, selectedFirstPoint);
        var endCoordinate = GetOrientationCoordinate(orientation, selectedSecondPoint);
        var projectedCoordinate = GetOrientationCoordinate(orientation, projectedPoint);
        var minCoordinate = Math.Min(startCoordinate, endCoordinate);
        var maxCoordinate = Math.Max(startCoordinate, endCoordinate);
        return projectedCoordinate > minCoordinate + PointTolerance &&
               projectedCoordinate < maxCoordinate - PointTolerance;
    }

    private static Point3d BuildPointOnContinuationAxis(
        Point3d anchorPoint,
        Point3d pickedPoint,
        LinearDimensionOrientation orientation)
    {
        return orientation == LinearDimensionOrientation.Horizontal
            ? new Point3d(pickedPoint.X, anchorPoint.Y, anchorPoint.Z)
            : new Point3d(anchorPoint.X, pickedPoint.Y, anchorPoint.Z);
    }

    private static Point3d BuildPointOnSelectedSegment(
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        Point3d pickedPoint,
        LinearDimensionOrientation orientation)
    {
        if (orientation == LinearDimensionOrientation.Horizontal)
        {
            var delta = selectedSecondPoint.X - selectedFirstPoint.X;
            if (Math.Abs(delta) < PointTolerance)
                return new Point3d(pickedPoint.X, selectedFirstPoint.Y, selectedFirstPoint.Z);

            var factor = (pickedPoint.X - selectedFirstPoint.X) / delta;
            return new Point3d(
                pickedPoint.X,
                selectedFirstPoint.Y + ((selectedSecondPoint.Y - selectedFirstPoint.Y) * factor),
                selectedFirstPoint.Z + ((selectedSecondPoint.Z - selectedFirstPoint.Z) * factor));
        }

        var verticalDelta = selectedSecondPoint.Y - selectedFirstPoint.Y;
        if (Math.Abs(verticalDelta) < PointTolerance)
            return new Point3d(selectedFirstPoint.X, pickedPoint.Y, selectedFirstPoint.Z);

        var verticalFactor = (pickedPoint.Y - selectedFirstPoint.Y) / verticalDelta;
        return new Point3d(
            selectedFirstPoint.X + ((selectedSecondPoint.X - selectedFirstPoint.X) * verticalFactor),
            pickedPoint.Y,
            selectedFirstPoint.Z + ((selectedSecondPoint.Z - selectedFirstPoint.Z) * verticalFactor));
    }

    private static bool TrySplitSelectedLinearDimension(
        Document doc,
        ObjectId selectedDimensionId,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d splitPoint,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        out List<ObjectId> createdIds,
        out string error)
    {
        createdIds = new List<ObjectId>(2);
        error = string.Empty;
        if (PointsCoincide(splitPoint, firstPoint) || PointsCoincide(splitPoint, secondPoint))
        {
            error = "内点距离端点过近，无法打断所选标注。";
            return false;
        }

        if (!TryBuildDimensionLinePoint(firstPoint, splitPoint, orientation, dimLineOrdinate, out var firstDimLinePoint, out error))
            return false;

        if (!TryBuildDimensionLinePoint(splitPoint, secondPoint, orientation, dimLineOrdinate, out var secondDimLinePoint, out error))
            return false;

        try
        {
            var db = doc.Database;
            var rotation = orientation == LinearDimensionOrientation.Horizontal ? 0.0 : (Math.PI * 0.5);
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var dbObject = tr.GetObject(selectedDimensionId, OpenMode.ForWrite, false);
                if (dbObject is not RotatedDimension selectedDimension || selectedDimension.IsErased)
                {
                    error = "所选线性标注已失效，请重新选择。";
                    return false;
                }

                using (var firstDim = new RotatedDimension(rotation, firstPoint, splitPoint, firstDimLinePoint, string.Empty, db.Dimstyle))
                {
                    ApplyDimensionDefaults(firstDim, db);
                    firstDim.LayerId = selectedDimension.LayerId;
                    firstDim.RecomputeDimensionBlock(true);
                    currentSpace.AppendEntity(firstDim);
                    tr.AddNewlyCreatedDBObject(firstDim, true);
                    createdIds.Add(firstDim.ObjectId);
                }

                using (var secondDim = new RotatedDimension(rotation, splitPoint, secondPoint, secondDimLinePoint, string.Empty, db.Dimstyle))
                {
                    ApplyDimensionDefaults(secondDim, db);
                    secondDim.LayerId = selectedDimension.LayerId;
                    secondDim.RecomputeDimensionBlock(true);
                    currentSpace.AppendEntity(secondDim);
                    tr.AddNewlyCreatedDBObject(secondDim, true);
                    createdIds.Add(secondDim.ObjectId);
                }

                selectedDimension.Erase();
                tr.Commit();
            }

            if (!TryApplyChainTextAvoidance(
                    doc,
                    new List<Point3d> { firstPoint, splitPoint, secondPoint },
                    createdIds,
                    orientation,
                    dimLineOrdinate,
                    out error))
            {
                WriteAvoidanceFailure(doc, error);
                error = string.Empty;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 打断所选线性标注失败（无效操作）", ex);
            error = $"打断所选线性标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 打断所选线性标注失败（CAD）", ex);
            error = $"打断所选线性标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 打断所选线性标注失败（参数）", ex);
            error = $"打断所选线性标注失败：{ex.Message}";
            return false;
        }
    }

    internal static bool TryReadSelectedDimensionContinuationState(
        Document doc,
        ObjectId dimensionId,
        out List<Point3d> chainPoints,
        out List<ObjectId> dimensionIds,
        out Point3d selectedFirstPoint,
        out Point3d selectedSecondPoint,
        out LinearDimensionOrientation orientation,
        out double dimLineOrdinate,
        out string error)
    {
        chainPoints = new List<Point3d>();
        dimensionIds = new List<ObjectId>();
        selectedFirstPoint = default;
        selectedSecondPoint = default;
        orientation = default;
        dimLineOrdinate = 0.0;
        error = string.Empty;

        try
        {
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dbObject = tr.GetObject(dimensionId, OpenMode.ForRead, false);
                if (dbObject is not RotatedDimension dimension)
                {
                    error = "所选对象不是线性标注，无法继续。";
                    return false;
                }

                if (!TryGetSelectedDimensionSettings(dimension, out orientation, out dimLineOrdinate, out error))
                    return false;

                var firstPoint = dimension.XLine1Point;
                var secondPoint = dimension.XLine2Point;
                NormalizeLinearContinuationDirection(orientation, ref firstPoint, ref secondPoint);
                selectedFirstPoint = firstPoint;
                selectedSecondPoint = secondPoint;

                if (!TryCollectSelectedLinearChainPoints(
                        tr,
                        dimensionId,
                        dimension,
                        orientation,
                        dimLineOrdinate,
                        selectedFirstPoint,
                        selectedSecondPoint,
                        out chainPoints,
                        out dimensionIds,
                        out error))
                {
                    return false;
                }

                tr.Commit();
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取所选标注续接状态失败（无效操作）", ex);
            error = $"读取所选标注续接状态失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取所选标注续接状态失败（CAD）", ex);
            error = $"读取所选标注续接状态失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取所选标注续接状态失败（参数）", ex);
            error = $"读取所选标注续接状态失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryGetSelectedDimensionSettings(
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
        error = "所选线性标注方向不是水平/垂直，无法继续。";
        return false;
    }

    private static int GetDocumentKey(Document doc) => doc.GetHashCode();

    private static void OnNativeLinearDimensionEnded(object? sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, "DIMLINEAR", StringComparison.OrdinalIgnoreCase))
            return;

        var doc = sender as Document ?? AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var key = GetDocumentKey(doc);
        if (!PendingSessions.ContainsKey(key))
            return;

        UnsubscribeNativeCommandHandlers(doc);
        doc.SendStringToExecute("_." + DddPluginCommandIds.DimLinearContinueInternal + "\n", true, false, false);
    }

    private static void OnNativeLinearDimensionCancelledOrFailed(object? sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, "DIMLINEAR", StringComparison.OrdinalIgnoreCase))
            return;

        var doc = sender as Document ?? AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        CleanupPendingSession(doc, GetDocumentKey(doc));
    }

    private static void CleanupPendingSession(Document doc, int key)
    {
        PendingSessions.TryRemove(key, out _);
        UnsubscribeNativeCommandHandlers(doc);
    }

    private static void UnsubscribeNativeCommandHandlers(Document doc)
    {
        try
        {
            doc.CommandEnded -= OnNativeLinearDimensionEnded;
            doc.CommandCancelled -= OnNativeLinearDimensionCancelledOrFailed;
            doc.CommandFailed -= OnNativeLinearDimensionCancelledOrFailed;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 取消事件订阅失败（无效操作）", ex);
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 取消事件订阅失败（CAD）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 取消事件订阅失败（参数）", ex);
        }
    }

    private static bool TryCollectSelectedLinearChainPoints(
        Transaction tr,
        ObjectId dimensionId,
        RotatedDimension selectedDimension,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        out List<Point3d> points,
        out List<ObjectId> dimensionIds,
        out string error)
    {
        points = new List<Point3d>();
        dimensionIds = new List<ObjectId>();
        error = string.Empty;
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

                if (!TryGetSelectedDimensionSettings(candidate, out var candidateOrientation, out var candidateOrdinate, out _))
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

        points.Sort((left, right) =>
            GetOrientationCoordinate(orientation, left).CompareTo(GetOrientationCoordinate(orientation, right)));
        if (points.Count < 2)
        {
            error = "未能解析所选线性标注所在的连续标注链。";
            return false;
        }

        DddDimensionChainHelper.AddDimensionIdsInOrder(
            segments,
            dimensionIds,
            (left, right) => GetOrientationCoordinate(orientation, left.First).CompareTo(GetOrientationCoordinate(orientation, right.First)));

        return true;
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

    private static double GetOrientationCoordinate(LinearDimensionOrientation orientation, Point3d point) =>
        orientation == LinearDimensionOrientation.Horizontal ? point.X : point.Y;

    private static void AddUniquePoint(List<Point3d> points, Point3d point)
    {
        foreach (var existing in points)
        {
            if (PointsCoincide(existing, point))
                return;
        }

        points.Add(point);
    }

    private static List<Point3d> BuildUpdatedPointList(
        IList<Point3d> points,
        Point3d newPoint,
        LinearDimensionOrientation orientation)
    {
        var updated = new List<Point3d>(points.Count + 1);
        foreach (var point in points)
            updated.Add(point);
        updated.Add(newPoint);
        return SortPoints(updated, orientation);
    }

    private static List<Point3d> SortPoints(List<Point3d> points, LinearDimensionOrientation orientation)
    {
        points.Sort((left, right) =>
            GetOrientationCoordinate(orientation, left).CompareTo(GetOrientationCoordinate(orientation, right)));
        return points;
    }

    private static bool TryGetCurrentSpaceLastEntityId(Document doc, out ObjectId lastEntityId, out string error)
    {
        lastEntityId = ObjectId.Null;
        error = string.Empty;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var currentSpace = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId objectId in currentSpace)
            {
                if (!objectId.IsErased)
                    lastEntityId = objectId;
            }

            tr.Commit();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取当前空间尾实体失败（无效操作）", ex);
            error = $"读取当前空间尾实体失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取当前空间尾实体失败（CAD）", ex);
            error = $"读取当前空间尾实体失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 读取当前空间尾实体失败（参数）", ex);
            error = $"读取当前空间尾实体失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryRebuildChain(
        Document doc,
        List<Point3d> points,
        List<ObjectId> existingDimensionIds,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        out List<ObjectId> rebuiltIds,
        out string error)
    {
        rebuiltIds = new List<ObjectId>();
        error = string.Empty;

        try
        {
            var db = doc.Database;
            var rotation = orientation == LinearDimensionOrientation.Horizontal ? 0.0 : (Math.PI * 0.5);
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                for (var index = 0; index < points.Count - 1; index++)
                {
                    var firstPoint = points[index];
                    var secondPoint = points[index + 1];
                    if (!TryBuildDimensionLinePoint(firstPoint, secondPoint, orientation, dimLineOrdinate, out var dimLinePoint, out error))
                        return false;

                    using var dim = new RotatedDimension(rotation, firstPoint, secondPoint, dimLinePoint, string.Empty, db.Dimstyle);
                    ApplyDimensionDefaults(dim, db);
                    currentSpace.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    rebuiltIds.Add(dim.ObjectId);
                }

                foreach (var existingId in existingDimensionIds)
                {
                    if (!existingId.IsValid || existingId.IsErased)
                        continue;

                    var dbObject = tr.GetObject(existingId, OpenMode.ForWrite, false);
                    if (dbObject is Entity entity && !entity.IsErased)
                        entity.Erase();
                }

                tr.Commit();
            }
        }
        catch (InvalidOperationException ex)
        {
            CleanupCreatedDimensions(doc, rebuiltIds);
            C_toolsDiagnostics.LogNonFatal("F_DC 重建线性标注链失败（无效操作）", ex);
            error = $"重建线性标注链失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            CleanupCreatedDimensions(doc, rebuiltIds);
            C_toolsDiagnostics.LogNonFatal("F_DC 重建线性标注链失败（CAD）", ex);
            error = $"重建线性标注链失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            CleanupCreatedDimensions(doc, rebuiltIds);
            C_toolsDiagnostics.LogNonFatal("F_DC 重建线性标注链失败（参数）", ex);
            error = $"重建线性标注链失败：{ex.Message}";
            return false;
        }

        if (!TryApplyChainTextAvoidance(doc, points, rebuiltIds, orientation, dimLineOrdinate, out error))
        {
            WriteAvoidanceFailure(doc, error);
            error = string.Empty;
        }

        return true;
    }

    private static void CleanupCreatedDimensions(Document doc, IEnumerable<ObjectId> dimensionIds)
    {
        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in dimensionIds)
                {
                    if (!id.IsValid || id.IsErased)
                        continue;

                    var dbObject = tr.GetObject(id, OpenMode.ForWrite, false);
                    if (dbObject is Entity entity && !entity.IsErased)
                        entity.Erase();
                }

                tr.Commit();
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 清理失败（无效操作）", ex);
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 清理失败（CAD）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 清理失败（参数）", ex);
        }
    }

    private static bool TryFindNewDimensionAfter(Document doc, ObjectId beforeEntityId, out ObjectId dimensionId, out string error)
    {
        dimensionId = ObjectId.Null;
        error = string.Empty;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var currentSpace = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead);
            var seenBefore = beforeEntityId.IsNull;
            foreach (ObjectId objectId in currentSpace)
            {
                if (!seenBefore)
                {
                    if (objectId == beforeEntityId)
                        seenBefore = true;
                    continue;
                }

                if (objectId.IsErased)
                    continue;

                var dbObject = tr.GetObject(objectId, OpenMode.ForRead, false);
                if (dbObject is RotatedDimension)
                    dimensionId = objectId;
            }

            tr.Commit();
            if (dimensionId.IsNull)
            {
                error = "未找到由原生 DIMLINEAR 创建的首个线性标注。";
                return false;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 查找原生首段线性标注失败（无效操作）", ex);
            error = $"查找原生首段线性标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 查找原生首段线性标注失败（CAD）", ex);
            error = $"查找原生首段线性标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DC 查找原生首段线性标注失败（参数）", ex);
            error = $"查找原生首段线性标注失败：{ex.Message}";
            return false;
        }
    }

    private static ContinuationPointResult GetContinuationPoint(Editor ed, Point3d basePoint, string message)
    {
        var options = new PromptPointOptions(message)
        {
            BasePoint = basePoint,
            UseBasePoint = true,
            AllowNone = true
        };

        var result = ed.GetPoint(options);
        return result.Status == PromptStatus.OK
            ? ContinuationPointResult.FromPoint(result.Value)
            : ContinuationPointResult.Exit();
    }

    private static void ApplyDimensionDefaults(RotatedDimension dim, Database db)
    {
        dim.SetDatabaseDefaults(db);
        using var styleData = db.GetDimstyleData();
        dim.DimensionStyle = db.Dimstyle;
        dim.SetDimstyleData(styleData);
        ApplyCurrentAnnotationScaleContext(dim, db, styleData);
        dim.LayerId = db.Clayer;
        dim.DimensionText = string.Empty;
        dim.Dimatfit = 3;
        dim.Dimtix = true;
        dim.Dimtofl = true;
        dim.Dimtmove = 2;
        dim.RecomputeDimensionBlock(true);
    }

    private static void ApplyCurrentAnnotationScaleContext(Dimension dim, Database db, DimStyleTableRecord styleData)
    {
        var isAnnotative = styleData.Annotative == AnnotativeStates.True;
        dim.Annotative = isAnnotative ? AnnotativeStates.True : AnnotativeStates.False;
        if (!isAnnotative)
            return;

        var currentScale = db.Cannoscale;
        if (currentScale == null)
            return;

        if (!dim.HasContext(currentScale))
            dim.AddContext(currentScale);

        dim.ResetScaleDependentProperties();
    }

    private static void WriteAvoidanceFailure(Document doc, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            doc.Editor.WriteMessage($"\nC_TOOL：{error}");
    }

    private static bool TryApplyChainTextAvoidance(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        LinearDimensionOrientation orientation,
        double dimLineOrdinate,
        out string error)
    {
        return DddDimensionTextAvoidanceService.TryApplyLinearChain(
            doc,
            points,
            dimensionIds,
            orientation == LinearDimensionOrientation.Horizontal,
            dimLineOrdinate,
            out error);
    }

    private static bool TryBuildDimensionLinePoint(
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
                error = "当前两点在水平方向上过近，无法继续线性标注。";
                return false;
            }

            dimLinePoint = new Point3d(midPoint.X, dimLineOrdinate, midPoint.Z);
            return true;
        }

        if (Math.Abs(secondPoint.Y - firstPoint.Y) < PointTolerance)
        {
            error = "当前两点在垂直方向上过近，无法继续线性标注。";
            return false;
        }

        dimLinePoint = new Point3d(dimLineOrdinate, midPoint.Y, midPoint.Z);
        return true;
    }

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static bool PointsCoincide(Point3d left, Point3d right) =>
        left.DistanceTo(right) < PointTolerance;

    private static double NormalizeRotation(double rotation)
    {
        var normalized = rotation % (Math.PI * 2.0);
        if (normalized < 0.0)
            normalized += Math.PI * 2.0;
        return normalized;
    }

    internal enum LinearDimensionOrientation
    {
        Horizontal,
        Vertical
    }

    private readonly struct ContinuationPointResult
    {
        private ContinuationPointResult(Point3d point, bool hasPoint)
        {
            Point = point;
            HasPoint = hasPoint;
        }

        internal Point3d Point { get; }

        internal bool HasPoint { get; }

        internal static ContinuationPointResult FromPoint(Point3d point) => new(point, true);

        internal static ContinuationPointResult Exit() => new(default, false);
    }

    private readonly struct PendingLinearSession
    {
        internal PendingLinearSession(ObjectId beforeEntityId)
        {
            BeforeEntityId = beforeEntityId;
        }

        internal ObjectId BeforeEntityId { get; }
    }
}
