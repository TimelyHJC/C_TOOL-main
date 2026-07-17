using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddAlignedDimensionService
{
    private const double PointTolerance = 1e-6;
    private const double ChainRowTolerance = 1e-4;
    private const double ParallelDotTolerance = 0.999;
    private static readonly ConcurrentDictionary<string, PendingAlignedSession> PendingSessions = new(StringComparer.OrdinalIgnoreCase);

    internal static void Run(Document doc)
    {
        if (TryContinueFromImpliedDimension(doc))
            return;

        if (!TryStartInitialAlignedDimension(doc, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                doc.Editor.WriteMessage($"\nC_TOOL：{error}");
            return;
        }
    }

    internal static void ContinueAfterNativeAlignedDimension(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (!PendingSessions.TryRemove(key, out var session))
            return;

        try
        {
            if (!TryFindNewDimensionAfter(doc, session.BeforeEntityId, out var initialDimensionId, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    doc.Editor.WriteMessage($"\nC_TOOL：{error}");
                return;
            }

            if (!TryReadInitialDimensionState(doc, initialDimensionId, out var firstPoint, out var secondPoint, out var signedOffset, out error))
            {
                doc.Editor.WriteMessage($"\nC_TOOL：{error}");
                return;
            }

            var points = SortPoints(new List<Point3d> { firstPoint, secondPoint }, firstPoint, secondPoint - firstPoint);
            var dimensionIds = new List<ObjectId> { initialDimensionId };
            if (!TryApplyChainTextAvoidance(doc, points, dimensionIds, signedOffset, out error))
                WriteAvoidanceFailure(doc, error);

            ContinueChain(doc, points, dimensionIds, signedOffset);
        }
        finally
        {
            UnsubscribeNativeCommandHandlers(doc);
        }
    }

    private static bool TryContinueFromImpliedDimension(Document doc)
    {
        var ed = doc.Editor;
        if (!TryGetImpliedDimensionId(ed, out var dimensionId, out var continuationKind))
            return false;

        if (continuationKind == ImpliedDimensionContinuationKind.Linear)
            return DddLinearDimensionService.TryContinueFromSelectedDimension(doc, dimensionId);

        if (continuationKind != ImpliedDimensionContinuationKind.Aligned)
        {
            ed.WriteMessage("\nC_TOOL：预选标注不支持续接，请选线性或对齐标注。");
            return true;
        }

        if (!TryReadSelectedDimensionContinuationState(
                doc,
                dimensionId,
                out var points,
                out var dimensionIds,
                out var selectedFirstPoint,
                out var selectedSecondPoint,
                out var signedOffset,
                out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return true;
        }

        ed.WriteMessage("\nC_TOOL：已识别对齐标注，请在左/右/内侧指定终点。");
        ContinueAppendingChainFromSelection(doc, points, dimensionIds, selectedFirstPoint, selectedSecondPoint, signedOffset);
        return true;
    }

    private static bool TryStartInitialAlignedDimension(Document doc, out string error)
    {
        error = string.Empty;

        if (!TryGetCurrentSpaceLastEntityId(doc, out var beforeEntityId, out error))
            return false;

        try
        {
            var key = GetDocumentKey(doc);
            CleanupPendingSession(doc, key);
            PendingSessions[key] = new PendingAlignedSession(beforeEntityId);
            doc.CommandEnded += OnNativeAlignedDimensionEnded;
            doc.CommandCancelled += OnNativeAlignedDimensionCancelledOrFailed;
            doc.CommandFailed += OnNativeAlignedDimensionCancelledOrFailed;
            doc.SendStringToExecute("_.DIMALIGNED ", true, false, false);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            CleanupPendingSession(doc, GetDocumentKey(doc));
            C_toolsDiagnostics.LogNonFatal("F_DA 启动原生 DIMALIGNED 失败（无效操作）", ex);
            error = $"执行原生 DIMALIGNED 失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            CleanupPendingSession(doc, GetDocumentKey(doc));
            C_toolsDiagnostics.LogNonFatal("F_DA 启动原生 DIMALIGNED 失败（CAD）", ex);
            error = $"执行原生 DIMALIGNED 失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            CleanupPendingSession(doc, GetDocumentKey(doc));
            C_toolsDiagnostics.LogNonFatal("F_DA 启动原生 DIMALIGNED 失败（参数）", ex);
            error = $"执行原生 DIMALIGNED 失败：{ex.Message}";
            return false;
        }
    }

    private static void ContinueAppendingChainFromSelection(
        Document doc,
        List<Point3d> chainPoints,
        List<ObjectId> dimensionIds,
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        double signedOffset)
    {
        if (chainPoints.Count < 2)
            return;

        var axisOrigin = chainPoints[0];
        var axisDirection = chainPoints[^1] - chainPoints[0];
        var ed = doc.Editor;
        var basePoint = MidPoint(selectedFirstPoint, selectedSecondPoint);

        while (true)
        {
            var continuation = GetContinuationPoint(ed, basePoint, "\nNext point <exit>: ");
            if (!continuation.HasPoint)
                return;

            if (!TryProjectPointToAxis(axisOrigin, axisDirection, continuation.Point, out var projectedPoint, out _))
            {
                ed.WriteMessage("\nC_TOOL：尺寸方向无效，无法投影。");
                return;
            }

            if (DddDimensionChainHelper.PointExists(projectedPoint, chainPoints, PointTolerance))
            {
                ed.WriteMessage("\nC_TOOL：点重合，已忽略。");
                continue;
            }

            if (DddDimensionChainHelper.TryResolveSplitSegment(
                    chainPoints,
                    dimensionIds,
                    (firstPoint, secondPoint) => (
                        IsPointInsideSelectedSegment(axisOrigin, axisDirection, firstPoint, secondPoint, projectedPoint),
                        projectedPoint),
                    out var splitDimensionId,
                    out var splitIndex,
                    out var splitFirstPoint,
                    out var splitSecondPoint,
                    out var splitPoint))
            {
                if (!TrySplitSelectedAlignedDimension(
                        doc,
                        splitDimensionId,
                        splitFirstPoint,
                        splitSecondPoint,
                        splitPoint,
                        signedOffset,
                        out var splitDimensionIds,
                        out var splitError))
                {
                    ed.WriteMessage($"\nC_TOOL：{splitError}");
                    continue;
                }

                ed.WriteMessage("\nC_TOOL：已打断标注段。");
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

            var sideCursor = DddDimensionChainHelper.ResolveSelectedContinuationCursor(
                chainPoints,
                AxisParameter(selectedFirstPoint, axisOrigin, axisDirection),
                AxisParameter(selectedSecondPoint, axisOrigin, axisDirection),
                AxisParameter(projectedPoint, axisOrigin, axisDirection));
            var extendToMinSide = PointsCoincide(sideCursor, chainPoints[0]);
            var cursorParameter = AxisParameter(sideCursor, axisOrigin, axisDirection);
            var nextParameter = AxisParameter(projectedPoint, axisOrigin, axisDirection);
            var validDirection = extendToMinSide
                ? nextParameter < cursorParameter - PointTolerance
                : nextParameter > cursorParameter + PointTolerance;

            if (!validDirection)
            {
                ed.WriteMessage(extendToMinSide
                    ? "\nC_TOOL：请在所选标注左侧继续点取，标注会保持在同一排。"
                    : "\nC_TOOL：请在所选标注右侧继续点取，标注会保持在同一排。");
                continue;
            }

            var segmentFirst = extendToMinSide ? projectedPoint : sideCursor;
            var segmentSecond = extendToMinSide ? sideCursor : projectedPoint;
            if (!TryAppendAlignedDimension(doc, segmentFirst, segmentSecond, signedOffset, out var createdDimensionId, out var error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                continue;
            }

            if (extendToMinSide)
                dimensionIds.Insert(0, createdDimensionId);
            else
                dimensionIds.Add(createdDimensionId);
            chainPoints = BuildUpdatedPointList(chainPoints, projectedPoint, axisOrigin, axisDirection);
            if (!TryApplyChainTextAvoidance(doc, chainPoints, dimensionIds, signedOffset, out error))
                WriteAvoidanceFailure(doc, error);

            ed.WriteMessage("\nC_TOOL：已追加对齐标注，继续点终点。");
        }
    }

    private static void ContinueChain(
        Document doc,
        List<Point3d> points,
        List<ObjectId> dimensionIds,
        double signedOffset)
    {
        var axisOrigin = points[0];
        var axisDirection = points[1] - points[0];
        var cursorPoint = points[1];
        var ed = doc.Editor;

        while (true)
        {
            var continuation = GetContinuationPoint(ed, cursorPoint, "\nNext point <exit>: ");
            if (!continuation.HasPoint)
                return;

            if (!TryProjectPointToAxis(axisOrigin, axisDirection, continuation.Point, out var projectedPoint, out _))
            {
                ed.WriteMessage("\nC_TOOL：尺寸方向无效，无法投影。");
                return;
            }

            if (DddDimensionChainHelper.PointExists(projectedPoint, points, PointTolerance))
            {
                cursorPoint = projectedPoint;
                ed.WriteMessage("\nC_TOOL：点重合，已忽略。");
                continue;
            }

            var rebuiltPoints = BuildUpdatedPointList(points, projectedPoint, axisOrigin, axisDirection);
            if (!TryRebuildChain(doc, rebuiltPoints, dimensionIds, signedOffset, out var rebuiltIds, out var error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                continue;
            }

            points = rebuiltPoints;
            dimensionIds = rebuiltIds;
            cursorPoint = projectedPoint;
        }
    }

    private static string GetDocumentKey(Document doc) =>
        doc.GetHashCode().ToString(CultureInfo.InvariantCulture);

    private static void OnNativeAlignedDimensionEnded(object? sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, "DIMALIGNED", StringComparison.OrdinalIgnoreCase))
            return;

        var doc = sender as Document ?? AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var key = GetDocumentKey(doc);
        if (!PendingSessions.ContainsKey(key))
            return;

        UnsubscribeNativeCommandHandlers(doc);
        doc.SendStringToExecute("_." + DddPluginCommandIds.DimAlignedContinueInternal + "\n", true, false, false);
    }

    private static void OnNativeAlignedDimensionCancelledOrFailed(object? sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, "DIMALIGNED", StringComparison.OrdinalIgnoreCase))
            return;

        var doc = sender as Document ?? AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        CleanupPendingSession(doc, GetDocumentKey(doc));
    }

    private static void CleanupPendingSession(Document doc, string key)
    {
        PendingSessions.TryRemove(key, out _);
        UnsubscribeNativeCommandHandlers(doc);
    }

    private static void UnsubscribeNativeCommandHandlers(Document doc)
    {
        try
        {
            doc.CommandEnded -= OnNativeAlignedDimensionEnded;
            doc.CommandCancelled -= OnNativeAlignedDimensionCancelledOrFailed;
            doc.CommandFailed -= OnNativeAlignedDimensionCancelledOrFailed;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 取消事件订阅失败（无效操作）", ex);
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 取消事件订阅失败（CAD）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 取消事件订阅失败（参数）", ex);
        }
    }

    private static bool TryGetImpliedDimensionId(
        Editor ed,
        out ObjectId dimensionId,
        out ImpliedDimensionContinuationKind continuationKind)
    {
        dimensionId = ObjectId.Null;
        continuationKind = ImpliedDimensionContinuationKind.None;

        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = implied.Value.GetObjectIds();
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        if (ids.Length != 1)
            return false;

        var objectClass = ids[0].ObjectClass;
        if (objectClass == null)
            return false;

        var alignedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(AlignedDimension));
        var rotatedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(RotatedDimension));
        if (!DddDimensionSelectionFilter.IsDimensionClass(objectClass))
            return false;

        continuationKind = objectClass.IsDerivedFrom(alignedClass)
            ? ImpliedDimensionContinuationKind.Aligned
            : objectClass.IsDerivedFrom(rotatedClass)
                ? ImpliedDimensionContinuationKind.Linear
                : ImpliedDimensionContinuationKind.Unsupported;
        dimensionId = ids[0];
        return true;
    }

    private static bool TryGetCurrentSpaceLastEntityId(Document doc, out ObjectId lastEntityId, out string error)
    {
        lastEntityId = ObjectId.Null;
        error = string.Empty;

        try
        {
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId objectId in currentSpace)
                {
                    if (!objectId.IsErased)
                        lastEntityId = objectId;
                }

                tr.Commit();
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取当前空间尾实体失败（无效操作）", ex);
            error = $"读取当前空间尾实体失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取当前空间尾实体失败（CAD）", ex);
            error = $"读取当前空间尾实体失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取当前空间尾实体失败（参数）", ex);
            error = $"读取当前空间尾实体失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryFindNewDimensionAfter(Document doc, ObjectId beforeEntityId, out ObjectId dimensionId, out string error)
    {
        dimensionId = ObjectId.Null;
        error = string.Empty;

        try
        {
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
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
                    if (dbObject is Dimension)
                        dimensionId = objectId;
                }

                tr.Commit();
            }

            if (dimensionId.IsNull)
            {
                error = "未找到由原生 DIMALIGNED 创建的首个标注。";
                return false;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 查找原生首段标注失败（无效操作）", ex);
            error = $"查找原生首段标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 查找原生首段标注失败（CAD）", ex);
            error = $"查找原生首段标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 查找原生首段标注失败（参数）", ex);
            error = $"查找原生首段标注失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryReadInitialDimensionState(
        Document doc,
        ObjectId dimensionId,
        out Point3d firstPoint,
        out Point3d secondPoint,
        out double signedOffset,
        out string error)
    {
        firstPoint = default;
        secondPoint = default;
        signedOffset = 0.0;
        error = string.Empty;

        try
        {
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dbObject = tr.GetObject(dimensionId, OpenMode.ForRead, false);
                if (dbObject is not AlignedDimension dimension)
                {
                    error = "首个标注不是对齐标注，无法继续。";
                    return false;
                }

                firstPoint = dimension.XLine1Point;
                secondPoint = dimension.XLine2Point;
                if (!TryComputeSignedOffset(firstPoint, secondPoint, dimension.DimLinePoint, out signedOffset))
                {
                    error = "无法读取首个对齐标注的基线偏移。";
                    return false;
                }

                tr.Commit();
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取首段标注状态失败（无效操作）", ex);
            error = $"读取首段标注状态失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取首段标注状态失败（CAD）", ex);
            error = $"读取首段标注状态失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取首段标注状态失败（参数）", ex);
            error = $"读取首段标注状态失败：{ex.Message}";
            return false;
        }
    }

    internal static bool TryReadSelectedDimensionContinuationState(
        Document doc,
        ObjectId dimensionId,
        out List<Point3d> points,
        out List<ObjectId> dimensionIds,
        out Point3d selectedFirstPoint,
        out Point3d selectedSecondPoint,
        out double signedOffset,
        out string error)
    {
        points = new List<Point3d>();
        dimensionIds = new List<ObjectId>();
        selectedFirstPoint = default;
        selectedSecondPoint = default;
        signedOffset = 0.0;
        error = string.Empty;

        try
        {
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dbObject = tr.GetObject(dimensionId, OpenMode.ForRead, false);
                if (dbObject is not AlignedDimension dimension)
                {
                    error = "所选对象不是对齐标注，无法继续。";
                    return false;
                }

                var firstPoint = dimension.XLine1Point;
                var secondPoint = dimension.XLine2Point;
                NormalizeAlignedContinuationDirection(ref firstPoint, ref secondPoint);
                if (!TryComputeSignedOffset(firstPoint, secondPoint, dimension.DimLinePoint, out signedOffset))
                {
                    error = "无法读取所选对齐标注的基线偏移。";
                    return false;
                }

                selectedFirstPoint = firstPoint;
                selectedSecondPoint = secondPoint;

                if (!TryCollectSelectedAlignedChainPoints(
                        tr,
                        dimensionId,
                        dimension,
                        signedOffset,
                        firstPoint,
                        secondPoint,
                        out points,
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
            C_toolsDiagnostics.LogNonFatal("F_DA 读取所选标注续接状态失败（无效操作）", ex);
            error = $"读取所选标注续接状态失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取所选标注续接状态失败（CAD）", ex);
            error = $"读取所选标注续接状态失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 读取所选标注续接状态失败（参数）", ex);
            error = $"读取所选标注续接状态失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryCollectSelectedAlignedChainPoints(
        Transaction tr,
        ObjectId dimensionId,
        AlignedDimension selectedDimension,
        double signedOffset,
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        out List<Point3d> points,
        out List<ObjectId> dimensionIds,
        out string error)
    {
        points = new List<Point3d>();
        dimensionIds = new List<ObjectId>();
        error = string.Empty;

        if (!TryNormalizePlanar(selectedSecondPoint - selectedFirstPoint, out var selectedAxis))
        {
            error = "所选对齐标注方向无效，无法继续。";
            return false;
        }

        var referenceNormal = new Vector3d(-selectedAxis.Y, selectedAxis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "所选对齐标注法向无效，无法继续。";
            return false;
        }

        var selectedAnchor = BuildLineAnchor(selectedFirstPoint, selectedSecondPoint, signedOffset);
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

                var candidateAnchor = BuildLineAnchor(candidateFirstPoint, candidateSecondPoint, candidateOffset);
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

        points = SortPoints(points, selectedFirstPoint, selectedAxis);
        if (points.Count < 2)
        {
            error = "未能解析所选对齐标注所在的连续标注链。";
            return false;
        }

        DddDimensionChainHelper.AddDimensionIdsInOrder(
            segments,
            dimensionIds,
            (left, right) => AxisParameter(left.First, selectedFirstPoint, selectedAxis).CompareTo(AxisParameter(right.First, selectedFirstPoint, selectedAxis)));

        return true;
    }

    private static bool IsPointInsideSelectedSegment(
        Point3d axisOrigin,
        Vector3d axisDirection,
        Point3d selectedFirstPoint,
        Point3d selectedSecondPoint,
        Point3d projectedPoint)
    {
        var selectedStartParameter = AxisParameter(selectedFirstPoint, axisOrigin, axisDirection);
        var selectedEndParameter = AxisParameter(selectedSecondPoint, axisOrigin, axisDirection);
        var projectedParameter = AxisParameter(projectedPoint, axisOrigin, axisDirection);
        var minParameter = Math.Min(selectedStartParameter, selectedEndParameter);
        var maxParameter = Math.Max(selectedStartParameter, selectedEndParameter);
        return projectedParameter > minParameter + PointTolerance &&
               projectedParameter < maxParameter - PointTolerance;
    }


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

    private static void AddUniquePoint(List<Point3d> points, Point3d point)
    {
        foreach (var existing in points)
        {
            if (PointsCoincide(existing, point))
                return;
        }

        points.Add(point);
    }

    private static void WriteAvoidanceFailure(Document doc, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            doc.Editor.WriteMessage($"\nC_TOOL：{error}");
    }

    private static bool TryAppendAlignedDimension(
        Document doc,
        Point3d firstPoint,
        Point3d secondPoint,
        double signedOffset,
        out ObjectId createdDimensionId,
        out string error)
    {
        createdDimensionId = ObjectId.Null;
        error = string.Empty;

        if (!TryBuildParallelDimLinePoint(firstPoint, secondPoint, signedOffset, out var dimLinePoint, out error))
            return false;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                using (var dim = new AlignedDimension(firstPoint, secondPoint, dimLinePoint, string.Empty, db.Dimstyle))
                {
                    ApplyDimensionDefaults(dim, db);
                    currentSpace.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    createdDimensionId = dim.ObjectId;
                }

                tr.Commit();
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 追加对齐标注失败（无效操作）", ex);
            error = $"追加对齐标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 追加对齐标注失败（CAD）", ex);
            error = $"追加对齐标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 追加对齐标注失败（参数）", ex);
            error = $"追加对齐标注失败：{ex.Message}";
            return false;
        }
    }

    private static bool TrySplitSelectedAlignedDimension(
        Document doc,
        ObjectId selectedDimensionId,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d splitPoint,
        double signedOffset,
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

        if (!TryBuildParallelDimLinePoint(firstPoint, splitPoint, signedOffset, out var firstDimLinePoint, out error))
            return false;

        if (!TryBuildParallelDimLinePoint(splitPoint, secondPoint, signedOffset, out var secondDimLinePoint, out error))
            return false;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var dbObject = tr.GetObject(selectedDimensionId, OpenMode.ForWrite, false);
                if (dbObject is not AlignedDimension selectedDimension || selectedDimension.IsErased)
                {
                    error = "所选对齐标注已失效，请重新选择。";
                    return false;
                }

                using (var firstDim = new AlignedDimension(firstPoint, splitPoint, firstDimLinePoint, string.Empty, db.Dimstyle))
                {
                    ApplyDimensionDefaults(firstDim, db);
                    firstDim.LayerId = selectedDimension.LayerId;
                    firstDim.RecomputeDimensionBlock(true);
                    currentSpace.AppendEntity(firstDim);
                    tr.AddNewlyCreatedDBObject(firstDim, true);
                    createdIds.Add(firstDim.ObjectId);
                }

                using (var secondDim = new AlignedDimension(splitPoint, secondPoint, secondDimLinePoint, string.Empty, db.Dimstyle))
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
        }
        catch (InvalidOperationException ex)
        {
            CleanupCreatedDimensions(doc, createdIds);
            C_toolsDiagnostics.LogNonFatal("F_DA 打断所选对齐标注失败（无效操作）", ex);
            error = $"打断所选对齐标注失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            CleanupCreatedDimensions(doc, createdIds);
            C_toolsDiagnostics.LogNonFatal("F_DA 打断所选对齐标注失败（CAD）", ex);
            error = $"打断所选对齐标注失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            CleanupCreatedDimensions(doc, createdIds);
            C_toolsDiagnostics.LogNonFatal("F_DA 打断所选对齐标注失败（参数）", ex);
            error = $"打断所选对齐标注失败：{ex.Message}";
            return false;
        }

        if (!TryApplyChainTextAvoidance(doc, new List<Point3d> { firstPoint, splitPoint, secondPoint }, createdIds, signedOffset, out error))
        {
            WriteAvoidanceFailure(doc, error);
            error = string.Empty;
        }

        return true;
    }

    private static bool TryRebuildChain(
        Document doc,
        List<Point3d> points,
        List<ObjectId> existingDimensionIds,
        double signedOffset,
        out List<ObjectId> rebuiltIds,
        out string error)
    {
        rebuiltIds = new List<ObjectId>();
        error = string.Empty;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                for (var index = 0; index < points.Count - 1; index++)
                {
                    var firstPoint = points[index];
                    var secondPoint = points[index + 1];
                    if (!TryBuildParallelDimLinePoint(firstPoint, secondPoint, signedOffset, out var dimLinePoint, out error))
                        return false;

                    using (var dim = new AlignedDimension(firstPoint, secondPoint, dimLinePoint, string.Empty, db.Dimstyle))
                    {
                        ApplyDimensionDefaults(dim, db);
                        currentSpace.AppendEntity(dim);
                        tr.AddNewlyCreatedDBObject(dim, true);
                        rebuiltIds.Add(dim.ObjectId);
                    }
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
            C_toolsDiagnostics.LogNonFatal("F_DA 重建对齐标注链失败（无效操作）", ex);
            error = $"重建对齐标注链失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            CleanupCreatedDimensions(doc, rebuiltIds);
            C_toolsDiagnostics.LogNonFatal("F_DA 重建对齐标注链失败（CAD）", ex);
            error = $"重建对齐标注链失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            CleanupCreatedDimensions(doc, rebuiltIds);
            C_toolsDiagnostics.LogNonFatal("F_DA 重建对齐标注链失败（参数）", ex);
            error = $"重建对齐标注链失败：{ex.Message}";
            return false;
        }

        if (!TryApplyChainTextAvoidance(doc, points, rebuiltIds, signedOffset, out error))
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
            C_toolsDiagnostics.LogNonFatal("F_DA 清理失败（无效操作）", ex);
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 清理失败（CAD）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DA 清理失败（参数）", ex);
        }
    }

    private static void ApplyDimensionDefaults(AlignedDimension dim, Database db)
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

    private static bool TryApplyChainTextAvoidance(
        Document doc,
        IList<Point3d> points,
        IList<ObjectId> dimensionIds,
        double signedOffset,
        out string error)
    {
        return DddDimensionChainTextAvoidanceService.TryApplyAlignedChain(doc, points, dimensionIds, signedOffset, out error);
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

    private static List<Point3d> BuildUpdatedPointList(
        IList<Point3d> points,
        Point3d newPoint,
        Point3d axisOrigin,
        Vector3d axisDirection)
    {
        var updated = new List<Point3d>(points.Count + 1);
        foreach (var point in points)
            updated.Add(point);
        updated.Add(newPoint);
        return SortPoints(updated, axisOrigin, axisDirection);
    }

    private static List<Point3d> SortPoints(List<Point3d> points, Point3d axisOrigin, Vector3d axisDirection)
    {
        points.Sort((left, right) =>
            AxisParameter(left, axisOrigin, axisDirection).CompareTo(AxisParameter(right, axisOrigin, axisDirection)));
        return points;
    }

    private static double AxisParameter(Point3d point, Point3d axisOrigin, Vector3d axisDirection) =>
        Dot2d(point - axisOrigin, axisDirection);

    private static bool TryBuildParallelDimLinePoint(
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
            error = "两测点过近，无法继续连续标注。";
            return false;
        }

        if (Math.Abs(signedOffset) < PointTolerance)
        {
            error = "连续标注基线偏移量无效，请重新执行 F_DA 指定新的标注线位置。";
            return false;
        }

        var midPoint = MidPoint(firstPoint, secondPoint);
        dimLinePoint = midPoint + (perpendicular * signedOffset);
        return true;
    }

    private static Point3d BuildLineAnchor(Point3d firstPoint, Point3d secondPoint, double signedOffset)
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

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static bool AreParallel(Vector3d left, Vector3d right) =>
        Math.Abs(left.DotProduct(right)) >= ParallelDotTolerance;

    private static double Dot2d(Vector3d left, Vector3d right) => (left.X * right.X) + (left.Y * right.Y);

    private static bool PointsCoincide(Point3d left, Point3d right) =>
        left.DistanceTo(right) < PointTolerance;

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

    private readonly struct PendingAlignedSession
    {
        internal PendingAlignedSession(ObjectId beforeEntityId)
        {
            BeforeEntityId = beforeEntityId;
        }

        internal ObjectId BeforeEntityId { get; }
    }

    private enum ImpliedDimensionContinuationKind
    {
        None,
        Aligned,
        Linear,
        Unsupported
    }
}
