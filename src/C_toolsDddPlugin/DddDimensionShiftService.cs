using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using C_toolsShared;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddDimensionShiftService
{
    private const double PointTolerance = 1e-6;
    private const double ChainRowTolerance = 1e-4;
    private const double ParallelDotTolerance = 0.999;

    internal static void Run(Document doc) => Run(doc, ShiftDirection.Vertical);

    internal static void RunHorizontal(Document doc) => Run(doc, ShiftDirection.Horizontal);

    private static void Run(Document doc, ShiftDirection direction)
    {
        var context = CreateCommandContext(direction);
        if (!TryGetSeedEntityId(
                doc,
                context,
                out var seedId,
                out var seedKind,
                out var relatedId,
                out var selectedDimensionIds,
                out var selectedLeaderIds))
            return;

        switch (seedKind)
        {
            case ShiftSeedKind.Dimension:
                RunDimensionShift(doc, seedId, context, selectedDimensionIds);
                return;
            case ShiftSeedKind.Text:
                RunTextShift(doc, seedId, relatedId, context);
                return;
            case ShiftSeedKind.Leader:
            case ShiftSeedKind.MLeader:
                RunLeaderShift(doc, seedId, context, selectedLeaderIds: selectedLeaderIds);
                return;
            default:
                WriteCommandMessage(doc.Editor, context, "\nC_TOOL：F_DS 当前仅支持线性/对齐标注、文字、引线或多重引线。");
                return;
        }
    }

    private static void RunTextShift(Document doc, ObjectId textId, ObjectId relatedLeaderId, ShiftCommandContext context)
    {
        var ed = doc.Editor;
        if (TryResolveLeaderIdForText(doc, textId, relatedLeaderId, context, out var leaderId, out var resolveError))
        {
            RunLeaderShift(doc, leaderId, context, explicitTextId: textId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(resolveError))
        {
            ed.WriteMessage($"\nC_TOOL：{resolveError}");
            return;
        }

        if (!TryCreateTextSession(doc, textId, context, out var session, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        using (session)
        {
            ed.WriteMessage("\nC_TOOL：拖拽文字新位置，单击确认。");

            if (!TrySetPreviewVisibility(doc, session.PreviewVisibilityById, visible: false, context, out error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                return;
            }

            try
            {
                var jig = new TextShiftDrawJig(session, GetTextShiftPrompt(context));
                var dragResult = ed.Drag(jig);
                if (dragResult.Status != PromptStatus.OK)
                {
                    WriteCommandMessage(ed, context, $"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionShift.Cancelled}");
                    return;
                }

                if (Math.Abs(jig.Offset) <= PointTolerance)
                {
                    ed.WriteMessage("\nC_TOOL：文字位置未变。");
                    return;
                }

                if (!TryApplyTextOffset(doc, session, jig.Offset, context, out error))
                {
                    ed.WriteMessage($"\nC_TOOL：{error}");
                    return;
                }

                WriteCommandMessage(ed, context, "\nC_TOOL：F_DS 已调整当前文字位置。");
            }
            finally
            {
                if (!TryRestorePreviewVisibility(doc, session.PreviewVisibilityById, context, out var restoreError))
                    ed.WriteMessage($"\nC_TOOL：{restoreError}");
            }
        }
    }

    private static void RunDimensionShift(
        Document doc,
        ObjectId seedId,
        ShiftCommandContext context,
        IReadOnlyCollection<ObjectId>? selectedDimensionIds)
    {
        var ed = doc.Editor;
        if (!TryCreateDimensionSession(doc, seedId, context, selectedDimensionIds, out var session, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        using (session)
        {
            var groupLabel = GetDimensionGroupLabel(session.GroupingMode);
            if (ShouldAlignSelectedDimensionLines(session, context))
            {
                ed.WriteMessage(
                    $"\nC_TOOL：已识别 {session.Items.Count} 个{groupLabel}，先将标注横线对齐到同一水平线，再拖拽确认位置。");
            }
            else if (context.IsHorizontal)
            {
                ed.WriteMessage(session.Items.Count > 1
                    ? $"\nC_TOOL：已识别 {session.Items.Count} 个{groupLabel}，拖拽预览后单击确认新文字位置。"
                    : "\nC_TOOL：拖拽预览当前标注文字的新位置，单击确认。");
            }
            else
            {
                ed.WriteMessage(session.Items.Count > 1
                    ? $"\nC_TOOL：已识别 {session.Items.Count} 个{groupLabel}，拖拽预览后单击确认新位置。"
                    : "\nC_TOOL：拖拽预览当前标注的新位置，单击确认。");
            }

            if (!TrySetPreviewVisibility(doc, session.PreviewVisibilityById, visible: false, context, out error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                return;
            }

            try
            {
                var jig = new DimensionShiftDrawJig(session, context, GetDimensionShiftPrompt(context));
                var dragResult = ed.Drag(jig);
                if (dragResult.Status != PromptStatus.OK)
                {
                    WriteCommandMessage(ed, context, $"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionShift.Cancelled}");
                    return;
                }

                if (Math.Abs(jig.Offset) <= PointTolerance && !HasSelectedDimensionLineAlignmentDelta(session, context))
                {
                    ed.WriteMessage(context.IsHorizontal
                        ? "\nC_TOOL：标注文字位置未变化。"
                        : "\nC_TOOL：标注位置未变化。");
                    return;
                }

                if (!TryApplyOffset(doc, session, jig.Offset, context, out var movedCount, out error))
                {
                    ed.WriteMessage($"\nC_TOOL：{error}");
                    return;
                }

                if (!TrySyncDimensionTextAvoidance(doc, session, context, out error))
                {
                    ed.WriteMessage($"\nC_TOOL：{error}");
                    return;
                }

                if (context.IsHorizontal)
                {
                    WriteCommandMessage(ed, context, movedCount > 1
                        ? $"\nC_TOOL：F_DS 已调整 {movedCount} 个{groupLabel}的文字位置。"
                        : "\nC_TOOL：F_DS 已调整当前标注文字位置。");
                }
                else
                {
                    WriteCommandMessage(ed, context, movedCount > 1
                        ? $"\nC_TOOL：F_DS 已调整 {movedCount} 个{groupLabel}的位置。"
                        : "\nC_TOOL：F_DS 已调整当前标注位置。");
                }
            }
            finally
            {
                if (!TryRestorePreviewVisibility(doc, session.PreviewVisibilityById, context, out var restoreError))
                    ed.WriteMessage($"\nC_TOOL：{restoreError}");
            }
        }
    }

    private static void RunLeaderShift(
        Document doc,
        ObjectId seedId,
        ShiftCommandContext context,
        IReadOnlyCollection<ObjectId>? selectedLeaderIds = null,
        ObjectId explicitTextId = default)
    {
        var ed = doc.Editor;
        if (!TryCreateLeaderSession(doc, seedId, explicitTextId, context, selectedLeaderIds, out var session, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                ed.WriteMessage($"\nC_TOOL：{error}");
            return;
        }

        using (session)
        {
            var leaderLabel = GetLeaderGroupLabel(session.Items);
            ed.WriteMessage(session.Items.Count > 1
                ? $"\nC_TOOL：已识别 {session.Items.Count} 个{leaderLabel}，先对齐到同一{GetLeaderAlignmentLineLabel(context)}，再拖拽确认新位置。"
                : "\nC_TOOL：拖拽引线文字新位置，箭头不动，单击确认。");

            if (!TrySetPreviewVisibility(doc, session.PreviewVisibilityById, visible: false, context, out error))
            {
                ed.WriteMessage($"\nC_TOOL：{error}");
                return;
            }

            try
            {
                var jig = new LeaderShiftDrawJig(session, context, GetLeaderShiftPrompt(context));
                var dragResult = ed.Drag(jig);
                if (dragResult.Status != PromptStatus.OK)
                {
                    WriteCommandMessage(ed, context, $"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionShift.Cancelled}");
                    return;
                }

                if (Math.Abs(jig.Offset) <= PointTolerance && !HasLeaderAlignmentDelta(session))
                {
                    ed.WriteMessage("\nC_TOOL：引线位置未变。");
                    return;
                }

                if (!TryApplyLeaderOffset(doc, session, jig.Offset, context, out var movedCount, out error))
                {
                    ed.WriteMessage($"\nC_TOOL：{error}");
                    return;
                }

                WriteCommandMessage(ed, context, movedCount > 1
                    ? $"\nC_TOOL：F_DS 已调整 {movedCount} 个{leaderLabel}的位置。"
                    : $"\nC_TOOL：F_DS 已调整当前{leaderLabel}位置。");
            }
            finally
            {
                if (!TryRestorePreviewVisibility(doc, session.PreviewVisibilityById, context, out var restoreError))
                    ed.WriteMessage($"\nC_TOOL：{restoreError}");
            }
        }
    }

    private static bool TryCreateDimensionSession(
        Document doc,
        ObjectId seedId,
        ShiftCommandContext context,
        IReadOnlyCollection<ObjectId>? selectedDimensionIds,
        out DimensionShiftSession session,
        out string error)
    {
        session = null!;
        error = string.Empty;
        Dictionary<ObjectId, DimensionInfo>? infoById = null;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var dbObject = tr.GetObject(seedId, OpenMode.ForRead, false);
            if (dbObject is not Dimension seedDimension)
            {
                error = "所选对象不是标注。";
                return false;
            }

            if (!TryBuildDimensionInfo(seedId, seedDimension, context, out var seedInfo, out error))
                return false;

            infoById = new Dictionary<ObjectId, DimensionInfo>
            {
                [seedId] = seedInfo
            };

            List<ObjectId> orderedIds;
            var groupingMode = DimensionGroupingMode.Single;

            if (selectedDimensionIds is { Count: > 1 })
            {
                if (!TryCollectSelectedDimensionInfos(tr, seedInfo, selectedDimensionIds, infoById, context, out orderedIds, out error))
                {
                    DisposePreviewTemplates(infoById.Values);
                    return false;
                }

                if (orderedIds.Count > 1)
                    groupingMode = DimensionGroupingMode.SelectedDimensions;
            }
            else
            {
                var segments = new List<DddDimensionChainSegment>
                {
                    new(seedInfo.FirstPoint, seedInfo.SecondPoint, seedId)
                };

                if (!TryCollectChainInfos(tr, seedDimension, seedInfo, segments, infoById, context, out error))
                {
                    DisposePreviewTemplates(infoById.Values);
                    return false;
                }

                orderedIds = new List<ObjectId>();
                DddDimensionChainHelper.AddDimensionIdsInOrder(
                    segments,
                    orderedIds,
                    (left, right) => GetAxisCoordinate(left.First, seedInfo.FirstPoint, seedInfo.Axis)
                        .CompareTo(GetAxisCoordinate(right.First, seedInfo.FirstPoint, seedInfo.Axis)));

                if (orderedIds.Count > 1)
                    groupingMode = DimensionGroupingMode.ContinuousChain;
            }

            var items = new List<DimensionShiftItem>(orderedIds.Count);
            foreach (var id in orderedIds)
            {
                if (!infoById.TryGetValue(id, out var info))
                    continue;

                items.Add(new DimensionShiftItem(
                    info.DimensionId,
                    info.Kind,
                    info.DimLinePoint,
                    info.UsesDefaultTextPosition,
                    info.TextPosition,
                    info.PreviewTemplate));
            }

            if (items.Count == 0)
            {
                DisposePreviewTemplates(infoById.Values);
                error = "未找到可调整位置的标注。";
                return false;
            }

            session = new DimensionShiftSession(
                ResolveDimensionBasePoint(seedInfo, groupingMode, context),
                ResolveDimensionMoveDirection(seedInfo, context),
                items,
                groupingMode,
                BuildPreviewVisibilityMap(tr, orderedIds));
            tr.Commit();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            if (infoById != null)
                DisposePreviewTemplates(infoById.Values);
            LogCommandNonFatal(context, "F_DS 读取标注调整会话失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            if (infoById != null)
                DisposePreviewTemplates(infoById.Values);
            LogCommandNonFatal(context, "F_DS 读取标注调整会话失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            if (infoById != null)
                DisposePreviewTemplates(infoById.Values);
            LogCommandNonFatal(context, "F_DS 读取标注调整会话失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
    }

    private static bool TryCreateLeaderSession(
        Document doc,
        ObjectId seedId,
        ObjectId explicitTextId,
        ShiftCommandContext context,
        IReadOnlyCollection<ObjectId>? selectedLeaderIds,
        out LeaderShiftSession session,
        out string error)
    {
        session = null!;
        error = string.Empty;
        List<LeaderShiftItem>? items = null;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            items = new List<LeaderShiftItem>();
            foreach (var leaderId in GetOrderedLeaderIds(seedId, selectedLeaderIds))
            {
                if (!TryBuildLeaderShiftItem(doc, leaderId, tr, explicitTextId, context, out var item, out error))
                {
                    DisposeLeaderPreviewTemplates(items);
                    return false;
                }

                items.Add(item);
            }

            if (items.Count == 0)
            {
                error = "未找到可调整位置的引线。";
                return false;
            }

            session = new LeaderShiftSession(
                items[0].BasePoint,
                items[0].MoveNormal,
                items,
                BuildPreviewVisibilityMap(tr, GetLeaderPreviewEntityIds(items)));
            tr.Commit();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            if (items != null)
                DisposeLeaderPreviewTemplates(items);
            LogCommandNonFatal(context, "F_DS 读取引线调整会话失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            if (items != null)
                DisposeLeaderPreviewTemplates(items);
            LogCommandNonFatal(context, "F_DS 读取引线调整会话失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            if (items != null)
                DisposeLeaderPreviewTemplates(items);
            LogCommandNonFatal(context, "F_DS 读取引线调整会话失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
    }

    private static bool TryCreateTextSession(
        Document doc,
        ObjectId textId,
        ShiftCommandContext context,
        out TextShiftSession session,
        out string error)
    {
        session = null!;
        error = string.Empty;
        TextShiftItem? item = null;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var dbObject = tr.GetObject(textId, OpenMode.ForRead, false);
            if (dbObject is not Entity textEntity)
            {
                error = "所选对象不是文字。";
                return false;
            }

            if (!TryBuildTextShiftItem(doc, textId, textEntity, context, out item, out error))
                return false;

            session = new TextShiftSession(
                item.BasePoint,
                item.MoveNormal,
                item,
                BuildPreviewVisibilityMap(tr, item.GetPreviewEntityIds()));
            tr.Commit();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            item?.Dispose();
            LogCommandNonFatal(context, "F_DS 读取文字调整会话失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            item?.Dispose();
            LogCommandNonFatal(context, "F_DS 读取文字调整会话失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            item?.Dispose();
            LogCommandNonFatal(context, "F_DS 读取文字调整会话失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
    }

    private static bool TryCollectChainInfos(
        Transaction tr,
        Dimension seedDimension,
        DimensionInfo seedInfo,
        List<DddDimensionChainSegment> segments,
        Dictionary<ObjectId, DimensionInfo> infoById,
        ShiftCommandContext context,
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
                if (!entityId.IsValid || entityId.IsErased || infoById.ContainsKey(entityId))
                    continue;

                var dbObject = tr.GetObject(entityId, OpenMode.ForRead, false);
                if (dbObject is not Dimension candidate)
                    continue;

                if (!TryBuildDimensionInfo(entityId, candidate, context, out var candidateInfo, out _))
                    continue;

                var keepCandidate = false;
                try
                {
                    if (!AreParallel(seedInfo.Axis, candidateInfo.Axis))
                        continue;

                    var rowDistance = Math.Abs((candidateInfo.DimLinePoint - seedInfo.DimLinePoint).DotProduct(seedInfo.ReferenceNormal));
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
                    infoById[entityId] = candidateInfo;
                    added = true;
                    keepCandidate = true;
                }
                finally
                {
                    if (!keepCandidate)
                        candidateInfo.PreviewTemplate.Dispose();
                }
            }
        }

        return true;
    }

    private static bool TryCollectSelectedDimensionInfos(
        Transaction tr,
        DimensionInfo seedInfo,
        IReadOnlyCollection<ObjectId> selectedDimensionIds,
        Dictionary<ObjectId, DimensionInfo> infoById,
        ShiftCommandContext context,
        out List<ObjectId> orderedIds,
        out string error)
    {
        orderedIds = new List<ObjectId>();
        error = string.Empty;

        foreach (var entityId in selectedDimensionIds)
        {
            if (!entityId.IsValid || entityId.IsErased || infoById.ContainsKey(entityId))
                continue;

            var dbObject = tr.GetObject(entityId, OpenMode.ForRead, false);
            if (dbObject is not Dimension candidate)
                continue;

            if (!TryBuildDimensionInfo(entityId, candidate, context, out var candidateInfo, out _))
                continue;

            var keepCandidate = false;
            try
            {
                if (!AreParallel(seedInfo.Axis, candidateInfo.Axis))
                    continue;

                infoById[entityId] = candidateInfo;
                keepCandidate = true;
            }
            finally
            {
                if (!keepCandidate)
                    candidateInfo.PreviewTemplate.Dispose();
            }
        }

        var orderedInfos = new List<DimensionInfo>(infoById.Values);
        orderedInfos.Sort((left, right) => CompareDimensionInfosInAxisOrder(left, right, seedInfo.FirstPoint, seedInfo.Axis));
        foreach (var info in orderedInfos)
            orderedIds.Add(info.DimensionId);

        return true;
    }

    private static bool TryApplyOffset(
        Document doc,
        DimensionShiftSession session,
        double offset,
        ShiftCommandContext context,
        out int movedCount,
        out string error)
    {
        movedCount = 0;
        error = string.Empty;

        try
        {
            var groupDelta = session.MoveNormal * offset;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var item in session.Items)
                {
                    var dbObject = tr.GetObject(item.DimensionId, OpenMode.ForWrite, false);
                    if (dbObject is not Dimension dimension)
                        continue;

                    var itemDelta = ResolveDimensionItemDelta(session, item, groupDelta, context);
                    if (!TryApplyOffsetToDimension(dimension, item, itemDelta, context, out error))
                        return false;

                    movedCount++;
                }

                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用标注位置失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用标注位置失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用标注位置失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
    }

    private static bool TryApplyLeaderOffset(
        Document doc,
        LeaderShiftSession session,
        double offset,
        ShiftCommandContext context,
        out int movedCount,
        out string error)
    {
        movedCount = 0;
        error = string.Empty;

        try
        {
            var groupDelta = session.MoveNormal * offset;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var item in session.Items)
                {
                    var dbObject = tr.GetObject(item.LeaderId, OpenMode.ForWrite, false);
                    if (dbObject is not Entity leaderEntity)
                        continue;

                    var itemDelta = ResolveLeaderItemDelta(session, item, groupDelta);
                    if (!TryApplyOffsetToLeader(tr, leaderEntity, item, itemDelta, context, out error))
                        return false;

                    movedCount++;
                }

                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用引线位置失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用引线位置失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用引线位置失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
    }

    private static bool TryApplyTextOffset(
        Document doc,
        TextShiftSession session,
        double offset,
        ShiftCommandContext context,
        out string error)
    {
        error = string.Empty;

        try
        {
            var delta = session.MoveNormal * offset;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (tr.GetObject(session.Item.TextId, OpenMode.ForWrite, false) is not Entity textEntity)
                {
                    error = "所选对象不是文字。";
                    return false;
                }

                textEntity.TransformBy(Matrix3d.Displacement(delta));
                tr.Commit();
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用文字位置失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用文字位置失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 应用文字位置失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "执行失败", ex.Message);
            return false;
        }
    }

    private static bool TrySyncDimensionTextAvoidance(
        Document doc,
        DimensionShiftSession session,
        ShiftCommandContext context,
        out string error)
    {
        error = string.Empty;

        if (session.Items.Count == 0)
            return true;

        var dimensionIds = new List<ObjectId>(session.Items.Count);
        foreach (var item in session.Items)
        {
            if (!item.DimensionId.IsNull && item.DimensionId.IsValid && !item.DimensionId.IsErased)
                dimensionIds.Add(item.DimensionId);
        }

        if (dimensionIds.Count == 0)
            return true;

        if (DddDimensionTextAvoidanceService.TryApplyDimensionIds(
                doc,
                dimensionIds,
                context.CommandName,
                preferCurrentPlacement: true,
                out _,
                out _,
                out var syncError))
        {
            return true;
        }

        error = BuildCommandFailureMessage(context, "已调整位置，但避让同步失败", syncError);
        return false;
    }

    private static bool TryApplyOffsetToLeader(
        Transaction tr,
        Entity leaderEntity,
        LeaderShiftItem item,
        Vector3d delta,
        ShiftCommandContext context,
        out string error)
    {
        switch (item.Kind)
        {
            case SupportedLeaderKind.MLeader when leaderEntity is MLeader mLeader:
                return TryApplyOffsetToMLeader(mLeader, item, delta, context, out error);
            case SupportedLeaderKind.Leader when leaderEntity is Leader leader:
                return TryApplyOffsetToLegacyLeader(tr, leader, item, delta, out error);
            default:
                error = "所选对象与当前引线类型不匹配。";
                return false;
        }
    }

    private static bool TryApplyOffsetToMLeader(
        MLeader leader,
        LeaderShiftItem item,
        Vector3d delta,
        ShiftCommandContext context,
        out string error)
    {
        error = string.Empty;

        if (leader.ContentType != ContentType.MTextContent)
        {
            error = FormatForCommand(context, "F_DS 当前仅支持带文字的多重引线。");
            return false;
        }

        ApplyMLeaderShiftGeometry(leader, item, delta);
        return true;
    }

    private static void ApplyMLeaderShiftGeometry(MLeader leader, LeaderShiftItem item, Vector3d delta)
    {
        foreach (var line in item.Lines)
        {
            leader.SetFirstVertex(line.LeaderLineIndex, line.ArrowPoint);
            leader.SetLastVertex(line.LeaderLineIndex, line.LandingPoint + delta);
        }

        var nextTextLocation = item.OriginalTextLocation + delta;
        leader.TextLocation = nextTextLocation;
        var mt = leader.MText;
        if (mt != null)
        {
            mt.Location = nextTextLocation;
            leader.MText = mt;
        }

        foreach (var line in item.Lines)
        {
            leader.SetFirstVertex(line.LeaderLineIndex, line.ArrowPoint);
            leader.SetLastVertex(line.LeaderLineIndex, line.LandingPoint + delta);
        }
    }

    private static bool TryApplyOffsetToLegacyLeader(
        Transaction tr,
        Leader leader,
        LeaderShiftItem item,
        Vector3d delta,
        out string error)
    {
        error = string.Empty;

        if (item.OriginalLeaderVertices.Length < 2)
        {
            error = "所选引线没有可调整的基线。";
            return false;
        }

        ApplyLegacyLeaderShiftGeometry(leader, item, delta);
        if (!item.AnnotationId.IsNull &&
            item.AnnotationId.IsValid &&
            !item.AnnotationId.IsErased &&
            tr.GetObject(item.AnnotationId, OpenMode.ForWrite, false) is Entity annotationEntity)
        {
            annotationEntity.TransformBy(Matrix3d.Displacement(delta));
        }

        return true;
    }

    private static void ApplyLegacyLeaderShiftGeometry(Leader leader, LeaderShiftItem item, Vector3d delta)
    {
        for (var i = 0; i < item.OriginalLeaderVertices.Length; i++)
        {
            var target = i == 0 ? item.OriginalLeaderVertices[i] : item.OriginalLeaderVertices[i] + delta;
            leader.SetVertexAt(i, target);
        }
    }

    private static bool TryApplyOffsetToDimension(
        Dimension dimension,
        DimensionShiftItem item,
        Vector3d delta,
        ShiftCommandContext context,
        out string error)
    {
        return TryApplyOffsetToDimension(
            dimension,
            item,
            new DimensionShiftDelta(delta, delta, context.IsHorizontal),
            context,
            out error);
    }

    private static bool TryApplyOffsetToDimension(
        Dimension dimension,
        DimensionShiftItem item,
        DimensionShiftDelta delta,
        ShiftCommandContext context,
        out string error)
    {
        error = string.Empty;
        var moveDimensionLine = !context.IsHorizontal;
        ApplyDimensionTextPlacement(dimension, item, delta.TextDelta, delta.ForceCustomTextPosition);

        switch (item.Kind)
        {
            case SupportedDimensionKind.Rotated when dimension is RotatedDimension rotatedDimension:
                if (moveDimensionLine)
                    rotatedDimension.DimLinePoint = item.OriginalDimLinePoint + delta.DimensionLineDelta;
                rotatedDimension.RecomputeDimensionBlock(true);
                return true;
            case SupportedDimensionKind.Aligned when dimension is AlignedDimension alignedDimension:
                if (moveDimensionLine)
                    alignedDimension.DimLinePoint = item.OriginalDimLinePoint + delta.DimensionLineDelta;
                alignedDimension.RecomputeDimensionBlock(true);
                return true;
            default:
                error = FormatForCommand(context, "F_DS 仅支持线性标注与对齐标注。");
                return false;
        }
    }

    private static void ApplyDimensionTextPlacement(
        Dimension dimension,
        DimensionShiftItem item,
        Vector3d delta,
        bool forceCustomPosition)
    {
        dimension.Dimatfit = 3;
        dimension.Dimtix = false;
        dimension.Dimtofl = true;
        dimension.Dimtmove = 2;

        if (item.UsesDefaultTextPosition && !forceCustomPosition)
        {
            dimension.UsingDefaultTextPosition = true;
            return;
        }

        dimension.UsingDefaultTextPosition = false;
        dimension.TextPosition = item.OriginalTextPosition + delta;
    }

    private static bool TryGetSeedEntityId(
        Document doc,
        ShiftCommandContext context,
        out ObjectId entityId,
        out ShiftSeedKind kind,
        out ObjectId relatedId,
        out ObjectId[]? selectedDimensionIds,
        out ObjectId[]? selectedLeaderIds)
    {
        var ed = doc.Editor;
        entityId = ObjectId.Null;
        kind = ShiftSeedKind.Unknown;
        relatedId = ObjectId.Null;
        selectedDimensionIds = null;
        selectedLeaderIds = null;

        if (TryGetImpliedSeedEntityId(ed, out entityId, out kind, out relatedId, out selectedDimensionIds, out selectedLeaderIds))
            return true;

        var options = new PromptEntityOptions("\nC_TOOL：选择要调整位置的线性/对齐标注、文字、引线或多重引线：")
        {
            AllowNone = true
        };
        options.SetRejectMessage("\nC_TOOL：只能选择线性标注、对齐标注、文字、引线或多重引线。");
        DddDimensionSelectionFilter.AddLinearAlignedAllowedClasses(options);
        options.AddAllowedClass(typeof(DBText), true);
        options.AddAllowedClass(typeof(MText), true);
        options.AddAllowedClass(typeof(Leader), true);
        options.AddAllowedClass(typeof(MLeader), true);

        var result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
        {
            WriteCommandMessage(ed, context, $"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionShift.Cancelled}");
            return false;
        }

        entityId = result.ObjectId;
        kind = ClassifySeedKind(entityId.ObjectClass);
        return kind != ShiftSeedKind.Unknown;
    }

    private static bool TryGetImpliedSeedEntityId(
        Editor ed,
        out ObjectId entityId,
        out ShiftSeedKind kind,
        out ObjectId relatedId,
        out ObjectId[]? selectedDimensionIds,
        out ObjectId[]? selectedLeaderIds)
    {
        entityId = ObjectId.Null;
        kind = ShiftSeedKind.Unknown;
        relatedId = ObjectId.Null;
        selectedDimensionIds = null;
        selectedLeaderIds = null;

        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = implied.Value.GetObjectIds();
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        if (ids.Length == 1)
        {
            kind = ClassifySeedKind(ids[0].ObjectClass);
            if (kind == ShiftSeedKind.Unknown)
                return false;

            entityId = ids[0];
            return true;
        }

        if (ids.Length == 2 &&
            TryResolveImpliedLeaderTextPair(ids[0], ids[1], out var textId, out var leaderId))
        {
            entityId = textId;
            relatedId = leaderId;
            kind = ShiftSeedKind.Text;
            return true;
        }

        var areAllDimensions = ids.Length > 1;
        for (var index = 0; index < ids.Length && areAllDimensions; index++)
        {
            if (ClassifySeedKind(ids[index].ObjectClass) != ShiftSeedKind.Dimension)
                areAllDimensions = false;
        }

        if (areAllDimensions)
        {
            entityId = ids[0];
            kind = ShiftSeedKind.Dimension;
            selectedDimensionIds = ids;
            return true;
        }

        var areAllLeaders = ids.Length > 1;
        for (var index = 0; index < ids.Length && areAllLeaders; index++)
        {
            var currentKind = ClassifySeedKind(ids[index].ObjectClass);
            if (currentKind != ShiftSeedKind.Leader && currentKind != ShiftSeedKind.MLeader)
                areAllLeaders = false;
        }

        if (areAllLeaders)
        {
            entityId = ids[0];
            kind = ClassifySeedKind(ids[0].ObjectClass);
            selectedLeaderIds = ids;
            return true;
        }

        return false;
    }

    private static IEnumerable<ObjectId> GetOrderedLeaderIds(ObjectId seedId, IReadOnlyCollection<ObjectId>? selectedLeaderIds)
    {
        var orderedIds = new List<ObjectId>();
        AddLeaderId(orderedIds, seedId);
        if (selectedLeaderIds == null)
            return orderedIds;

        foreach (var leaderId in selectedLeaderIds)
            AddLeaderId(orderedIds, leaderId);

        return orderedIds;
    }

    private static void AddLeaderId(List<ObjectId> orderedIds, ObjectId leaderId)
    {
        if (leaderId.IsNull || !leaderId.IsValid || leaderId.IsErased || orderedIds.Contains(leaderId))
            return;

        orderedIds.Add(leaderId);
    }

    private static bool TryBuildLeaderShiftItem(
        Document doc,
        ObjectId leaderId,
        Transaction tr,
        ObjectId explicitTextId,
        ShiftCommandContext context,
        out LeaderShiftItem item,
        out string error)
    {
        item = null!;
        error = string.Empty;

        var dbObject = tr.GetObject(leaderId, OpenMode.ForRead, false);
        switch (dbObject)
        {
            case MLeader mLeader:
                return TryBuildLeaderShiftItem(doc, leaderId, mLeader, context, out item, out error);
            case Leader leader:
                return TryBuildLegacyLeaderShiftItem(doc, leaderId, leader, tr, explicitTextId, context, out item, out error);
            default:
                error = "所选对象不是引线或多重引线。";
                return false;
        }
    }

    private static ShiftSeedKind ClassifySeedKind(Autodesk.AutoCAD.Runtime.RXClass? objectClass)
    {
        if (objectClass == null)
            return ShiftSeedKind.Unknown;

        if (DddDimensionSelectionFilter.IsLinearOrAlignedDimensionClass(objectClass))
            return ShiftSeedKind.Dimension;

        var dbTextClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(DBText));
        if (objectClass.IsDerivedFrom(dbTextClass))
            return ShiftSeedKind.Text;

        var mTextClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(MText));
        if (objectClass.IsDerivedFrom(mTextClass))
            return ShiftSeedKind.Text;

        var leaderClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(Leader));
        if (objectClass.IsDerivedFrom(leaderClass))
            return ShiftSeedKind.Leader;

        var mLeaderClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(MLeader));
        if (objectClass.IsDerivedFrom(mLeaderClass))
            return ShiftSeedKind.MLeader;

        return ShiftSeedKind.Unknown;
    }

    private static bool TryResolveImpliedLeaderTextPair(
        ObjectId firstId,
        ObjectId secondId,
        out ObjectId textId,
        out ObjectId leaderId)
    {
        textId = ObjectId.Null;
        leaderId = ObjectId.Null;

        var firstKind = ClassifySeedKind(firstId.ObjectClass);
        var secondKind = ClassifySeedKind(secondId.ObjectClass);

        if (firstKind == ShiftSeedKind.Text && secondKind == ShiftSeedKind.Leader)
        {
            textId = firstId;
            leaderId = secondId;
            return true;
        }

        if (firstKind == ShiftSeedKind.Leader && secondKind == ShiftSeedKind.Text)
        {
            textId = secondId;
            leaderId = firstId;
            return true;
        }

        return false;
    }

    private static bool TryBuildDimensionInfo(
        ObjectId dimensionId,
        Dimension dimension,
        ShiftCommandContext context,
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
                error = FormatForCommand(context, "F_DS 仅支持线性标注与对齐标注。");
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
            error = "所选线性标注方向无效，无法调整位置。";
            return false;
        }

        var referenceNormal = new Vector3d(-axis.Y, axis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "所选线性标注法向无效，无法调整位置。";
            return false;
        }

        var firstPoint = dimension.XLine1Point;
        var secondPoint = dimension.XLine2Point;
        NormalizeSegmentDirection(axis, ref firstPoint, ref secondPoint);
        ReadTextPlacement(dimension, dimension.DimLinePoint, out var usesDefaultTextPosition, out var textPosition);
        var previewTemplate = dimension.Clone() as Dimension;
        if (previewTemplate == null)
        {
            error = "无法创建线性标注预览。";
            return false;
        }

        info = new DimensionInfo(
            dimensionId,
            SupportedDimensionKind.Rotated,
            firstPoint,
            secondPoint,
            dimension.DimLinePoint,
            axis,
            referenceNormal,
            usesDefaultTextPosition,
            textPosition,
            previewTemplate);
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
            error = "所选对齐标注方向无效，无法调整位置。";
            return false;
        }

        var referenceNormal = new Vector3d(-axis.Y, axis.X, 0.0);
        if (!TryNormalizePlanar(referenceNormal, out referenceNormal))
        {
            error = "所选对齐标注法向无效，无法调整位置。";
            return false;
        }

        NormalizeSegmentDirection(axis, ref firstPoint, ref secondPoint);
        ReadTextPlacement(dimension, dimension.DimLinePoint, out var usesDefaultTextPosition, out var textPosition);
        var previewTemplate = dimension.Clone() as Dimension;
        if (previewTemplate == null)
        {
            error = "无法创建对齐标注预览。";
            return false;
        }

        info = new DimensionInfo(
            dimensionId,
            SupportedDimensionKind.Aligned,
            firstPoint,
            secondPoint,
            dimension.DimLinePoint,
            axis,
            referenceNormal,
            usesDefaultTextPosition,
            textPosition,
            previewTemplate);
        return true;
    }

    private static bool TryBuildLeaderShiftItem(
        Document doc,
        ObjectId leaderId,
        MLeader leader,
        ShiftCommandContext context,
        out LeaderShiftItem item,
        out string error)
    {
        item = null!;
        error = string.Empty;

        if (leader.ContentType != ContentType.MTextContent)
        {
            error = FormatForCommand(context, "F_DS 当前仅支持带文字的多重引线。");
            return false;
        }

        if (!TryGetLeaderLineInfos(leader, out var lines, out error))
            return false;

        var previewTemplate = leader.Clone() as MLeader;
        if (previewTemplate == null)
        {
            error = "无法创建多重引线预览。";
            return false;
        }

        var basePoint = lines[0].LandingPoint;
        var moveNormal = ResolveLeaderMoveDirection(doc, context);
        var textLocation = TryReadLeaderTextLocation(leader, basePoint);
        item = new LeaderShiftItem(
            SupportedLeaderKind.MLeader,
            leaderId,
            basePoint,
            moveNormal,
            textLocation,
            lines,
            Array.Empty<Point3d>(),
            ObjectId.Null,
            previewTemplate,
            null);
        return true;
    }

    private static bool TryBuildLegacyLeaderShiftItem(
        Document doc,
        ObjectId leaderId,
        Leader leader,
        Transaction tr,
        ObjectId explicitTextId,
        ShiftCommandContext context,
        out LeaderShiftItem item,
        out string error)
    {
        item = null!;
        error = string.Empty;

        if (!TryGetLegacyLeaderVertices(leader, out var vertices, out error))
            return false;

        var previewTemplate = leader.Clone() as Entity;
        if (previewTemplate == null)
        {
            error = "无法创建引线预览。";
            return false;
        }

        var annotationId = ObjectId.Null;
        Entity? annotationPreviewTemplate = null;
        var basePoint = vertices[^1];
        var annotationBasePoint = basePoint;
        var effectiveTextId = ResolveExplicitOrAttachedTextId(leader, explicitTextId);
        if (!effectiveTextId.IsNull &&
            effectiveTextId.IsValid &&
            !effectiveTextId.IsErased &&
            tr.GetObject(effectiveTextId, OpenMode.ForRead, false) is Entity annotationEntity)
        {
            annotationId = effectiveTextId;
            annotationBasePoint = ResolveLeaderAnnotationBasePoint(annotationEntity, basePoint);
            annotationPreviewTemplate = annotationEntity.Clone() as Entity;
        }

        item = new LeaderShiftItem(
            SupportedLeaderKind.Leader,
            leaderId,
            basePoint,
            ResolveLeaderMoveDirection(doc, context),
            annotationBasePoint,
            Array.Empty<LeaderLineInfo>(),
            vertices,
            annotationId,
            previewTemplate,
            annotationPreviewTemplate);
        return true;
    }

    private static ObjectId ResolveExplicitOrAttachedTextId(Leader leader, ObjectId explicitTextId)
    {
        if (!explicitTextId.IsNull)
            return explicitTextId;

        if (!leader.Annotation.IsNull)
            return leader.Annotation;

        return ObjectId.Null;
    }

    private static bool TryResolveLeaderIdForText(
        Document doc,
        ObjectId textId,
        ObjectId explicitLeaderId,
        ShiftCommandContext context,
        out ObjectId leaderId,
        out string error)
    {
        leaderId = ObjectId.Null;
        error = string.Empty;

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            if (tr.GetObject(textId, OpenMode.ForRead, false) is not Entity textEntity)
            {
                error = "所选对象不是文字。";
                return false;
            }

            if (explicitLeaderId != ObjectId.Null)
            {
                if (tr.GetObject(explicitLeaderId, OpenMode.ForRead, false) is Leader)
                {
                    leaderId = explicitLeaderId;
                    return true;
                }

                error = "预选对象中未找到可联动的引线。";
                return false;
            }

            if (TryFindAttachedLeaderId(tr, textEntity, textId, out leaderId))
                return true;

            return false;
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 查找文字关联引线失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 查找文字关联引线失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 查找文字关联引线失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "启动失败", ex.Message);
            return false;
        }
    }

    private static bool TryFindAttachedLeaderId(Transaction tr, Entity textEntity, ObjectId textId, out ObjectId leaderId)
    {
        leaderId = ObjectId.Null;
        if (textEntity.OwnerId.IsNull || !textEntity.OwnerId.IsValid)
            return false;

        if (tr.GetObject(textEntity.OwnerId, OpenMode.ForRead, false) is not BlockTableRecord ownerRecord)
            return false;

        foreach (ObjectId entityId in ownerRecord)
        {
            if (!entityId.IsValid || entityId.IsErased)
                continue;

            if (tr.GetObject(entityId, OpenMode.ForRead, false) is not Leader leader)
                continue;

            if (leader.Annotation == textId)
            {
                leaderId = entityId;
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildTextShiftItem(
        Document doc,
        ObjectId textId,
        Entity textEntity,
        ShiftCommandContext context,
        out TextShiftItem item,
        out string error)
    {
        item = null!;
        error = string.Empty;

        if (textEntity is not DBText && textEntity is not MText)
        {
            error = FormatForCommand(context, "F_DS 当前仅支持单行文字或多行文字。");
            return false;
        }

        var previewTemplate = textEntity.Clone() as Entity;
        if (previewTemplate == null)
        {
            error = "无法创建文字预览。";
            return false;
        }

        var basePoint = ResolveLeaderAnnotationBasePoint(textEntity, Point3d.Origin);
        item = new TextShiftItem(textId, basePoint, ResolveLeaderMoveDirection(doc, context), previewTemplate);
        return true;
    }

    private static bool TryGetLeaderLineInfos(MLeader leader, out List<LeaderLineInfo> lines, out string error)
    {
        lines = new List<LeaderLineInfo>();
        error = string.Empty;

        var leaderIndexes = leader.GetLeaderIndexes();
        if (leaderIndexes == null || leaderIndexes.Count == 0)
        {
            error = "所选多重引线没有可调整的基线。";
            return false;
        }

        foreach (var rawLeaderIndex in leaderIndexes)
        {
            if (rawLeaderIndex is not int leaderIndex)
                continue;

            var leaderLineIndexes = leader.GetLeaderLineIndexes(leaderIndex);
            if (leaderLineIndexes == null || leaderLineIndexes.Count == 0)
                continue;

            foreach (var rawLeaderLineIndex in leaderLineIndexes)
            {
                if (rawLeaderLineIndex is not int leaderLineIndex)
                    continue;

                lines.Add(new LeaderLineInfo(
                    leaderIndex,
                    leaderLineIndex,
                    leader.GetFirstVertex(leaderLineIndex),
                    leader.GetLastVertex(leaderLineIndex)));
            }
        }

        if (lines.Count == 0)
        {
            error = "所选多重引线没有可调整的基线。";
            return false;
        }

        return true;
    }

    private static bool TryGetLegacyLeaderVertices(Leader leader, out Point3d[] vertices, out string error)
    {
        error = string.Empty;
        vertices = Array.Empty<Point3d>();

        if (leader.NumVertices < 2)
        {
            error = "所选引线没有可调整的基线。";
            return false;
        }

        vertices = new Point3d[leader.NumVertices];
        for (var i = 0; i < leader.NumVertices; i++)
            vertices[i] = leader.VertexAt(i);

        return true;
    }

    private static Point3d ResolveLeaderAnnotationBasePoint(Entity annotationEntity, Point3d fallback)
    {
        if (TryGetEntityCenter(annotationEntity, out var center))
            return center;

        switch (annotationEntity)
        {
            case MText mText when IsFinitePoint(mText.Location):
                return mText.Location;
            case DBText dbText when IsFinitePoint(dbText.Position):
                return dbText.Position;
            default:
                return fallback;
        }
    }

    private static bool TryGetEntityCenter(Entity entity, out Point3d center)
    {
        center = Point3d.Origin;

        try
        {
            var extents = entity.GeometricExtents;
            center = MidPoint(extents.MinPoint, extents.MaxPoint);
            return IsFinitePoint(center);
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

        return false;
    }

    private static Dictionary<ObjectId, bool> BuildPreviewVisibilityMap(Transaction tr, IEnumerable<ObjectId> entityIds)
    {
        var map = new Dictionary<ObjectId, bool>();
        foreach (var entityId in entityIds)
        {
            if (entityId.IsNull || !entityId.IsValid || entityId.IsErased || map.ContainsKey(entityId))
                continue;

            if (tr.GetObject(entityId, OpenMode.ForRead, false) is Entity entity)
                map[entityId] = entity.Visible;
        }

        return map;
    }

    private static bool TrySetPreviewVisibility(
        Document doc,
        IReadOnlyDictionary<ObjectId, bool> visibilityById,
        bool visible,
        ShiftCommandContext context,
        out string error)
    {
        error = string.Empty;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (DisablePreviewUndoRecording(db, context))
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var pair in visibilityById)
                {
                    if (tr.GetObject(pair.Key, OpenMode.ForWrite, false) is not Entity entity)
                        continue;

                    entity.Visible = visible;
                }

                tr.Commit();
                TryQueuePreviewGraphicsFlush(db, context);
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 切换预览可见性失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "预览失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 切换预览可见性失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "预览失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 切换预览可见性失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "预览失败", ex.Message);
            return false;
        }
    }

    private static bool TryRestorePreviewVisibility(
        Document doc,
        IReadOnlyDictionary<ObjectId, bool> visibilityById,
        ShiftCommandContext context,
        out string error)
    {
        error = string.Empty;

        try
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (DisablePreviewUndoRecording(db, context))
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var pair in visibilityById)
                {
                    if (tr.GetObject(pair.Key, OpenMode.ForWrite, false) is not Entity entity)
                        continue;

                    entity.Visible = pair.Value;
                }

                tr.Commit();
                TryQueuePreviewGraphicsFlush(db, context);
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 恢复预览可见性失败（无效操作）", ex);
            error = BuildCommandFailureMessage(context, "预览结束后恢复显示失败", ex.Message);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 恢复预览可见性失败（CAD）", ex);
            error = BuildCommandFailureMessage(context, "预览结束后恢复显示失败", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 恢复预览可见性失败（参数）", ex);
            error = BuildCommandFailureMessage(context, "预览结束后恢复显示失败", ex.Message);
            return false;
        }
    }

    private static IDisposable DisablePreviewUndoRecording(Database database, ShiftCommandContext context)
    {
        try
        {
            if (!database.UndoRecording)
                return NoopDisposable.Instance;

            database.DisableUndoRecording(true);
            return new PreviewUndoRecordingScope(database, context);
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 暂停预览撤销记录失败（无效操作）", ex);
            return NoopDisposable.Instance;
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 暂停预览撤销记录失败（CAD）", ex);
            return NoopDisposable.Instance;
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 暂停预览撤销记录失败（参数）", ex);
            return NoopDisposable.Instance;
        }
    }

    private static void TryQueuePreviewGraphicsFlush(Database database, ShiftCommandContext context)
    {
        try
        {
            database.TransactionManager.QueueForGraphicsFlush();
        }
        catch (InvalidOperationException ex)
        {
            LogCommandNonFatal(context, "F_DS 刷新预览图形失败（无效操作）", ex);
        }
        catch (AcadRuntimeException ex)
        {
            LogCommandNonFatal(context, "F_DS 刷新预览图形失败（CAD）", ex);
        }
        catch (ArgumentException ex)
        {
            LogCommandNonFatal(context, "F_DS 刷新预览图形失败（参数）", ex);
        }
    }

    private static Point3d TryReadLeaderTextLocation(MLeader leader, Point3d fallback)
    {
        try
        {
            var value = leader.TextLocation;
            if (IsFinitePoint(value))
                return value;
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

    private static Vector3d ResolveLeaderMoveDirection(Document doc, ShiftCommandContext context)
    {
        try
        {
            var ucs = doc.Editor.CurrentUserCoordinateSystem.CoordinateSystem3d;
            var axisSource = context.IsHorizontal ? ucs.Xaxis : ucs.Yaxis;
            if (TryNormalizePlanar(axisSource, out var moveAxis))
                return moveAxis;
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

        return context.IsHorizontal
            ? new Vector3d(1.0, 0.0, 0.0)
            : new Vector3d(0.0, 1.0, 0.0);
    }

    private static void ReadTextPlacement(
        Dimension dimension,
        Point3d fallback,
        out bool usesDefaultTextPosition,
        out Point3d textPosition)
    {
        usesDefaultTextPosition = true;
        textPosition = fallback;

        try
        {
            usesDefaultTextPosition = dimension.UsingDefaultTextPosition;
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

        try
        {
            var currentTextPosition = dimension.TextPosition;
            if (IsFinitePoint(currentTextPosition))
                textPosition = currentTextPosition;
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

        if (!IsFinitePoint(textPosition))
            textPosition = fallback;
    }

    private static Point3d ResolveDimensionBasePoint(
        DimensionInfo seedInfo,
        DimensionGroupingMode groupingMode,
        ShiftCommandContext context) =>
        context.IsHorizontal ? seedInfo.TextPosition : seedInfo.DimLinePoint;

    private static Vector3d ResolveDimensionMoveDirection(DimensionInfo info, ShiftCommandContext context)
    {
        if (context.IsHorizontal)
            return info.Axis;

        var midpoint = MidPoint(info.FirstPoint, info.SecondPoint);
        var offset = Dot2d(info.DimLinePoint - midpoint, info.ReferenceNormal);
        if (Math.Abs(offset) > PointTolerance)
            return offset >= 0.0 ? info.ReferenceNormal : -info.ReferenceNormal;

        return info.ReferenceNormal;
    }

    private static ShiftCommandContext CreateCommandContext(ShiftDirection direction) =>
        direction == ShiftDirection.Horizontal
            ? new ShiftCommandContext(direction, DddPluginCommandIds.DimShiftHorizontal, "左右")
            : new ShiftCommandContext(direction, DddPluginCommandIds.DimShift, "上下");

    private static string FormatForCommand(ShiftCommandContext context, string text)
    {
        if (string.IsNullOrEmpty(text) || string.Equals(context.CommandName, DddPluginCommandIds.DimShift, StringComparison.Ordinal))
            return text;

        return text.Replace(DddPluginCommandIds.DimShift, context.CommandName);
    }

    private static void WriteCommandMessage(Editor editor, ShiftCommandContext context, string message) =>
        editor.WriteMessage(FormatForCommand(context, message));

    private static void LogCommandNonFatal(ShiftCommandContext context, string message, Exception ex) =>
        C_toolsDiagnostics.LogNonFatal(FormatForCommand(context, message), ex);

    private static string BuildCommandFailureMessage(ShiftCommandContext context, string phase, string detail) =>
        FormatForCommand(context, $"F_DS {phase}：{detail}");

    private static string GetDimensionShiftPrompt(ShiftCommandContext context) =>
        context.IsHorizontal
            ? "\nC_TOOL：指定标注文字新位置（拖拽预览，单击确认）："
            : "\nC_TOOL：指定标注新位置（拖拽预览，单击确认）：";

    private static string GetLeaderShiftPrompt(ShiftCommandContext context) =>
        $"\nC_TOOL：指定引线文字与基线新位置（仅{context.AxisLabel}拖拽预览，单击确认）：";

    private static string GetTextShiftPrompt(ShiftCommandContext context) =>
        $"\nC_TOOL：指定文字新位置（仅{context.AxisLabel}拖拽预览，单击确认）：";

    private static string GetDimensionGroupLabel(DimensionGroupingMode groupingMode) =>
        groupingMode == DimensionGroupingMode.SelectedDimensions ? "选中标注" : "连续标注";

    private static string GetLeaderGroupLabel(IReadOnlyCollection<LeaderShiftItem> items)
    {
        var hasLeader = false;
        var hasMLeader = false;

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case SupportedLeaderKind.Leader:
                    hasLeader = true;
                    break;
                case SupportedLeaderKind.MLeader:
                    hasMLeader = true;
                    break;
            }

            if (hasLeader && hasMLeader)
                return "引线/多重引线";
        }

        return hasLeader ? "引线" : "多重引线";
    }

    private static string GetLeaderAlignmentLineLabel(ShiftCommandContext context) =>
        context.IsHorizontal ? "垂直线" : "水平线";

    private static bool ShouldAlignSelectedDimensionLines(DimensionShiftSession session, ShiftCommandContext context) =>
        ShouldAlignSelectedDimensionLines(session.GroupingMode, session.Items.Count, context);

    private static bool ShouldAlignSelectedDimensionLines(
        DimensionGroupingMode groupingMode,
        int itemCount,
        ShiftCommandContext context) =>
        !context.IsHorizontal && groupingMode == DimensionGroupingMode.SelectedDimensions && itemCount > 1;

    private static bool HasSelectedDimensionLineAlignmentDelta(DimensionShiftSession session, ShiftCommandContext context)
    {
        if (!ShouldAlignSelectedDimensionLines(session, context))
            return false;

        foreach (var item in session.Items)
        {
            if (Math.Abs(GetSelectedDimensionLineAlignmentOffset(session, item)) > PointTolerance)
                return true;
        }

        return false;
    }

    private static DimensionShiftDelta ResolveDimensionItemDelta(
        DimensionShiftSession session,
        DimensionShiftItem item,
        Vector3d groupDelta,
        ShiftCommandContext context)
    {
        if (!ShouldAlignSelectedDimensionLines(session, context))
            return new DimensionShiftDelta(groupDelta, groupDelta, context.IsHorizontal);

        var alignmentOffset = GetSelectedDimensionLineAlignmentOffset(session, item);
        var alignmentDelta = session.MoveNormal * alignmentOffset;
        var itemDelta = groupDelta + alignmentDelta;
        return new DimensionShiftDelta(
            itemDelta,
            itemDelta,
            forceCustomTextPosition: !item.UsesDefaultTextPosition);
    }

    private static double GetSelectedDimensionLineAlignmentOffset(DimensionShiftSession session, DimensionShiftItem item)
    {
        var referencePoint = session.BasePoint;
        return Dot2d(referencePoint - item.OriginalDimLinePoint, session.MoveNormal);
    }

    private static bool HasLeaderAlignmentDelta(LeaderShiftSession session)
    {
        if (session.Items.Count <= 1)
            return false;

        foreach (var item in session.Items)
        {
            if (Math.Abs(GetLeaderAlignmentOffset(session, item)) > PointTolerance)
                return true;
        }

        return false;
    }

    private static Vector3d ResolveLeaderItemDelta(LeaderShiftSession session, LeaderShiftItem item, Vector3d groupDelta)
    {
        if (session.Items.Count <= 1)
            return groupDelta;

        var alignmentOffset = GetLeaderAlignmentOffset(session, item);
        return groupDelta + (session.MoveNormal * alignmentOffset);
    }

    private static double GetLeaderAlignmentOffset(LeaderShiftSession session, LeaderShiftItem item)
    {
        var referencePoint = ResolveLeaderAlignmentAnchor(session.Items[0]);
        var itemPoint = ResolveLeaderAlignmentAnchor(item);
        return Dot2d(referencePoint - itemPoint, session.MoveNormal);
    }

    private static Point3d ResolveLeaderAlignmentAnchor(LeaderShiftItem item) =>
        IsFinitePoint(item.OriginalTextLocation) ? item.OriginalTextLocation : item.BasePoint;

    private static void NormalizeSegmentDirection(Vector3d axis, ref Point3d firstPoint, ref Point3d secondPoint)
    {
        if (GetAxisCoordinate(secondPoint, firstPoint, axis) < 0.0)
            (firstPoint, secondPoint) = (secondPoint, firstPoint);
    }

    private static int CompareDimensionInfosInAxisOrder(DimensionInfo left, DimensionInfo right, Point3d origin, Vector3d axis)
    {
        var leftMidMetric = GetAxisCoordinate(MidPoint(left.FirstPoint, left.SecondPoint), origin, axis);
        var rightMidMetric = GetAxisCoordinate(MidPoint(right.FirstPoint, right.SecondPoint), origin, axis);
        var result = leftMidMetric.CompareTo(rightMidMetric);
        if (result != 0)
            return result;

        var leftFirstMetric = GetAxisCoordinate(left.FirstPoint, origin, axis);
        var rightFirstMetric = GetAxisCoordinate(right.FirstPoint, origin, axis);
        return leftFirstMetric.CompareTo(rightFirstMetric);
    }

    private static double GetAxisCoordinate(Point3d point, Point3d origin, Vector3d axis) =>
        Dot2d(point - origin, axis);

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

    private static bool IsFiniteValue(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsFinitePoint(Point3d point) =>
        IsFiniteValue(point.X) &&
        IsFiniteValue(point.Y) &&
        IsFiniteValue(point.Z);

    private static double Dot2d(Vector3d left, Vector3d right) => (left.X * right.X) + (left.Y * right.Y);

    private static Point3d MidPoint(Point3d firstPoint, Point3d secondPoint) =>
        new(
            (firstPoint.X + secondPoint.X) * 0.5,
            (firstPoint.Y + secondPoint.Y) * 0.5,
            (firstPoint.Z + secondPoint.Z) * 0.5);

    private static void DisposePreviewTemplates(IEnumerable<DimensionInfo> infos)
    {
        foreach (var info in infos)
            info.PreviewTemplate.Dispose();
    }

    private static void DisposeLeaderPreviewTemplates(IEnumerable<LeaderShiftItem> items)
    {
        foreach (var item in items)
            item.Dispose();
    }

    private static IEnumerable<ObjectId> GetLeaderPreviewEntityIds(IEnumerable<LeaderShiftItem> items)
    {
        foreach (var item in items)
        {
            foreach (var entityId in item.GetPreviewEntityIds())
                yield return entityId;
        }
    }

    private readonly struct DimensionShiftDelta
    {
        internal DimensionShiftDelta(Vector3d textDelta, Vector3d dimensionLineDelta, bool forceCustomTextPosition)
        {
            TextDelta = textDelta;
            DimensionLineDelta = dimensionLineDelta;
            ForceCustomTextPosition = forceCustomTextPosition;
        }

        internal Vector3d TextDelta { get; }

        internal Vector3d DimensionLineDelta { get; }

        internal bool ForceCustomTextPosition { get; }
    }

    private sealed class PreviewUndoRecordingScope : IDisposable
    {
        private readonly Database _database;
        private readonly ShiftCommandContext _context;
        private bool _disposed;

        internal PreviewUndoRecordingScope(Database database, ShiftCommandContext context)
        {
            _database = database;
            _context = context;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                _database.DisableUndoRecording(false);
            }
            catch (InvalidOperationException ex)
            {
                LogCommandNonFatal(_context, "F_DS 恢复预览撤销记录失败（无效操作）", ex);
            }
            catch (AcadRuntimeException ex)
            {
                LogCommandNonFatal(_context, "F_DS 恢复预览撤销记录失败（CAD）", ex);
            }
            catch (ArgumentException ex)
            {
                LogCommandNonFatal(_context, "F_DS 恢复预览撤销记录失败（参数）", ex);
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static readonly NoopDisposable Instance = new();

        private NoopDisposable()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class DimensionShiftDrawJig : DrawJig
    {
        private readonly DimensionShiftSession _session;
        private readonly ShiftCommandContext _context;
        private readonly string _prompt;
        private Point3d _currentPoint;
        private double _offset;

        internal DimensionShiftDrawJig(DimensionShiftSession session, ShiftCommandContext context, string prompt)
        {
            _session = session;
            _context = context;
            _prompt = prompt;
            _currentPoint = session.BasePoint;
        }

        internal double Offset => _offset;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(_prompt)
            {
                UserInputControls = UserInputControls.Accept3dCoordinates
            };

            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            if (result.Value.IsEqualTo(_currentPoint))
                return SamplerStatus.NoChange;

            _currentPoint = result.Value;
            var nextOffset = Dot2d(_currentPoint - _session.BasePoint, _session.MoveNormal);
            if (Math.Abs(nextOffset - _offset) <= PointTolerance)
                return SamplerStatus.NoChange;

            _offset = nextOffset;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var groupDelta = _session.MoveNormal * _offset;
            foreach (var item in _session.Items)
            {
                using var clone = item.PreviewTemplate.Clone() as Dimension;
                if (clone == null)
                    continue;

                var itemDelta = ResolveDimensionItemDelta(_session, item, groupDelta, _context);
                if (!TryApplyOffsetToDimension(clone, item, itemDelta, _context, out _))
                    continue;

                draw.Geometry.Draw(clone);
            }

            return true;
        }
    }

    private sealed class LeaderShiftDrawJig : DrawJig
    {
        private readonly LeaderShiftSession _session;
        private readonly ShiftCommandContext _context;
        private readonly string _prompt;
        private Point3d _currentPoint;
        private double _offset;

        internal LeaderShiftDrawJig(LeaderShiftSession session, ShiftCommandContext context, string prompt)
        {
            _session = session;
            _context = context;
            _prompt = prompt;
            _currentPoint = session.BasePoint;
        }

        internal double Offset => _offset;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(_prompt)
            {
                UserInputControls = UserInputControls.Accept3dCoordinates
            };

            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            if (result.Value.IsEqualTo(_currentPoint))
                return SamplerStatus.NoChange;

            _currentPoint = result.Value;
            var nextOffset = Dot2d(_currentPoint - _session.BasePoint, _session.MoveNormal);
            if (Math.Abs(nextOffset - _offset) <= PointTolerance)
                return SamplerStatus.NoChange;

            _offset = nextOffset;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var groupDelta = _session.MoveNormal * _offset;
            foreach (var item in _session.Items)
            {
                var itemDelta = ResolveLeaderItemDelta(_session, item, groupDelta);
                switch (item.Kind)
                {
                    case SupportedLeaderKind.MLeader:
                    {
                        using var clone = item.PreviewTemplate.Clone() as MLeader;
                        if (clone == null)
                            continue;

                        if (!TryApplyOffsetToMLeader(clone, item, itemDelta, _context, out _))
                            continue;

                        draw.Geometry.Draw(clone);
                        continue;
                    }
                    case SupportedLeaderKind.Leader:
                    {
                        using var clone = item.PreviewTemplate.Clone() as Leader;
                        if (clone == null)
                            continue;

                        ApplyLegacyLeaderShiftGeometry(clone, item, itemDelta);
                        draw.Geometry.Draw(clone);

                        using var annotationClone = item.AnnotationPreviewTemplate?.Clone() as Entity;
                        if (annotationClone != null)
                        {
                            annotationClone.TransformBy(Matrix3d.Displacement(itemDelta));
                            draw.Geometry.Draw(annotationClone);
                        }

                        continue;
                    }
                    default:
                        continue;
                }
            }

            return true;
        }
    }

    private sealed class TextShiftDrawJig : DrawJig
    {
        private readonly TextShiftSession _session;
        private readonly string _prompt;
        private Point3d _currentPoint;
        private double _offset;

        internal TextShiftDrawJig(TextShiftSession session, string prompt)
        {
            _session = session;
            _prompt = prompt;
            _currentPoint = session.BasePoint;
        }

        internal double Offset => _offset;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(_prompt)
            {
                UserInputControls = UserInputControls.Accept3dCoordinates
            };

            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            if (result.Value.IsEqualTo(_currentPoint))
                return SamplerStatus.NoChange;

            _currentPoint = result.Value;
            var nextOffset = Dot2d(_currentPoint - _session.BasePoint, _session.MoveNormal);
            if (Math.Abs(nextOffset - _offset) <= PointTolerance)
                return SamplerStatus.NoChange;

            _offset = nextOffset;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var delta = _session.MoveNormal * _offset;
            using var clone = _session.Item.PreviewTemplate.Clone() as Entity;
            if (clone == null)
                return true;

            clone.TransformBy(Matrix3d.Displacement(delta));
            draw.Geometry.Draw(clone);
            return true;
        }
    }

    private sealed class DimensionShiftSession : IDisposable
    {
        internal DimensionShiftSession(
            Point3d basePoint,
            Vector3d moveNormal,
            List<DimensionShiftItem> items,
            DimensionGroupingMode groupingMode,
            Dictionary<ObjectId, bool> previewVisibilityById)
        {
            BasePoint = basePoint;
            MoveNormal = moveNormal;
            Items = items;
            GroupingMode = groupingMode;
            PreviewVisibilityById = previewVisibilityById;
        }

        internal Point3d BasePoint { get; }

        internal Vector3d MoveNormal { get; }

        internal List<DimensionShiftItem> Items { get; }

        internal DimensionGroupingMode GroupingMode { get; }

        internal IReadOnlyDictionary<ObjectId, bool> PreviewVisibilityById { get; }

        public void Dispose()
        {
            foreach (var item in Items)
                item.Dispose();
        }
    }

    private sealed class LeaderShiftSession : IDisposable
    {
        internal LeaderShiftSession(
            Point3d basePoint,
            Vector3d moveNormal,
            List<LeaderShiftItem> items,
            Dictionary<ObjectId, bool> previewVisibilityById)
        {
            BasePoint = basePoint;
            MoveNormal = moveNormal;
            Items = items;
            PreviewVisibilityById = previewVisibilityById;
        }

        internal Point3d BasePoint { get; }

        internal Vector3d MoveNormal { get; }

        internal List<LeaderShiftItem> Items { get; }

        internal IReadOnlyDictionary<ObjectId, bool> PreviewVisibilityById { get; }

        public void Dispose()
        {
            foreach (var item in Items)
                item.Dispose();
        }
    }

    private sealed class TextShiftSession : IDisposable
    {
        internal TextShiftSession(
            Point3d basePoint,
            Vector3d moveNormal,
            TextShiftItem item,
            Dictionary<ObjectId, bool> previewVisibilityById)
        {
            BasePoint = basePoint;
            MoveNormal = moveNormal;
            Item = item;
            PreviewVisibilityById = previewVisibilityById;
        }

        internal Point3d BasePoint { get; }

        internal Vector3d MoveNormal { get; }

        internal TextShiftItem Item { get; }

        internal IReadOnlyDictionary<ObjectId, bool> PreviewVisibilityById { get; }

        public void Dispose() => Item.Dispose();
    }
}
