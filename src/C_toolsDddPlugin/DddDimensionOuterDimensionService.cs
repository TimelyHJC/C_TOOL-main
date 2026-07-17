using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsDddPlugin;

internal static class DddDimensionOuterDimensionService
{
    private const string SettingsKeyword = "S";
    private const double PointTolerance = 1e-6;
    private const double ChainRowTolerance = 1e-4;
    private const double ParallelDotTolerance = 0.999;
    private const string DuplicateOuterDimensionError = "同位置的外包总尺寸已存在，无需重复生成。";

    internal static void Run(Document doc)
    {
        var ed = doc.Editor;
        var settings = DddOuterDimensionSettingsStore.LoadOrDefault();
        if (!TryGetTargetDimensionIds(doc, ref settings, out var dimensionIds))
            return;

        if (dimensionIds.Count == 1)
        {
            if (!TryCreateOuterDimension(
                    doc,
                    dimensionIds[0],
                    includeWholeChain: true,
                    settings.OffsetDistance,
                    out var createdDimensionId,
                    out var result,
                    out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    ed.WriteMessage($"\nC_TOOL：{error}");
                return;
            }

            TryApplyCreatedDimensionAvoidance(doc, createdDimensionId, ed);

            ed.WriteMessage(
                $"\nC_TOOL：F_DQQ 已基于 {result.SegmentCount} 段连续标注生成外包总尺寸 {FormatMeasurement(result.Measurement)}，并向外偏移 {FormatMeasurement(settings.OffsetDistance)}。");
            return;
        }

        if (!TryCreateOuterDimensionsForSelection(
                doc,
                dimensionIds,
                settings.OffsetDistance,
                out var createdDimensionIds,
                out var batchResult,
                out var batchError))
        {
            if (!string.IsNullOrWhiteSpace(batchError))
                ed.WriteMessage($"\nC_TOOL：{batchError}");
            return;
        }

        TryApplyCreatedDimensionAvoidance(doc, createdDimensionIds, ed);

        var summaryMessage = $"\nC_TOOL：F_DQQ 已按所选连续标注生成 {batchResult.CreatedCount} 条外包总尺寸，并向外偏移 {FormatMeasurement(settings.OffsetDistance)}。";
        if (batchResult.SkippedCount > 0)
            summaryMessage += $" 其中 {batchResult.SkippedCount} 组因已存在同位置外包尺寸而跳过。";
        if (batchResult.FailedCount > 0)
        {
            summaryMessage += !string.IsNullOrWhiteSpace(batchResult.FirstFailureReason)
                ? $" 另有 {batchResult.FailedCount} 组未生成，首个原因：{batchResult.FirstFailureReason}"
                : $" 另有 {batchResult.FailedCount} 组未生成。";
        }

        ed.WriteMessage(summaryMessage);
    }

    private static bool TryCreateOuterDimension(
        Document doc,
        ObjectId seedId,
        bool includeWholeChain,
        double offsetDistance,
        out ObjectId createdDimensionId,
        out OuterDimensionCreationResult result,
        out string error)
    {
        createdDimensionId = ObjectId.Null;
        result = default;
        error = string.Empty;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var seedObject = tr.GetObject(seedId, OpenMode.ForRead, false);
                if (seedObject is not Dimension seedDimension)
                {
                    error = "所选对象不是标注。";
                    return false;
                }

                if (!TryBuildDimensionInfo(seedId, seedDimension, out var seedInfo, out error))
                    return false;

                Point3d firstPoint;
                Point3d secondPoint;
                var segmentCount = 1;

                if (includeWholeChain)
                {
                    var segments = new List<DddDimensionChainSegment>
                    {
                        new(seedInfo.FirstPoint, seedInfo.SecondPoint, seedId)
                    };
                    if (!TryCollectChainInfos(tr, seedDimension, seedInfo, segments, out error))
                        return false;

                    var chainPoints = CollectOrderedChainPoints(segments, seedInfo.FirstPoint, seedInfo.Axis);
                    if (chainPoints.Count < 2)
                    {
                        error = "未能识别所选标注所在的连续尺寸链。";
                        return false;
                    }

                    firstPoint = chainPoints[0];
                    secondPoint = chainPoints[^1];
                    segmentCount = segments.Count;
                }
                else
                {
                    firstPoint = seedInfo.FirstPoint;
                    secondPoint = seedInfo.SecondPoint;
                }

                if (PointsCoincide(firstPoint, secondPoint))
                {
                    error = "连续尺寸链的首尾点重合，无法生成外包尺寸。";
                    return false;
                }

                if (!TryCreateOuterDimensionCore(
                        tr,
                        seedDimension,
                        seedInfo,
                        firstPoint,
                        secondPoint,
                        offsetDistance,
                        segmentCount,
                        out createdDimensionId,
                        out result,
                        out error))
                {
                    return false;
                }

                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DQQ 生成外包尺寸失败（无效操作）", ex);
            error = $"F_DQQ 执行失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DQQ 生成外包尺寸失败（CAD）", ex);
            error = $"F_DQQ 执行失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DQQ 生成外包尺寸失败（参数）", ex);
            error = $"F_DQQ 执行失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryCreateOuterDimensionsForSelection(
        Document doc,
        IReadOnlyList<ObjectId> dimensionIds,
        double offsetDistance,
        out List<ObjectId> createdDimensionIds,
        out BatchOuterDimensionResult result,
        out string error)
    {
        createdDimensionIds = new List<ObjectId>();
        result = default;
        error = string.Empty;

        var createdCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var firstFailureReason = string.Empty;
        var requestCount = 0;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (!TryBuildSelectionOuterDimensionRequests(tr, dimensionIds, out var requests, out error))
                    return false;

                requestCount = requests.Count;

                foreach (var request in requests)
                {
                    if (TryCreateOuterDimensionCore(
                            tr,
                            request.SeedDimension,
                            request.SeedInfo,
                            request.FirstPoint,
                            request.SecondPoint,
                            offsetDistance,
                            request.SegmentCount,
                            out var createdDimensionId,
                            out _,
                            out var itemError))
                    {
                        createdCount++;
                        if (createdDimensionId.IsValid)
                            createdDimensionIds.Add(createdDimensionId);
                        continue;
                    }

                    if (string.Equals(itemError, DuplicateOuterDimensionError, StringComparison.Ordinal))
                    {
                        skippedCount++;
                        continue;
                    }

                    failedCount++;
                    if (string.IsNullOrWhiteSpace(firstFailureReason) && !string.IsNullOrWhiteSpace(itemError))
                        firstFailureReason = itemError;
                }

                result = new BatchOuterDimensionResult(createdCount, skippedCount, failedCount, firstFailureReason);
                if (createdCount > 0)
                {
                    tr.Commit();
                    return true;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DQQ 批量生成外包尺寸失败（无效操作）", ex);
            error = $"F_DQQ 执行失败：{ex.Message}";
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DQQ 批量生成外包尺寸失败（CAD）", ex);
            error = $"F_DQQ 执行失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DQQ 批量生成外包尺寸失败（参数）", ex);
            error = $"F_DQQ 执行失败：{ex.Message}";
            return false;
        }

        if (requestCount > 0 && skippedCount == requestCount)
        {
            error = "所选标注对应的外包总尺寸已全部存在，无需重复生成。";
            return false;
        }

        error = !string.IsNullOrWhiteSpace(firstFailureReason)
            ? firstFailureReason
            : "未能按所选标注生成外包总尺寸。";
        return false;
    }

    private static void TryApplyCreatedDimensionAvoidance(Document doc, ObjectId createdDimensionId, Editor ed)
    {
        if (!createdDimensionId.IsValid || createdDimensionId.IsErased)
            return;

        TryApplyCreatedDimensionAvoidance(doc, new List<ObjectId> { createdDimensionId }, ed);
    }

    private static void TryApplyCreatedDimensionAvoidance(Document doc, IReadOnlyList<ObjectId> createdDimensionIds, Editor ed)
    {
        if (createdDimensionIds.Count == 0)
            return;

        if (!DddDimensionTextAvoidanceService.TryApplyDimensionIds(
                doc,
                new List<ObjectId>(createdDimensionIds),
                DddPluginCommandIds.DimOuter,
                out _,
                out _,
                out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
        }
    }

    private static bool TryGetTargetDimensionIds(
        Document doc,
        ref DddOuterDimensionSettingsDto settings,
        out List<ObjectId> dimensionIds)
    {
        var ed = doc.Editor;
        dimensionIds = new List<ObjectId>();

        if (TryGetImpliedDimensionIds(ed, out dimensionIds))
            return true;

        while (true)
        {
            var currentSettings = settings;
            var options = new PromptSelectionOptions
            {
                MessageForAdding = GetSelectionPromptMessage(currentSettings)
            };

            options.SetKeywords("[设置(S)]", SettingsKeyword);
            options.KeywordInput += (_, e) =>
            {
                var keyword = NormalizeKeyword(e.Input);
                if (!string.Equals(keyword, SettingsKeyword, StringComparison.OrdinalIgnoreCase))
                    return;

                ShowSettingsDialog(doc, ref currentSettings);
                options.MessageForAdding = GetSelectionPromptMessage(currentSettings);
                e.SetErrorMessage(GetSelectionPromptMessage(currentSettings));
            };

            var result = ed.GetSelection(options, CreateDimensionSelectionFilter());
            settings = currentSettings;
            if (result.Status == PromptStatus.OK && result.Value != null)
            {
                dimensionIds = CollectLinearAlignedDimensionIds(result.Value.GetObjectIds());
                if (dimensionIds.Count > 0)
                    return true;

                ed.WriteMessage("\nC_TOOL：请选择线性标注或对齐标注。");
                continue;
            }

            if (result.Status == PromptStatus.Keyword)
                continue;

            if (result.Status == PromptStatus.None)
                return false;

            if (result.Status == PromptStatus.Cancel)
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_DQQ_Cancelled}");
            else
                ed.WriteMessage("\nC_TOOL：F_DQQ 未能读取标注选择。");
            return false;
        }
    }

    private static bool TryGetImpliedDimensionIds(Editor ed, out List<ObjectId> dimensionIds)
    {
        dimensionIds = new List<ObjectId>();

        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = implied.Value.GetObjectIds();
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        if (ids.Length == 0)
            return false;

        dimensionIds = CollectLinearAlignedDimensionIds(ids);

        return dimensionIds.Count > 0;
    }

    private static List<ObjectId> CollectLinearAlignedDimensionIds(IEnumerable<ObjectId> ids)
    {
        var seen = new HashSet<ObjectId>();
        var dimensionIds = new List<ObjectId>();
        foreach (var id in ids)
        {
            if (id.IsNull || !seen.Add(id))
                continue;

            if (DddDimensionSelectionFilter.IsLinearOrAlignedDimension(id))
                dimensionIds.Add(id);
        }

        return dimensionIds;
    }

    private static SelectionFilter CreateDimensionSelectionFilter() =>
        new(new[]
        {
            new TypedValue((int)DxfCode.Start, "DIMENSION")
        });

    private static bool TryCollectChainInfos(
        Transaction tr,
        Dimension seedDimension,
        DimensionInfo seedInfo,
        List<DddDimensionChainSegment> segments,
        out string error)
    {
        error = string.Empty;
        var ownerRecord = (BlockTableRecord)tr.GetObject(seedDimension.OwnerId, OpenMode.ForRead);

        var added = true;
        while (added)
        {
            added = false;
            foreach (ObjectId entityId in ownerRecord)
            {
                if (!entityId.IsValid || entityId.IsErased || entityId == seedInfo.DimensionId)
                    continue;

                var dbObject = tr.GetObject(entityId, OpenMode.ForRead, false);
                if (dbObject is not Dimension candidate)
                    continue;

                if (!TryBuildDimensionInfo(entityId, candidate, out var candidateInfo, out _))
                    continue;

                if (candidateInfo.Kind != seedInfo.Kind || !AreParallel(seedInfo.Axis, candidateInfo.Axis))
                    continue;

                var rowDistance = Math.Abs(Dot2d(candidateInfo.LineAnchor - seedInfo.LineAnchor, seedInfo.ReferenceNormal));
                if (rowDistance > ChainRowTolerance)
                    continue;

                if (!DddDimensionChainHelper.SharesAnyEndpoint(
                        candidateInfo.FirstPoint,
                        candidateInfo.SecondPoint,
                        segments,
                        PointTolerance))
                {
                    continue;
                }

                if (DddDimensionChainHelper.ContainsSegment(
                        segments,
                        candidateInfo.FirstPoint,
                        candidateInfo.SecondPoint,
                        PointTolerance))
                {
                    continue;
                }

                segments.Add(new DddDimensionChainSegment(candidateInfo.FirstPoint, candidateInfo.SecondPoint, entityId));
                added = true;
            }
        }

        return true;
    }

    private static List<Point3d> CollectOrderedChainPoints(
        IEnumerable<DddDimensionChainSegment> segments,
        Point3d axisOrigin,
        Vector3d axis)
    {
        var points = new List<Point3d>();
        foreach (var segment in segments)
        {
            AddUniquePoint(points, segment.First);
            AddUniquePoint(points, segment.Second);
        }

        points.Sort((left, right) =>
            GetAxisCoordinate(left, axisOrigin, axis).CompareTo(GetAxisCoordinate(right, axisOrigin, axis)));
        return points;
    }

    private static bool HasExistingOuterDimension(
        Transaction tr,
        ObjectId ownerId,
        DimensionInfo seedInfo,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d targetDimLinePoint)
    {
        var ownerRecord = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
        foreach (ObjectId entityId in ownerRecord)
        {
            if (!entityId.IsValid || entityId.IsErased || entityId == seedInfo.DimensionId)
                continue;

            var dbObject = tr.GetObject(entityId, OpenMode.ForRead, false);
            if (dbObject is not Dimension candidate)
                continue;

            if (!TryBuildDimensionInfo(entityId, candidate, out var candidateInfo, out _))
                continue;

            if (candidateInfo.Kind != seedInfo.Kind || !AreParallel(seedInfo.Axis, candidateInfo.Axis))
                continue;

            if (!HasSameEndpoints(candidateInfo.FirstPoint, candidateInfo.SecondPoint, firstPoint, secondPoint))
                continue;

            var rowDistance = Math.Abs(Dot2d(candidateInfo.LineAnchor - targetDimLinePoint, seedInfo.ReferenceNormal));
            if (rowDistance <= ChainRowTolerance)
                return true;
        }

        return false;
    }

    private static bool TryCreateOuterDimensionCore(
        Transaction tr,
        Dimension seedDimension,
        DimensionInfo seedInfo,
        Point3d firstPoint,
        Point3d secondPoint,
        double offsetDistance,
        int segmentCount,
        out ObjectId createdDimensionId,
        out OuterDimensionCreationResult result,
        out string error)
    {
        createdDimensionId = ObjectId.Null;
        result = default;
        error = string.Empty;

        var targetOffset = seedInfo.RowOffset + offsetDistance;
        var targetDimLinePoint = MidPoint(firstPoint, secondPoint) + (seedInfo.OutwardNormal * targetOffset);

        if (HasExistingOuterDimension(tr, seedDimension.OwnerId, seedInfo, firstPoint, secondPoint, targetDimLinePoint))
        {
            error = DuplicateOuterDimensionError;
            return false;
        }

        var ownerRecord = (BlockTableRecord)tr.GetObject(seedDimension.OwnerId, OpenMode.ForWrite);
        using var createdDimension = CreateOuterDimension(seedDimension, seedInfo.Kind, firstPoint, secondPoint, targetDimLinePoint, out error);
        if (createdDimension == null)
            return false;

        ownerRecord.AppendEntity(createdDimension);
        tr.AddNewlyCreatedDBObject(createdDimension, true);
        createdDimension.RecomputeDimensionBlock(true);
        createdDimensionId = createdDimension.ObjectId;

        result = new OuterDimensionCreationResult(segmentCount, Math.Abs(Dot2d(secondPoint - firstPoint, seedInfo.Axis)));
        return true;
    }

    private static bool TryBuildSelectionOuterDimensionRequests(
        Transaction tr,
        IReadOnlyList<ObjectId> dimensionIds,
        out List<SelectionOuterDimensionRequest> requests,
        out string error)
    {
        requests = new List<SelectionOuterDimensionRequest>();
        error = string.Empty;

        var selectedItems = new List<SelectionDimensionItem>(dimensionIds.Count);
        foreach (var dimensionId in dimensionIds)
        {
            var seedObject = tr.GetObject(dimensionId, OpenMode.ForRead, false);
            if (seedObject is not Dimension seedDimension)
            {
                error = "所选对象不是标注。";
                return false;
            }

            if (!TryBuildDimensionInfo(dimensionId, seedDimension, out var seedInfo, out error))
                return false;

            selectedItems.Add(new SelectionDimensionItem(seedDimension, seedInfo));
        }

        if (selectedItems.Count == 0)
        {
            error = "未找到可处理的标注。";
            return false;
        }

        var rowBuckets = BuildSelectionRowBuckets(selectedItems);
        foreach (var bucket in rowBuckets)
        {
            foreach (var request in BuildSelectionRequestsFromBucket(bucket))
                requests.Add(request);
        }

        if (requests.Count == 0)
        {
            error = "未能识别所选标注中的连续尺寸组。";
            return false;
        }

        return true;
    }

    private static List<List<SelectionDimensionItem>> BuildSelectionRowBuckets(IReadOnlyList<SelectionDimensionItem> items)
    {
        var buckets = new List<List<SelectionDimensionItem>>();
        foreach (var item in items)
        {
            var added = false;
            foreach (var bucket in buckets)
            {
                if (!CanShareSelectionRow(bucket[0], item))
                    continue;

                bucket.Add(item);
                added = true;
                break;
            }

            if (!added)
                buckets.Add(new List<SelectionDimensionItem> { item });
        }

        return buckets;
    }

    private static IEnumerable<SelectionOuterDimensionRequest> BuildSelectionRequestsFromBucket(
        IReadOnlyList<SelectionDimensionItem> bucket)
    {
        var requests = new List<SelectionOuterDimensionRequest>();
        if (bucket.Count == 0)
            return requests;

        var axis = bucket[0].Info.Axis;
        var pending = new List<SelectionSegmentItem>(bucket.Count);
        foreach (var item in bucket)
        {
            var firstPoint = item.Info.FirstPoint;
            var secondPoint = item.Info.SecondPoint;
            NormalizeSegmentDirection(axis, ref firstPoint, ref secondPoint);
            pending.Add(new SelectionSegmentItem(item.Dimension, item.Info, firstPoint, secondPoint));
        }

        while (pending.Count > 0)
        {
            var currentSegments = new List<DddDimensionChainSegment>
            {
                new(pending[0].FirstPoint, pending[0].SecondPoint, pending[0].Info.DimensionId)
            };
            var componentItems = new List<SelectionSegmentItem> { pending[0] };
            pending.RemoveAt(0);

            var added = true;
            while (added)
            {
                added = false;
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    var candidate = pending[index];
                    if (!DddDimensionChainHelper.SharesAnyEndpoint(
                            candidate.FirstPoint,
                            candidate.SecondPoint,
                            currentSegments,
                            PointTolerance))
                    {
                        continue;
                    }

                    componentItems.Add(candidate);
                    currentSegments.Add(new DddDimensionChainSegment(candidate.FirstPoint, candidate.SecondPoint, candidate.Info.DimensionId));
                    pending.RemoveAt(index);
                    added = true;
                }
            }

            var points = CollectSelectionComponentPoints(componentItems, axis);
            if (points.Count < 2)
                continue;

            var componentSeed = componentItems[0];
            var firstPoint = points[0];
            var secondPoint = points[^1];
            NormalizeSegmentDirection(componentSeed.Info.Axis, ref firstPoint, ref secondPoint);

            requests.Add(new SelectionOuterDimensionRequest(
                componentSeed.Dimension,
                componentSeed.Info,
                firstPoint,
                secondPoint,
                componentItems.Count));
        }

        return requests;
    }

    private static List<Point3d> CollectSelectionComponentPoints(
        IEnumerable<SelectionSegmentItem> componentItems,
        Vector3d axis)
    {
        Point3d? axisOrigin = null;
        var points = new List<Point3d>();
        foreach (var item in componentItems)
        {
            axisOrigin ??= item.FirstPoint;
            AddUniquePoint(points, item.FirstPoint);
            AddUniquePoint(points, item.SecondPoint);
        }

        if (axisOrigin == null)
            return points;

        points.Sort((left, right) => GetAxisCoordinate(left, axisOrigin.Value, axis).CompareTo(GetAxisCoordinate(right, axisOrigin.Value, axis)));
        return points;
    }

    private static bool CanShareSelectionRow(SelectionDimensionItem left, SelectionDimensionItem right)
    {
        if (left.Dimension.OwnerId != right.Dimension.OwnerId)
            return false;

        if (left.Info.Kind != right.Info.Kind || !AreParallel(left.Info.Axis, right.Info.Axis))
            return false;

        if (Dot2d(left.Info.OutwardNormal, right.Info.OutwardNormal) < ParallelDotTolerance)
            return false;

        var rowDistance = Math.Abs(Dot2d(right.Info.LineAnchor - left.Info.LineAnchor, left.Info.ReferenceNormal));
        return rowDistance <= ChainRowTolerance;
    }

    private static Dimension? CreateOuterDimension(
        Dimension seedDimension,
        SupportedDimensionKind kind,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d dimLinePoint,
        out string error)
    {
        error = string.Empty;

        switch (kind)
        {
            case SupportedDimensionKind.Rotated when seedDimension is RotatedDimension rotatedSeed:
            {
                var created = rotatedSeed.Clone() as RotatedDimension;
                if (created == null)
                {
                    error = "无法创建线性外包尺寸。";
                    return null;
                }

                created.XLine1Point = firstPoint;
                created.XLine2Point = secondPoint;
                created.DimLinePoint = dimLinePoint;
                PrepareCreatedDimension(created);
                return created;
            }
            case SupportedDimensionKind.Aligned when seedDimension is AlignedDimension alignedSeed:
            {
                var created = alignedSeed.Clone() as AlignedDimension;
                if (created == null)
                {
                    error = "无法创建对齐外包尺寸。";
                    return null;
                }

                created.XLine1Point = firstPoint;
                created.XLine2Point = secondPoint;
                created.DimLinePoint = dimLinePoint;
                PrepareCreatedDimension(created);
                return created;
            }
            default:
                error = "F_DQQ 仅支持线性标注与对齐标注。";
                return null;
        }
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

    private static bool TryBuildDimensionInfo(
        ObjectId dimensionId,
        Dimension dimension,
        out DimensionInfo info,
        out string error)
    {
        info = default;
        error = string.Empty;

        switch (dimension)
        {
            case RotatedDimension rotatedDimension:
                return TryBuildRotatedDimensionInfo(dimensionId, rotatedDimension, out info, out error);
            case AlignedDimension alignedDimension:
                return TryBuildAlignedDimensionInfo(dimensionId, alignedDimension, out info, out error);
            default:
                error = "F_DQQ 仅支持线性标注与对齐标注。";
                return false;
        }
    }

    private static bool TryBuildRotatedDimensionInfo(
        ObjectId dimensionId,
        RotatedDimension dimension,
        out DimensionInfo info,
        out string error)
    {
        info = default;
        error = string.Empty;

        var axisSource = new Vector3d(Math.Cos(dimension.Rotation), Math.Sin(dimension.Rotation), 0.0);
        if (!TryNormalizePlanar(axisSource, out var axis))
        {
            error = "所选线性标注方向无效，无法生成外包尺寸。";
            return false;
        }

        var referenceNormal = new Vector3d(-axis.Y, axis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "所选线性标注法向无效，无法生成外包尺寸。";
            return false;
        }

        var firstPoint = dimension.XLine1Point;
        var secondPoint = dimension.XLine2Point;
        NormalizeSegmentDirection(axis, ref firstPoint, ref secondPoint);
        BuildOffsetInfo(firstPoint, secondPoint, dimension.DimLinePoint, axis, referenceNormal, out var lineAnchor, out var outwardNormal, out var rowOffset);

        info = new DimensionInfo(
            dimensionId,
            SupportedDimensionKind.Rotated,
            firstPoint,
            secondPoint,
            axis,
            referenceNormal,
            outwardNormal,
            lineAnchor,
            rowOffset);
        return true;
    }

    private static bool TryBuildAlignedDimensionInfo(
        ObjectId dimensionId,
        AlignedDimension dimension,
        out DimensionInfo info,
        out string error)
    {
        info = default;
        error = string.Empty;

        var firstPoint = dimension.XLine1Point;
        var secondPoint = dimension.XLine2Point;
        if (!TryNormalizePlanar(secondPoint - firstPoint, out var axis))
        {
            error = "所选对齐标注方向无效，无法生成外包尺寸。";
            return false;
        }

        var referenceNormal = new Vector3d(-axis.Y, axis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "所选对齐标注法向无效，无法生成外包尺寸。";
            return false;
        }

        NormalizeSegmentDirection(axis, ref firstPoint, ref secondPoint);
        BuildOffsetInfo(firstPoint, secondPoint, dimension.DimLinePoint, axis, referenceNormal, out var lineAnchor, out var outwardNormal, out var rowOffset);

        info = new DimensionInfo(
            dimensionId,
            SupportedDimensionKind.Aligned,
            firstPoint,
            secondPoint,
            axis,
            referenceNormal,
            outwardNormal,
            lineAnchor,
            rowOffset);
        return true;
    }

    private static void BuildOffsetInfo(
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d dimLinePoint,
        Vector3d axis,
        Vector3d referenceNormal,
        out Point3d lineAnchor,
        out Vector3d outwardNormal,
        out double rowOffset)
    {
        var midPoint = MidPoint(firstPoint, secondPoint);
        lineAnchor = ProjectPointOntoLine(midPoint, dimLinePoint, axis);
        var signedOffset = Dot2d(lineAnchor - midPoint, referenceNormal);
        outwardNormal = Math.Abs(signedOffset) > PointTolerance
            ? (signedOffset >= 0.0 ? referenceNormal : -referenceNormal)
            : referenceNormal;
        rowOffset = Math.Abs(signedOffset);
    }

    private static bool HasSameEndpoints(
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d targetFirstPoint,
        Point3d targetSecondPoint)
    {
        return (PointsCoincide(firstPoint, targetFirstPoint) && PointsCoincide(secondPoint, targetSecondPoint)) ||
               (PointsCoincide(firstPoint, targetSecondPoint) && PointsCoincide(secondPoint, targetFirstPoint));
    }

    private static void NormalizeSegmentDirection(Vector3d axis, ref Point3d firstPoint, ref Point3d secondPoint)
    {
        if (GetAxisCoordinate(secondPoint, firstPoint, axis) < 0.0)
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

    private static Point3d ProjectPointOntoLine(Point3d point, Point3d linePoint, Vector3d axis)
    {
        var delta = point - linePoint;
        return linePoint + (axis * delta.DotProduct(axis));
    }

    private static bool TryNormalizePlanar(Vector3d vector, out Vector3d normalized)
    {
        normalized = new Vector3d(vector.X, vector.Y, 0.0);
        if (normalized.Length <= PointTolerance)
            return false;

        normalized = normalized.GetNormal();
        return true;
    }

    private static double GetAxisCoordinate(Point3d point, Point3d origin, Vector3d axis) =>
        Dot2d(point - origin, axis);

    private static bool AreParallel(Vector3d left, Vector3d right) =>
        Math.Abs(left.DotProduct(right)) >= ParallelDotTolerance;

    private static double Dot2d(Vector3d left, Vector3d right) =>
        (left.X * right.X) + (left.Y * right.Y);

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static bool PointsCoincide(Point3d left, Point3d right) =>
        left.DistanceTo(right) < PointTolerance;

    private static string FormatMeasurement(double measurement) =>
        measurement.ToString("0.###", CultureInfo.InvariantCulture);

    private static string GetSelectionPromptMessage(DddOuterDimensionSettingsDto settings) =>
        $"\nC_TOOL：选择线性/对齐标注生成外包总尺寸，偏移量 {FormatMeasurement(settings.OffsetDistance)} [设置(S)]：";

    private static string NormalizeKeyword(string? keyword) =>
        (keyword ?? "").Trim();

    private static void ShowSettingsDialog(Document doc, ref DddOuterDimensionSettingsDto settings)
    {
        var ed = doc.Editor;

        try
        {
            var window = new DddOuterDimensionSettingsWindow(settings);
            var accepted = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                true);

            if (accepted != true || window.SavedSettings == null)
                return;

            if (!TrySaveSettings(ed, window.SavedSettings))
                return;

            settings = window.SavedSettings;
            ed.WriteMessage($"\nC_TOOL：F_DQQ 设置已更新，向外偏移量 {FormatMeasurement(settings.OffsetDistance)}。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DQQ 设置失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：打开 F_DQQ 设置失败：{ex.Message}");
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DQQ 设置失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：打开 F_DQQ 设置失败：{ex.Message}");
        }
    }

    private static bool TrySaveSettings(Editor ed, DddOuterDimensionSettingsDto settings)
    {
        try
        {
            DddOuterDimensionSettingsStore.Save(settings);
            return true;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DQQ 设置失败（路径参数）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DQQ 设置失败：{ex.Message}");
            return false;
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DQQ 设置失败（路径过长）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DQQ 设置失败：{ex.Message}");
            return false;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DQQ 设置失败（路径格式）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DQQ 设置失败：{ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DQQ 设置失败（权限）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DQQ 设置失败：{ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DQQ 设置失败", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DQQ 设置失败：{ex.Message}");
            return false;
        }
    }

    private readonly struct DimensionInfo
    {
        internal DimensionInfo(
            ObjectId dimensionId,
            SupportedDimensionKind kind,
            Point3d firstPoint,
            Point3d secondPoint,
            Vector3d axis,
            Vector3d referenceNormal,
            Vector3d outwardNormal,
            Point3d lineAnchor,
            double rowOffset)
        {
            DimensionId = dimensionId;
            Kind = kind;
            FirstPoint = firstPoint;
            SecondPoint = secondPoint;
            Axis = axis;
            ReferenceNormal = referenceNormal;
            OutwardNormal = outwardNormal;
            LineAnchor = lineAnchor;
            RowOffset = rowOffset;
        }

        internal ObjectId DimensionId { get; }

        internal SupportedDimensionKind Kind { get; }

        internal Point3d FirstPoint { get; }

        internal Point3d SecondPoint { get; }

        internal Vector3d Axis { get; }

        internal Vector3d ReferenceNormal { get; }

        internal Vector3d OutwardNormal { get; }

        internal Point3d LineAnchor { get; }

        internal double RowOffset { get; }
    }

    private readonly struct OuterDimensionCreationResult
    {
        internal OuterDimensionCreationResult(int segmentCount, double measurement)
        {
            SegmentCount = segmentCount;
            Measurement = measurement;
        }

        internal int SegmentCount { get; }

        internal double Measurement { get; }
    }

    private readonly struct BatchOuterDimensionResult
    {
        internal BatchOuterDimensionResult(int createdCount, int skippedCount, int failedCount, string firstFailureReason)
        {
            CreatedCount = createdCount;
            SkippedCount = skippedCount;
            FailedCount = failedCount;
            FirstFailureReason = firstFailureReason;
        }

        internal int CreatedCount { get; }

        internal int SkippedCount { get; }

        internal int FailedCount { get; }

        internal string FirstFailureReason { get; }
    }

    private readonly struct SelectionDimensionItem
    {
        internal SelectionDimensionItem(Dimension dimension, DimensionInfo info)
        {
            Dimension = dimension;
            Info = info;
        }

        internal Dimension Dimension { get; }

        internal DimensionInfo Info { get; }
    }

    private readonly struct SelectionSegmentItem
    {
        internal SelectionSegmentItem(
            Dimension dimension,
            DimensionInfo info,
            Point3d firstPoint,
            Point3d secondPoint)
        {
            Dimension = dimension;
            Info = info;
            FirstPoint = firstPoint;
            SecondPoint = secondPoint;
        }

        internal Dimension Dimension { get; }

        internal DimensionInfo Info { get; }

        internal Point3d FirstPoint { get; }

        internal Point3d SecondPoint { get; }
    }

    private readonly struct SelectionOuterDimensionRequest
    {
        internal SelectionOuterDimensionRequest(
            Dimension seedDimension,
            DimensionInfo seedInfo,
            Point3d firstPoint,
            Point3d secondPoint,
            int segmentCount)
        {
            SeedDimension = seedDimension;
            SeedInfo = seedInfo;
            FirstPoint = firstPoint;
            SecondPoint = secondPoint;
            SegmentCount = segmentCount;
        }

        internal Dimension SeedDimension { get; }

        internal DimensionInfo SeedInfo { get; }

        internal Point3d FirstPoint { get; }

        internal Point3d SecondPoint { get; }

        internal int SegmentCount { get; }
    }

    private enum SupportedDimensionKind
    {
        Rotated,
        Aligned
    }
}
