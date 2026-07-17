using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsPlugin;

/// <summary>
/// F_ZZ：将所选对象整体居中到所在矩形框中心。
/// </summary>
internal static class RectangleCenterAlignCommandService
{
    private const string CommandName = PluginCommandIds.RectangleCenterAlign;
    private const double GeometryTolerance = 1e-6;
    private const double AxisAlignmentTolerance = 1e-4;
    private const double AxisMergeTolerance = 1e-4;
    private const double AxisDirectionTolerance = 1e-4;
    private const int MaxAnalysisOrientationCount = 64;

    internal static void Run()
    {
        CadCommandContext.ExecuteInActiveDocument($"执行 {CommandName}", (doc, ed) =>
        {
            if (!TryGetSelectionIds(ed, out var selectedIds, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 已取消。"
                    : $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 未选择任何对象。");
                return;
            }

            var analysisTransforms = BuildAnalysisTransforms(ed.CurrentUserCoordinateSystem);
            var targetIds = Array.Empty<ObjectId>();
            var targetExtents = default(Extents3d);
            var selectedFrames = new List<RectangleFrameInfo>();
            var targetResolutionError = "";
            CadDatabaseScope.Read(doc, (_, tr) =>
            {
                TryResolveTargets(
                    tr,
                    selectedIds,
                    analysisTransforms,
                    out targetIds,
                    out targetExtents,
                    out selectedFrames,
                    out targetResolutionError);
            });

            if (targetIds.Length == 0)
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} {targetResolutionError}");
                return;
            }

            var frameInfo = default(RectangleFrameInfo);
            var frameSource = "";
            CadDatabaseScope.Read(doc, (db, tr) =>
            {
                if (TryFindBestContainingFrame(selectedFrames, targetExtents, out var selectedFrameInfo))
                {
                    UseBetterFrame(selectedFrameInfo, "当前选择", ref frameInfo, ref frameSource);
                }

                if (TryFindBestContainingFrameInCurrentSpace(tr, db, targetIds, targetExtents, out var entityFrameInfo))
                {
                    UseBetterFrame(entityFrameInfo, "自动识别", ref frameInfo, ref frameSource);
                }

                if (TryInferContainingFrameFromLineNetworkInCurrentSpace(
                        tr,
                        db,
                        targetIds,
                        targetExtents,
                        analysisTransforms,
                        out var lineNetworkFrameInfo))
                {
                    UseBetterFrame(lineNetworkFrameInfo, "自动识别", ref frameInfo, ref frameSource);
                }
            });

            if (!frameInfo.IsResolved)
            {
                if (!TryPromptRectangleFrame(doc, targetIds, analysisTransforms, out var pickedFrameInfo, out cancelled))
                {
                    ed.WriteMessage(cancelled
                        ? $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 已取消。"
                        : $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 未找到可用的矩形框。");
                    return;
                }

                frameInfo = pickedFrameInfo;
                frameSource = "手动点选";
            }

            var movedCount = 0;
            var finalError = "";
            var alreadyCentered = false;
            var frameInvalid = false;

            CadDatabaseScope.Write(doc, (_, tr) =>
            {
                if (!TryGetTargetExtents(tr, targetIds, out targetExtents, out finalError))
                {
                    return;
                }

                if (frameInfo.RequiresEntityValidation &&
                    !TryGetRectangleFrameInfo(tr, frameInfo.ObjectId, out frameInfo))
                {
                    frameInvalid = true;
                    return;
                }

                var targetCenter = GetExtentsCenter(targetExtents);
                var displacement = new Vector3d(
                    frameInfo.Center.X - targetCenter.X,
                    frameInfo.Center.Y - targetCenter.Y,
                    0.0);
                if (displacement.Length <= GeometryTolerance)
                {
                    alreadyCentered = true;
                    return;
                }

                if (!TryMoveTargets(tr, targetIds, displacement, out movedCount, out finalError))
                    return;
            }, requireDocumentLock: true);

            if (finalError.Length > 0)
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} {finalError}");
                return;
            }

            if (frameInvalid)
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} 目标矩形框已失效，请重新执行。");
                return;
            }

            if (alreadyCentered)
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} 所选对象已经在矩形框中心。");
                return;
            }

            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} 已将 {movedCount} 个对象居中到矩形框中心（{frameSource}）。");
        });
    }

    private static bool TryGetSelectionIds(Editor ed, out ObjectId[] ids, out bool cancelled)
    {
        ids = Array.Empty<ObjectId>();
        cancelled = false;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            ids = implied.Value.GetObjectIds()
                .Where(static id => !id.IsNull)
                .Distinct()
                .ToArray();
            return ids.Length > 0;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nC_TOOL：选择要居中的对象；若已同时选中矩形框，命令会优先使用它："
        };
        var result = ed.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
        {
            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }

        ids = result.Value.GetObjectIds()
            .Where(static id => !id.IsNull)
            .Distinct()
            .ToArray();
        return ids.Length > 0;
    }

    private static bool TryResolveTargets(
        Transaction tr,
        ObjectId[] selectedIds,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out ObjectId[] targetIds,
        out Extents3d targetExtents,
        out List<RectangleFrameInfo> selectedFrames,
        out string error)
    {
        targetIds = Array.Empty<ObjectId>();
        targetExtents = default;
        selectedFrames = new List<RectangleFrameInfo>();
        error = "";

        var targetCandidates = new List<TargetCandidate>(selectedIds.Length);
        var guideCandidateIds = new List<ObjectId>();
        var hasDeferredGuideWithoutExtents = false;

        for (var index = 0; index < selectedIds.Length; index++)
        {
            var objectId = selectedIds[index];
            if (objectId.IsNull)
                continue;

            if (TryGetRectangleFrameInfo(tr, objectId, out var frameInfo))
            {
                selectedFrames.Add(frameInfo);
                continue;
            }

            var canUseAsGuide = CanContributeLineNetworkGuide(tr, objectId);
            if (canUseAsGuide)
                guideCandidateIds.Add(objectId);

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null)
            {
                continue;
            }

            if (!TryGetEntityExtents(entity, out var extents))
            {
                if (canUseAsGuide)
                {
                    hasDeferredGuideWithoutExtents = true;
                    continue;
                }

                error = "所选对象中存在无法读取范围的实体，请重新选择。";
                return false;
            }

            targetCandidates.Add(new TargetCandidate(objectId, extents));
        }

        if (selectedFrames.Count == 0 &&
            TryResolveSelectedLineNetworkFrame(
                tr,
                guideCandidateIds,
                targetCandidates,
                analysisTransforms,
                out targetIds,
                out targetExtents,
                out var selectedLineNetworkFrame))
        {
            selectedFrames.Add(selectedLineNetworkFrame);
            return true;
        }

        if (hasDeferredGuideWithoutExtents && targetCandidates.Count == 0)
        {
            error = "未读取到可处理的对象。";
            return false;
        }

        if (targetCandidates.Count == 0)
        {
            error = selectedFrames.Count > 0
                ? "当前仅选中了矩形框，请同时选中需要居中的对象，或只选对象后让命令自动识别矩形框。"
                : "未读取到可处理的对象。";
            return false;
        }

        return TryBuildTargetSet(targetCandidates, out targetIds, out targetExtents);
    }

    private static bool TryBuildTargetSet(
        IReadOnlyList<TargetCandidate> candidates,
        out ObjectId[] targetIds,
        out Extents3d targetExtents)
    {
        targetIds = Array.Empty<ObjectId>();
        targetExtents = default;
        if (candidates.Count == 0)
            return false;

        var ids = new List<ObjectId>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            ids.Add(candidate.ObjectId);
            if (index == 0)
            {
                targetExtents = candidate.Extents;
                continue;
            }

            targetExtents.AddExtents(candidate.Extents);
        }

        targetIds = ids.ToArray();
        return true;
    }

    private static bool CanContributeLineNetworkGuide(
        Transaction tr,
        ObjectId objectId)
    {
        var directions = new List<Vector3d>();
        AppendGuideDirections(tr, objectId, directions);
        return directions.Count > 0;
    }

    private static bool TryResolveSelectedLineNetworkFrame(
        Transaction tr,
        IReadOnlyList<ObjectId> guideCandidateIds,
        IReadOnlyList<TargetCandidate> targetCandidates,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out ObjectId[] targetIds,
        out Extents3d targetExtents,
        out RectangleFrameInfo frameInfo)
    {
        targetIds = Array.Empty<ObjectId>();
        targetExtents = default;
        frameInfo = default;
        if (guideCandidateIds.Count == 0 || targetCandidates.Count == 0)
            return false;

        var guideIds = new HashSet<ObjectId>(guideCandidateIds);
        var targetOnlyCandidates = targetCandidates
            .Where(candidate => !guideIds.Contains(candidate.ObjectId))
            .ToList();
        if (!TryBuildTargetSet(targetOnlyCandidates, out var resolvedTargetIds, out var resolvedTargetExtents))
            return false;

        var bestFrame = default(RectangleFrameInfo);
        var resolvedTransforms = BuildLineNetworkAnalysisTransforms(tr, guideCandidateIds, analysisTransforms);
        for (var index = 0; index < resolvedTransforms.Count; index++)
        {
            var transform = resolvedTransforms[index];
            var segments = new List<AxisAlignedSegment>();
            for (var guideIndex = 0; guideIndex < guideCandidateIds.Count; guideIndex++)
                AppendAxisAlignedSegments(tr, guideCandidateIds[guideIndex], transform.WorldToAnalysis, segments);

            if (segments.Count == 0)
                continue;

            var analysisTargetExtents = TransformExtents(resolvedTargetExtents, transform.WorldToAnalysis);
            if (!TryInferAxisAlignedRectangleBounds(analysisTargetExtents, segments, out var analysisExtents))
                continue;

            var candidate = RectangleFrameInfo.CreateInferred(
                TransformExtents(analysisExtents, transform.AnalysisToWorld),
                analysisExtents.Area2d());
            UseBetterFrame(candidate, ref bestFrame);
        }

        if (!bestFrame.IsResolved)
            return false;

        targetIds = resolvedTargetIds;
        targetExtents = resolvedTargetExtents;
        frameInfo = bestFrame;
        return true;
    }

    private static bool TryFindBestContainingFrame(
        List<RectangleFrameInfo> candidates,
        Extents3d targetExtents,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!ContainsTarget(candidate, targetExtents))
                continue;

            UseBetterFrame(candidate, ref frameInfo);
        }

        return frameInfo.IsResolved;
    }

    private static void UseBetterFrame(
        RectangleFrameInfo candidate,
        string source,
        ref RectangleFrameInfo bestFrame,
        ref string bestSource)
    {
        if (!UseBetterFrame(candidate, ref bestFrame))
            return;

        bestSource = source;
    }

    private static bool UseBetterFrame(
        RectangleFrameInfo candidate,
        ref RectangleFrameInfo bestFrame)
    {
        if (!candidate.IsResolved)
            return false;

        if (bestFrame.IsResolved && candidate.Area >= bestFrame.Area - GeometryTolerance)
            return false;

        bestFrame = candidate;
        return true;
    }

    private static bool TryFindBestContainingFrameInCurrentSpace(
        Transaction tr,
        Database db,
        ObjectId[] targetIds,
        Extents3d targetExtents,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        var excluded = new HashSet<ObjectId>(targetIds);

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, db.CurrentSpaceId, OpenMode.ForRead, out var currentSpace) ||
            currentSpace == null)
        {
            return false;
        }

        foreach (ObjectId entityId in currentSpace)
        {
            if (entityId.IsNull || excluded.Contains(entityId))
                continue;

            if (!TryGetRectangleFrameInfo(tr, entityId, out var candidate))
                continue;

            if (!ContainsTarget(candidate, targetExtents))
                continue;

            UseBetterFrame(candidate, ref frameInfo);
        }

        return frameInfo.IsResolved;
    }

    private static bool TryPromptRectangleFrame(
        Document doc,
        ObjectId[] targetIds,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out RectangleFrameInfo frameInfo,
        out bool cancelled)
    {
        frameInfo = default;
        cancelled = false;
        var ed = doc.Editor;

        while (true)
        {
            var options = new PromptPointOptions("\n")
            {
                AllowNone = true
            };
            var result = ed.GetPoint(options);
            if (result.Status != PromptStatus.OK)
            {
                cancelled = result.Status is PromptStatus.Cancel or PromptStatus.None;
                return false;
            }

            var resolvedFrameInfo = default(RectangleFrameInfo);
            var isValidFrame = CadDatabaseScope.Read(doc, (db, tr) =>
                TryResolveFrameByPointInCurrentSpace(
                    tr,
                    db,
                    targetIds,
                    result.Value,
                    analysisTransforms,
                    out resolvedFrameInfo));

            if (isValidFrame)
            {
                frameInfo = resolvedFrameInfo;
                return true;
            }
        }
    }

    private static bool TryResolveFrameByPointInCurrentSpace(
        Transaction tr,
        Database db,
        ObjectId[] targetIds,
        Point3d pickPoint,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out RectangleFrameInfo frameInfo)
    {
        if (TryFindBestContainingFrameAtPointInCurrentSpace(tr, db, targetIds, pickPoint, out frameInfo))
            return true;

        return TryInferContainingFrameFromLineNetworkAtPointInCurrentSpace(
            tr,
            db,
            targetIds,
            pickPoint,
            analysisTransforms,
            out frameInfo);
    }

    private static bool TryFindBestContainingFrameAtPointInCurrentSpace(
        Transaction tr,
        Database db,
        ObjectId[] targetIds,
        Point3d pickPoint,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        var found = false;
        var bestArea = double.MaxValue;
        var excluded = new HashSet<ObjectId>(targetIds);

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, db.CurrentSpaceId, OpenMode.ForRead, out var currentSpace) ||
            currentSpace == null)
        {
            return false;
        }

        foreach (ObjectId entityId in currentSpace)
        {
            if (entityId.IsNull || excluded.Contains(entityId))
                continue;

            if (!TryGetRectangleFrameInfo(tr, entityId, out var candidate) ||
                !ContainsFramePoint(candidate, pickPoint, AxisAlignmentTolerance))
            {
                continue;
            }

            if (!found || candidate.Area < bestArea)
            {
                frameInfo = candidate;
                bestArea = candidate.Area;
                found = true;
            }
        }

        return found;
    }

    private static bool TryInferContainingFrameFromLineNetworkAtPointInCurrentSpace(
        Transaction tr,
        Database db,
        ObjectId[] targetIds,
        Point3d pickPoint,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        var resolvedTransforms = BuildLineNetworkAnalysisTransforms(tr, db, targetIds, analysisTransforms);
        for (var index = 0; index < resolvedTransforms.Count; index++)
        {
            var transform = resolvedTransforms[index];
            if (!TryCollectAxisAlignedSegmentsInCurrentSpace(tr, db, targetIds, transform.WorldToAnalysis, out var segments) ||
                segments.Count == 0)
            {
                continue;
            }

            var analysisPoint = pickPoint.TransformBy(transform.WorldToAnalysis);
            if (!TryInferAxisAlignedRectangleBounds(analysisPoint, segments, out var analysisExtents))
                continue;

            var candidate = RectangleFrameInfo.CreateInferred(
                TransformExtents(analysisExtents, transform.AnalysisToWorld),
                analysisExtents.Area2d());

            UseBetterFrame(candidate, ref frameInfo);
        }

        return frameInfo.IsResolved;
    }

    private static bool TryMoveTargets(
        Transaction tr,
        ObjectId[] targetIds,
        Vector3d displacement,
        out int movedCount,
        out string error)
    {
        movedCount = 0;
        error = "";

        var transform = Matrix3d.Displacement(displacement);
        for (var index = 0; index < targetIds.Length; index++)
        {
            var objectId = targetIds[index];
            if (objectId.IsNull)
                continue;

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null)
            {
                continue;
            }

            if (CadDatabaseScope.IsOnLockedLayer(tr, entity))
            {
                error = $"对象所在图层“{entity.Layer}”已锁定，未执行居中。";
                return false;
            }

            if (!entity.IsWriteEnabled)
                entity.UpgradeOpen();

            entity.TransformBy(transform);
            movedCount++;
        }

        if (movedCount <= 0)
        {
            error = "未找到可移动的对象。";
            return false;
        }

        return true;
    }

    private static bool TryInferContainingFrameFromLineNetworkInCurrentSpace(
        Transaction tr,
        Database db,
        ObjectId[] targetIds,
        Extents3d targetExtents,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        var resolvedTransforms = BuildLineNetworkAnalysisTransforms(tr, db, targetIds, analysisTransforms);
        for (var index = 0; index < resolvedTransforms.Count; index++)
        {
            var transform = resolvedTransforms[index];
            if (!TryCollectAxisAlignedSegmentsInCurrentSpace(tr, db, targetIds, transform.WorldToAnalysis, out var segments) ||
                segments.Count == 0)
            {
                continue;
            }

            var analysisTargetExtents = TransformExtents(targetExtents, transform.WorldToAnalysis);
            if (!TryInferAxisAlignedRectangleBounds(analysisTargetExtents, segments, out var analysisExtents))
                continue;

            var candidate = RectangleFrameInfo.CreateInferred(
                TransformExtents(analysisExtents, transform.AnalysisToWorld),
                analysisExtents.Area2d());

            UseBetterFrame(candidate, ref frameInfo);
        }

        return frameInfo.IsResolved;
    }

    private static bool TryGetTargetExtents(
        Transaction tr,
        ObjectId[] targetIds,
        out Extents3d extents,
        out string error)
    {
        extents = default;
        error = "";
        var hasExtents = false;

        for (var index = 0; index < targetIds.Length; index++)
        {
            var objectId = targetIds[index];
            if (objectId.IsNull)
                continue;

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null)
            {
                continue;
            }

            if (!TryGetEntityExtents(entity, out var current))
            {
                error = "执行前无法重新读取对象范围，请重新选择。";
                return false;
            }

            if (!hasExtents)
            {
                extents = current;
                hasExtents = true;
            }
            else
            {
                extents.AddExtents(current);
            }
        }

        if (!hasExtents)
        {
            error = "未读取到可用的对象范围。";
            return false;
        }

        return true;
    }

    private static bool TryCollectAxisAlignedSegmentsInCurrentSpace(
        Transaction tr,
        Database db,
        ObjectId[] excludedIds,
        Matrix3d worldToAnalysis,
        out List<AxisAlignedSegment> segments)
    {
        segments = new List<AxisAlignedSegment>();
        var excluded = new HashSet<ObjectId>(excludedIds);

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, db.CurrentSpaceId, OpenMode.ForRead, out var currentSpace) ||
            currentSpace == null)
        {
            return false;
        }

        foreach (ObjectId entityId in currentSpace)
        {
            if (entityId.IsNull || excluded.Contains(entityId))
                continue;

            AppendAxisAlignedSegments(tr, entityId, worldToAnalysis, segments);
        }

        return true;
    }

    private static List<AnalysisTransform> BuildLineNetworkAnalysisTransforms(
        Transaction tr,
        Database db,
        ObjectId[] excludedIds,
        IReadOnlyList<AnalysisTransform> baseTransforms)
    {
        var directions = new List<Vector3d>();
        var excluded = new HashSet<ObjectId>(excludedIds);

        if (CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, db.CurrentSpaceId, OpenMode.ForRead, out var currentSpace) &&
            currentSpace != null)
        {
            foreach (ObjectId entityId in currentSpace)
            {
                if (entityId.IsNull || excluded.Contains(entityId))
                    continue;

                AppendGuideDirections(tr, entityId, directions);
            }
        }

        return BuildLineNetworkAnalysisTransforms(baseTransforms, directions);
    }

    private static List<AnalysisTransform> BuildLineNetworkAnalysisTransforms(
        Transaction tr,
        IReadOnlyList<ObjectId> guideIds,
        IReadOnlyList<AnalysisTransform> baseTransforms)
    {
        var directions = new List<Vector3d>();
        for (var index = 0; index < guideIds.Count; index++)
        {
            if (!guideIds[index].IsNull)
                AppendGuideDirections(tr, guideIds[index], directions);
        }

        return BuildLineNetworkAnalysisTransforms(baseTransforms, directions);
    }

    private static List<AnalysisTransform> BuildLineNetworkAnalysisTransforms(
        IReadOnlyList<AnalysisTransform> baseTransforms,
        IReadOnlyList<Vector3d> directions)
    {
        var transforms = new List<AnalysisTransform>(baseTransforms);
        var signatures = new List<double>();

        for (var index = 0; index < directions.Count; index++)
        {
            if (transforms.Count >= MaxAnalysisOrientationCount)
                break;

            if (!TryGetDirectionSignature(directions[index], out var signature) ||
                HasEquivalentDirectionSignature(signatures, signature) ||
                !TryCreateAnalysisTransformFromDirection(directions[index], out var transform))
            {
                continue;
            }

            signatures.Add(signature);
            transforms.Add(transform);
        }

        return transforms;
    }

    private static bool TryCreateAnalysisTransformFromDirection(
        Vector3d direction,
        out AnalysisTransform transform)
    {
        transform = default;
        var length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        if (length <= GeometryTolerance)
            return false;

        var xAxis = new Vector3d(direction.X / length, direction.Y / length, 0.0);
        if (xAxis.X < -GeometryTolerance ||
            (Math.Abs(xAxis.X) <= GeometryTolerance && xAxis.Y < 0.0))
        {
            xAxis = xAxis * -1.0;
        }

        var yAxis = new Vector3d(-xAxis.Y, xAxis.X, 0.0);
        var analysisToWorld = Matrix3d.AlignCoordinateSystem(
            Point3d.Origin,
            Vector3d.XAxis,
            Vector3d.YAxis,
            Vector3d.ZAxis,
            Point3d.Origin,
            xAxis,
            yAxis,
            Vector3d.ZAxis);
        transform = new AnalysisTransform(analysisToWorld.Inverse(), analysisToWorld);
        return true;
    }

    private static bool TryGetDirectionSignature(Vector3d direction, out double signature)
    {
        signature = 0.0;
        var length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        if (length <= GeometryTolerance)
            return false;

        var angle = Math.Atan2(direction.Y / length, direction.X / length);
        if (angle < 0.0)
            angle += Math.PI;

        var halfPi = Math.PI * 0.5;
        while (angle >= halfPi)
            angle -= halfPi;

        signature = angle;
        return true;
    }

    private static bool HasEquivalentDirectionSignature(IReadOnlyList<double> signatures, double signature)
    {
        for (var index = 0; index < signatures.Count; index++)
        {
            var delta = Math.Abs(signatures[index] - signature);
            delta = Math.Min(delta, (Math.PI * 0.5) - delta);
            if (delta <= AxisDirectionTolerance)
                return true;
        }

        return false;
    }

    private static void AppendAxisAlignedSegments(
        Transaction tr,
        ObjectId entityId,
        Matrix3d worldToAnalysis,
        List<AxisAlignedSegment> segments)
    {
        if (!CadDatabaseScope.TryOpenAs<Entity>(tr, entityId, OpenMode.ForRead, out var entity) ||
            entity == null)
        {
            return;
        }

        switch (entity)
        {
            case Line line:
                TryAppendAxisAlignedSegment(
                    segments,
                    line.StartPoint.TransformBy(worldToAnalysis),
                    line.EndPoint.TransformBy(worldToAnalysis));
                break;
            case Xline xline:
                AppendXlineAxisAlignedSegments(xline, worldToAnalysis, segments);
                break;
            case Ray ray:
                AppendRayAxisAlignedSegments(ray, worldToAnalysis, segments);
                break;
            case Polyline polyline:
                AppendPolylineAxisAlignedSegments(polyline, worldToAnalysis, segments);
                break;
            case Polyline2d polyline2d:
                AppendPolyline2dAxisAlignedSegments(tr, polyline2d, worldToAnalysis, segments);
                break;
            case Polyline3d polyline3d:
                AppendPolyline3dAxisAlignedSegments(tr, polyline3d, worldToAnalysis, segments);
                break;
        }
    }

    private static void AppendGuideDirections(
        Transaction tr,
        ObjectId entityId,
        List<Vector3d> directions)
    {
        if (!CadDatabaseScope.TryOpenAs<Entity>(tr, entityId, OpenMode.ForRead, out var entity) ||
            entity == null)
        {
            return;
        }

        switch (entity)
        {
            case Line line:
                TryAppendGuideDirection(directions, line.EndPoint - line.StartPoint);
                break;
            case Xline xline:
                TryAppendGuideDirection(directions, xline.UnitDir);
                break;
            case Ray ray:
                TryAppendGuideDirection(directions, ray.UnitDir);
                break;
            case Polyline polyline:
                AppendPolylineGuideDirections(polyline, directions);
                break;
            case Polyline2d polyline2d:
                AppendPolyline2dGuideDirections(tr, polyline2d, directions);
                break;
            case Polyline3d polyline3d:
                AppendPolyline3dGuideDirections(tr, polyline3d, directions);
                break;
        }
    }

    private static void AppendPolylineGuideDirections(
        Polyline polyline,
        List<Vector3d> directions)
    {
        if (polyline.NumberOfVertices < 2)
            return;

        var segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            if (Math.Abs(polyline.GetBulgeAt(index)) > GeometryTolerance)
                continue;

            var nextIndex = (index + 1) % polyline.NumberOfVertices;
            TryAppendGuideDirection(
                directions,
                polyline.GetPoint3dAt(nextIndex) - polyline.GetPoint3dAt(index));
        }
    }

    private static void AppendPolyline2dGuideDirections(
        Transaction tr,
        Polyline2d polyline2d,
        List<Vector3d> directions)
    {
        var vertices = new List<(Point3d Point, double Bulge)>();
        foreach (ObjectId vertexId in polyline2d)
        {
            if (!CadDatabaseScope.TryOpenAs<Vertex2d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return;
            }

            vertices.Add((vertex.Position, vertex.Bulge));
        }

        if (vertices.Count < 2)
            return;

        var segmentCount = polyline2d.Closed ? vertices.Count : vertices.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            if (Math.Abs(vertices[index].Bulge) > GeometryTolerance)
                continue;

            var nextIndex = (index + 1) % vertices.Count;
            TryAppendGuideDirection(directions, vertices[nextIndex].Point - vertices[index].Point);
        }
    }

    private static void AppendPolyline3dGuideDirections(
        Transaction tr,
        Polyline3d polyline3d,
        List<Vector3d> directions)
    {
        var vertices = new List<Point3d>();
        foreach (ObjectId vertexId in polyline3d)
        {
            if (!CadDatabaseScope.TryOpenAs<PolylineVertex3d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return;
            }

            vertices.Add(vertex.Position);
        }

        if (vertices.Count < 2)
            return;

        var segmentCount = polyline3d.Closed ? vertices.Count : vertices.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var nextIndex = (index + 1) % vertices.Count;
            TryAppendGuideDirection(directions, vertices[nextIndex] - vertices[index]);
        }
    }

    private static void TryAppendGuideDirection(
        List<Vector3d> directions,
        Vector3d direction)
    {
        if (((direction.X * direction.X) + (direction.Y * direction.Y)) <= GeometryTolerance * GeometryTolerance)
            return;

        directions.Add(direction);
    }

    private static void AppendPolylineAxisAlignedSegments(
        Polyline polyline,
        Matrix3d worldToAnalysis,
        List<AxisAlignedSegment> segments)
    {
        if (polyline.NumberOfVertices < 2)
            return;

        var segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            if (Math.Abs(polyline.GetBulgeAt(index)) > GeometryTolerance)
                continue;

            var nextIndex = (index + 1) % polyline.NumberOfVertices;
            TryAppendAxisAlignedSegment(
                segments,
                polyline.GetPoint3dAt(index).TransformBy(worldToAnalysis),
                polyline.GetPoint3dAt(nextIndex).TransformBy(worldToAnalysis));
        }
    }

    private static void AppendPolyline2dAxisAlignedSegments(
        Transaction tr,
        Polyline2d polyline2d,
        Matrix3d worldToAnalysis,
        List<AxisAlignedSegment> segments)
    {
        var vertices = new List<(Point3d Point, double Bulge)>();
        foreach (ObjectId vertexId in polyline2d)
        {
            if (!CadDatabaseScope.TryOpenAs<Vertex2d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return;
            }

            vertices.Add((vertex.Position, vertex.Bulge));
        }

        if (vertices.Count < 2)
            return;

        var segmentCount = polyline2d.Closed ? vertices.Count : vertices.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            if (Math.Abs(vertices[index].Bulge) > GeometryTolerance)
                continue;

            var nextIndex = (index + 1) % vertices.Count;
            TryAppendAxisAlignedSegment(
                segments,
                vertices[index].Point.TransformBy(worldToAnalysis),
                vertices[nextIndex].Point.TransformBy(worldToAnalysis));
        }
    }

    private static void AppendPolyline3dAxisAlignedSegments(
        Transaction tr,
        Polyline3d polyline3d,
        Matrix3d worldToAnalysis,
        List<AxisAlignedSegment> segments)
    {
        var vertices = new List<Point3d>();
        foreach (ObjectId vertexId in polyline3d)
        {
            if (!CadDatabaseScope.TryOpenAs<PolylineVertex3d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return;
            }

            vertices.Add(vertex.Position);
        }

        if (vertices.Count < 2)
            return;

        var segmentCount = polyline3d.Closed ? vertices.Count : vertices.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var nextIndex = (index + 1) % vertices.Count;
            TryAppendAxisAlignedSegment(
                segments,
                vertices[index].TransformBy(worldToAnalysis),
                vertices[nextIndex].TransformBy(worldToAnalysis));
        }
    }

    private static void TryAppendAxisAlignedSegment(
        List<AxisAlignedSegment> segments,
        Point3d startPoint,
        Point3d endPoint)
    {
        var deltaX = endPoint.X - startPoint.X;
        var deltaY = endPoint.Y - startPoint.Y;
        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (length <= GeometryTolerance)
            return;

        if (Math.Abs(deltaX / length) <= AxisDirectionTolerance && Math.Abs(deltaY) > GeometryTolerance)
        {
            segments.Add(AxisAlignedSegment.CreateVertical(
                (startPoint.X + endPoint.X) * 0.5,
                startPoint.Y,
                endPoint.Y));
            return;
        }

        if (Math.Abs(deltaY / length) <= AxisDirectionTolerance && Math.Abs(deltaX) > GeometryTolerance)
        {
            segments.Add(AxisAlignedSegment.CreateHorizontal(
                (startPoint.Y + endPoint.Y) * 0.5,
                startPoint.X,
                endPoint.X));
        }
    }

    private static void AppendXlineAxisAlignedSegments(
        Xline xline,
        Matrix3d worldToAnalysis,
        List<AxisAlignedSegment> segments)
    {
        if (TryCreateAxisAlignedDirectionalSegment(
                xline.BasePoint.TransformBy(worldToAnalysis),
                xline.UnitDir.TransformBy(worldToAnalysis),
                isRay: false,
                out var segment))
        {
            segments.Add(segment);
        }
    }

    private static void AppendRayAxisAlignedSegments(
        Ray ray,
        Matrix3d worldToAnalysis,
        List<AxisAlignedSegment> segments)
    {
        if (TryCreateAxisAlignedDirectionalSegment(
                ray.BasePoint.TransformBy(worldToAnalysis),
                ray.UnitDir.TransformBy(worldToAnalysis),
                isRay: true,
                out var segment))
        {
            segments.Add(segment);
        }
    }

    internal static bool TryCreateAxisAlignedDirectionalSegment(
        Point3d basePoint,
        Vector3d direction,
        bool isRay,
        out AxisAlignedSegment segment)
    {
        segment = default;
        if (direction.Length <= GeometryTolerance)
            return false;

        var unitDirection = direction.GetNormal();
        if (Math.Abs(unitDirection.X) <= AxisDirectionTolerance &&
            Math.Abs(unitDirection.Y) > GeometryTolerance)
        {
            var startY = isRay && unitDirection.Y >= 0.0 ? basePoint.Y : double.NegativeInfinity;
            var endY = isRay && unitDirection.Y < 0.0 ? basePoint.Y : double.PositiveInfinity;
            segment = AxisAlignedSegment.CreateVertical(basePoint.X, startY, endY);
            return true;
        }

        if (Math.Abs(unitDirection.Y) <= AxisDirectionTolerance &&
            Math.Abs(unitDirection.X) > GeometryTolerance)
        {
            var startX = isRay && unitDirection.X >= 0.0 ? basePoint.X : double.NegativeInfinity;
            var endX = isRay && unitDirection.X < 0.0 ? basePoint.X : double.PositiveInfinity;
            segment = AxisAlignedSegment.CreateHorizontal(basePoint.Y, startX, endX);
            return true;
        }

        return false;
    }

    private static bool TryGetRectangleFrameInfo(Transaction tr, ObjectId objectId, out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        if (objectId.IsNull)
            return false;

        if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
            entity == null)
        {
            return false;
        }

        return entity switch
        {
            Polyline polyline => TryCreatePolylineFrameInfo(objectId, polyline, out frameInfo),
            Polyline2d polyline2d => TryCreatePolyline2dFrameInfo(tr, objectId, polyline2d, out frameInfo),
            Polyline3d polyline3d => TryCreatePolyline3dFrameInfo(tr, objectId, polyline3d, out frameInfo),
            _ => false
        };
    }

    private static bool TryCreatePolylineFrameInfo(ObjectId objectId, Polyline polyline, out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        if (!polyline.Closed || polyline.NumberOfVertices < 3)
            return false;

        if (!TryCollectPolylineBoundaryPoints(polyline, out var points))
            return false;

        return TryCreateFrameInfo(objectId, polyline, points, out frameInfo);
    }

    private static bool TryCreatePolyline2dFrameInfo(
        Transaction tr,
        ObjectId objectId,
        Polyline2d polyline2d,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        if (!polyline2d.Closed || !TryCollectPolyline2dVertices(tr, polyline2d, out var points))
            return false;

        return TryCreateFrameInfo(objectId, polyline2d, points, out frameInfo);
    }

    private static bool TryCreatePolyline3dFrameInfo(
        Transaction tr,
        ObjectId objectId,
        Polyline3d polyline3d,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        if (!polyline3d.Closed || !TryCollectPolyline3dVertices(tr, polyline3d, out var points))
            return false;

        return TryCreateFrameInfo(objectId, polyline3d, points, out frameInfo);
    }

    private static bool TryCollectPolylineBoundaryPoints(Polyline polyline, out Point3d[] points)
    {
        points = Array.Empty<Point3d>();
        if (polyline.NumberOfVertices < 3)
            return false;

        var list = new List<Point3d>();
        var segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var nextIndex = (index + 1) % polyline.NumberOfVertices;
            AddPointIfDistinct(list, polyline.GetPoint3dAt(index));

            var bulge = polyline.GetBulgeAt(index);
            if (Math.Abs(bulge) <= GeometryTolerance)
            {
                AddPointIfDistinct(list, polyline.GetPoint3dAt(nextIndex));
                continue;
            }

            var divisions = ResolveBulgeSegmentCount(Math.Abs(4.0 * Math.Atan(bulge)));
            for (var step = 1; step <= divisions; step++)
            {
                try
                {
                    AddPointIfDistinct(list, polyline.GetPointAtParameter(index + (step / (double)divisions)));
                }
                catch (InvalidOperationException)
                {
                    AddPointIfDistinct(list, polyline.GetPoint3dAt(nextIndex));
                    break;
                }
                catch (AcadRuntimeException)
                {
                    AddPointIfDistinct(list, polyline.GetPoint3dAt(nextIndex));
                    break;
                }
            }
        }

        RemoveDuplicateClosingPoint(list);
        if (list.Count < 3)
            return false;

        points = list.ToArray();
        return true;
    }

    private static bool TryCollectPolyline2dVertices(Transaction tr, Polyline2d polyline2d, out Point3d[] points)
    {
        points = Array.Empty<Point3d>();
        var list = new List<Point3d>();

        foreach (ObjectId vertexId in polyline2d)
        {
            if (!CadDatabaseScope.TryOpenAs<Vertex2d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return false;
            }

            if (Math.Abs(vertex.Bulge) > GeometryTolerance)
                return false;

            AddPointIfDistinct(list, vertex.Position);
        }

        RemoveDuplicateClosingPoint(list);
        if (list.Count < 3)
            return false;

        points = list.ToArray();
        return true;
    }

    private static bool TryCollectPolyline3dVertices(Transaction tr, Polyline3d polyline3d, out Point3d[] points)
    {
        points = Array.Empty<Point3d>();
        var list = new List<Point3d>();

        foreach (ObjectId vertexId in polyline3d)
        {
            if (!CadDatabaseScope.TryOpenAs<PolylineVertex3d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return false;
            }

            AddPointIfDistinct(list, vertex.Position);
        }

        RemoveDuplicateClosingPoint(list);
        if (list.Count < 3)
            return false;

        points = list.ToArray();
        return true;
    }

    private static bool TryCreateFrameInfo(
        ObjectId objectId,
        Entity entity,
        Point3d[] points,
        out RectangleFrameInfo frameInfo)
    {
        frameInfo = default;
        if (!PlanarPolygonGeometry.TryComputeCentroid(points, out var center, out var area, GeometryTolerance) ||
            !TryGetEntityExtents(entity, out var extents))
        {
            return false;
        }

        frameInfo = RectangleFrameInfo.CreateEntityBacked(objectId, center, extents, area, points);
        return true;
    }

    private static void AddPointIfDistinct(List<Point3d> points, Point3d point)
    {
        if (points.Count > 0 && points[^1].DistanceTo(point) <= GeometryTolerance)
            return;

        points.Add(point);
    }

    private static void RemoveDuplicateClosingPoint(List<Point3d> points)
    {
        if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= GeometryTolerance)
            points.RemoveAt(points.Count - 1);
    }

    private static int ResolveBulgeSegmentCount(double includedAngle)
    {
        if (double.IsNaN(includedAngle) || double.IsInfinity(includedAngle) || includedAngle <= GeometryTolerance)
            return 1;

        var count = (int)Math.Ceiling(includedAngle / (Math.PI / 18.0));
        if (count < 4)
            return 4;

        return count > 96 ? 96 : count;
    }

    internal static bool TryInferAxisAlignedRectangleBounds(
        Extents3d targetExtents,
        IReadOnlyList<AxisAlignedSegment> segments,
        out Extents3d frameExtents)
    {
        if (TryInferClosedAxisAlignedRectangleBounds(targetExtents, segments, out frameExtents))
            return true;

        return TryInferCenterCrossingAxisAlignedRectangleBounds(targetExtents, segments, out frameExtents);
    }

    private static bool TryInferClosedAxisAlignedRectangleBounds(
        Extents3d targetExtents,
        IReadOnlyList<AxisAlignedSegment> segments,
        out Extents3d frameExtents)
    {
        frameExtents = default;
        if (segments.Count == 0)
            return false;

        var minPoint = targetExtents.MinPoint;
        var maxPoint = targetExtents.MaxPoint;
        var center = GetExtentsCenter(targetExtents);
        var verticalLines = BuildCoverageLines(segments, isVertical: true);
        var horizontalLines = BuildCoverageLines(segments, isVertical: false);
        if (verticalLines.Count == 0 || horizontalLines.Count == 0)
            return false;

        var leftCandidates = verticalLines
            .Where(line =>
                line.Coordinate <= minPoint.X + AxisAlignmentTolerance &&
                line.CoversValue(center.Y, AxisMergeTolerance))
            .OrderByDescending(line => line.Coordinate)
            .ToList();
        var rightCandidates = verticalLines
            .Where(line =>
                line.Coordinate >= maxPoint.X - AxisAlignmentTolerance &&
                line.CoversValue(center.Y, AxisMergeTolerance))
            .OrderBy(line => line.Coordinate)
            .ToList();
        if (leftCandidates.Count == 0 || rightCandidates.Count == 0)
            return false;

        var found = false;
        var bestArea = double.MaxValue;
        foreach (var leftLine in leftCandidates)
        {
            foreach (var rightLine in rightCandidates)
            {
                if (rightLine.Coordinate <= leftLine.Coordinate + GeometryTolerance)
                    continue;

                var bottomLine = horizontalLines
                    .Where(line =>
                        line.Coordinate <= minPoint.Y + AxisAlignmentTolerance &&
                        line.CoversInterval(leftLine.Coordinate, rightLine.Coordinate, AxisMergeTolerance))
                    .OrderByDescending(line => line.Coordinate)
                    .FirstOrDefault();
                if (bottomLine.IsEmpty)
                    continue;

                var topLine = horizontalLines
                    .Where(line =>
                        line.Coordinate >= maxPoint.Y - AxisAlignmentTolerance &&
                        line.CoversInterval(leftLine.Coordinate, rightLine.Coordinate, AxisMergeTolerance))
                    .OrderBy(line => line.Coordinate)
                    .FirstOrDefault();
                if (topLine.IsEmpty || topLine.Coordinate <= bottomLine.Coordinate + GeometryTolerance)
                    continue;

                if (!leftLine.CoversInterval(bottomLine.Coordinate, topLine.Coordinate, AxisMergeTolerance) ||
                    !rightLine.CoversInterval(bottomLine.Coordinate, topLine.Coordinate, AxisMergeTolerance))
                {
                    continue;
                }

                var candidateExtents = new Extents3d(
                    new Point3d(leftLine.Coordinate, bottomLine.Coordinate, minPoint.Z),
                    new Point3d(rightLine.Coordinate, topLine.Coordinate, maxPoint.Z));
                if (!ContainsExtents(candidateExtents, targetExtents, AxisAlignmentTolerance))
                    continue;

                var area = (rightLine.Coordinate - leftLine.Coordinate) * (topLine.Coordinate - bottomLine.Coordinate);
                if (found && area >= bestArea - GeometryTolerance)
                    continue;

                bestArea = area;
                frameExtents = candidateExtents;
                found = true;
            }
        }

        return found;
    }

    private static bool TryInferCenterCrossingAxisAlignedRectangleBounds(
        Extents3d targetExtents,
        IReadOnlyList<AxisAlignedSegment> segments,
        out Extents3d frameExtents)
    {
        frameExtents = default;
        if (segments.Count == 0)
            return false;

        var minPoint = targetExtents.MinPoint;
        var maxPoint = targetExtents.MaxPoint;
        var center = GetExtentsCenter(targetExtents);
        var verticalLines = BuildCoverageLines(segments, isVertical: true);
        var horizontalLines = BuildCoverageLines(segments, isVertical: false);
        if (verticalLines.Count == 0 || horizontalLines.Count == 0)
            return false;

        var leftCandidates = verticalLines
            .Where(line =>
                line.Coordinate <= minPoint.X + AxisAlignmentTolerance &&
                line.CoversValue(center.Y, AxisMergeTolerance))
            .OrderByDescending(line => line.Coordinate)
            .ToList();
        var rightCandidates = verticalLines
            .Where(line =>
                line.Coordinate >= maxPoint.X - AxisAlignmentTolerance &&
                line.CoversValue(center.Y, AxisMergeTolerance))
            .OrderBy(line => line.Coordinate)
            .ToList();
        var bottomCandidates = horizontalLines
            .Where(line =>
                line.Coordinate <= minPoint.Y + AxisAlignmentTolerance &&
                line.CoversValue(center.X, AxisMergeTolerance))
            .OrderByDescending(line => line.Coordinate)
            .ToList();
        var topCandidates = horizontalLines
            .Where(line =>
                line.Coordinate >= maxPoint.Y - AxisAlignmentTolerance &&
                line.CoversValue(center.X, AxisMergeTolerance))
            .OrderBy(line => line.Coordinate)
            .ToList();
        if (leftCandidates.Count == 0 ||
            rightCandidates.Count == 0 ||
            bottomCandidates.Count == 0 ||
            topCandidates.Count == 0)
        {
            return false;
        }

        var found = false;
        var bestArea = double.MaxValue;
        foreach (var leftLine in leftCandidates)
        {
            foreach (var rightLine in rightCandidates)
            {
                if (rightLine.Coordinate <= leftLine.Coordinate + GeometryTolerance)
                    continue;

                foreach (var bottomLine in bottomCandidates)
                {
                    foreach (var topLine in topCandidates)
                    {
                        if (topLine.Coordinate <= bottomLine.Coordinate + GeometryTolerance)
                            continue;

                        var candidateExtents = new Extents3d(
                            new Point3d(leftLine.Coordinate, bottomLine.Coordinate, minPoint.Z),
                            new Point3d(rightLine.Coordinate, topLine.Coordinate, maxPoint.Z));
                        if (!ContainsExtents(candidateExtents, targetExtents, AxisAlignmentTolerance))
                            continue;

                        var area = (rightLine.Coordinate - leftLine.Coordinate) *
                                   (topLine.Coordinate - bottomLine.Coordinate);
                        if (found && area >= bestArea - GeometryTolerance)
                            continue;

                        bestArea = area;
                        frameExtents = candidateExtents;
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    internal static bool TryInferAxisAlignedRectangleBounds(
        Point3d samplePoint,
        IReadOnlyList<AxisAlignedSegment> segments,
        out Extents3d frameExtents)
    {
        var tolerance = Math.Max(AxisAlignmentTolerance, GeometryTolerance) * 0.5;
        var probeExtents = new Extents3d(
            new Point3d(samplePoint.X - tolerance, samplePoint.Y - tolerance, samplePoint.Z),
            new Point3d(samplePoint.X + tolerance, samplePoint.Y + tolerance, samplePoint.Z));
        return TryInferAxisAlignedRectangleBounds(probeExtents, segments, out frameExtents);
    }

    private static List<AxisAlignedCoverageLine> BuildCoverageLines(
        IReadOnlyList<AxisAlignedSegment> segments,
        bool isVertical)
    {
        var candidates = segments
            .Where(segment => segment.IsVertical == isVertical)
            .OrderBy(segment => segment.Coordinate)
            .ThenBy(segment => segment.RangeStart)
            .ToList();
        var lines = new List<AxisAlignedCoverageLine>();
        for (var index = 0; index < candidates.Count;)
        {
            var groupCoordinate = candidates[index].Coordinate;
            var group = new List<AxisAlignedSegment> { candidates[index] };
            index++;
            while (index < candidates.Count &&
                   Math.Abs(candidates[index].Coordinate - groupCoordinate) <= AxisAlignmentTolerance)
            {
                group.Add(candidates[index]);
                index++;
            }

            lines.Add(AxisAlignedCoverageLine.Create(isVertical, group));
        }

        return lines;
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

    private static bool ContainsExtents(Extents3d outer, Extents3d inner, double tolerance = GeometryTolerance)
    {
        return outer.MinPoint.X <= inner.MinPoint.X + tolerance &&
               outer.MinPoint.Y <= inner.MinPoint.Y + tolerance &&
               outer.MaxPoint.X >= inner.MaxPoint.X - tolerance &&
               outer.MaxPoint.Y >= inner.MaxPoint.Y - tolerance;
    }

    private static bool ContainsTarget(RectangleFrameInfo frameInfo, Extents3d targetExtents)
    {
        if (frameInfo.HasBoundary)
            return ContainsBoundaryTarget(frameInfo.BoundaryPoints, targetExtents, AxisAlignmentTolerance);

        return ContainsExtents(frameInfo.Extents, targetExtents);
    }

    private static bool ContainsBoundaryTarget(
        IReadOnlyList<Point3d> boundaryPoints,
        Extents3d targetExtents,
        double tolerance)
    {
        var targetCorners = new[]
        {
            new Point3d(targetExtents.MinPoint.X, targetExtents.MinPoint.Y, targetExtents.MinPoint.Z),
            new Point3d(targetExtents.MinPoint.X, targetExtents.MaxPoint.Y, targetExtents.MinPoint.Z),
            new Point3d(targetExtents.MaxPoint.X, targetExtents.MinPoint.Y, targetExtents.MinPoint.Z),
            new Point3d(targetExtents.MaxPoint.X, targetExtents.MaxPoint.Y, targetExtents.MinPoint.Z)
        };

        for (var index = 0; index < targetCorners.Length; index++)
        {
            if (!PlanarPolygonGeometry.ContainsPoint(boundaryPoints, targetCorners[index], tolerance))
                return false;
        }

        return true;
    }

    private static bool ContainsFramePoint(RectangleFrameInfo frameInfo, Point3d point, double tolerance = GeometryTolerance)
    {
        if (frameInfo.HasBoundary)
            return PlanarPolygonGeometry.ContainsPoint(frameInfo.BoundaryPoints, point, tolerance);

        return ContainsPoint(frameInfo.Extents, point, tolerance);
    }

    private static bool ContainsPoint(Extents3d extents, Point3d point, double tolerance = GeometryTolerance)
    {
        return point.X >= extents.MinPoint.X - tolerance &&
               point.X <= extents.MaxPoint.X + tolerance &&
               point.Y >= extents.MinPoint.Y - tolerance &&
               point.Y <= extents.MaxPoint.Y + tolerance;
    }

    private static IReadOnlyList<AnalysisTransform> BuildAnalysisTransforms(Matrix3d currentUserCoordinateSystem)
    {
        return new[]
        {
            new AnalysisTransform(Matrix3d.Identity, Matrix3d.Identity),
            new AnalysisTransform(currentUserCoordinateSystem.Inverse(), currentUserCoordinateSystem)
        };
    }

    private static Extents3d TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var corners = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.Z),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MaxPoint.Z),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z)
        };

        var transformedPoints = corners
            .Select(point => point.TransformBy(transform))
            .ToArray();
        return CreateExtents(transformedPoints);
    }

    private static Extents3d CreateExtents(IReadOnlyList<Point3d> points)
    {
        if (points.Count == 0)
            return default;

        var minX = points[0].X;
        var minY = points[0].Y;
        var minZ = points[0].Z;
        var maxX = points[0].X;
        var maxY = points[0].Y;
        var maxZ = points[0].Z;
        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            minZ = Math.Min(minZ, point.Z);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
            maxZ = Math.Max(maxZ, point.Z);
        }

        return new Extents3d(
            new Point3d(minX, minY, minZ),
            new Point3d(maxX, maxY, maxZ));
    }

    private static Point3d GetExtentsCenter(Extents3d extents)
    {
        return new Point3d(
            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
            (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
    }

    internal readonly struct AxisAlignedSegment
    {
        private AxisAlignedSegment(bool isVertical, double coordinate, double rangeStart, double rangeEnd)
        {
            IsVertical = isVertical;
            Coordinate = coordinate;
            RangeStart = Math.Min(rangeStart, rangeEnd);
            RangeEnd = Math.Max(rangeStart, rangeEnd);
        }

        internal bool IsVertical { get; }
        internal double Coordinate { get; }
        internal double RangeStart { get; }
        internal double RangeEnd { get; }

        internal static AxisAlignedSegment CreateVertical(double x, double startY, double endY)
            => new(true, x, startY, endY);

        internal static AxisAlignedSegment CreateHorizontal(double y, double startX, double endX)
            => new(false, y, startX, endX);
    }

    private readonly struct AnalysisTransform
    {
        internal AnalysisTransform(Matrix3d worldToAnalysis, Matrix3d analysisToWorld)
        {
            WorldToAnalysis = worldToAnalysis;
            AnalysisToWorld = analysisToWorld;
        }

        internal Matrix3d WorldToAnalysis { get; }
        internal Matrix3d AnalysisToWorld { get; }
    }

    private readonly struct AxisAlignedCoverageLine
    {
        private AxisAlignedCoverageLine(bool isVertical, double coordinate, CoverageRange[] ranges)
        {
            IsVertical = isVertical;
            Coordinate = coordinate;
            Ranges = ranges;
        }

        internal bool IsVertical { get; }
        internal double Coordinate { get; }
        private CoverageRange[] Ranges { get; }
        internal bool IsEmpty => Ranges == null || Ranges.Length == 0;

        internal bool CoversValue(double value, double tolerance)
        {
            if (Ranges == null)
                return false;

            for (var index = 0; index < Ranges.Length; index++)
            {
                var range = Ranges[index];
                if (value >= range.Start - tolerance && value <= range.End + tolerance)
                    return true;
            }

            return false;
        }

        internal bool CoversInterval(double start, double end, double tolerance)
        {
            if (Ranges == null)
                return false;

            var min = Math.Min(start, end);
            var max = Math.Max(start, end);
            for (var index = 0; index < Ranges.Length; index++)
            {
                var range = Ranges[index];
                if (range.Start <= min + tolerance && range.End >= max - tolerance)
                    return true;
            }

            return false;
        }

        internal static AxisAlignedCoverageLine Create(bool isVertical, List<AxisAlignedSegment> segments)
        {
            if (segments.Count == 0)
                return new AxisAlignedCoverageLine(isVertical, 0.0, Array.Empty<CoverageRange>());

            var ordered = segments
                .OrderBy(segment => segment.RangeStart)
                .ToList();
            var mergedRanges = new List<CoverageRange>();
            var currentStart = ordered[0].RangeStart;
            var currentEnd = ordered[0].RangeEnd;
            for (var index = 1; index < ordered.Count; index++)
            {
                var segment = ordered[index];
                if (segment.RangeStart <= currentEnd + AxisMergeTolerance)
                {
                    currentEnd = Math.Max(currentEnd, segment.RangeEnd);
                    continue;
                }

                mergedRanges.Add(new CoverageRange(currentStart, currentEnd));
                currentStart = segment.RangeStart;
                currentEnd = segment.RangeEnd;
            }

            mergedRanges.Add(new CoverageRange(currentStart, currentEnd));
            return new AxisAlignedCoverageLine(
                isVertical,
                segments.Average(segment => segment.Coordinate),
                mergedRanges.ToArray());
        }
    }

    private readonly struct CoverageRange
    {
        internal CoverageRange(double start, double end)
        {
            Start = Math.Min(start, end);
            End = Math.Max(start, end);
        }

        internal double Start { get; }
        internal double End { get; }
    }

    private readonly struct TargetCandidate
    {
        internal TargetCandidate(ObjectId objectId, Extents3d extents)
        {
            ObjectId = objectId;
            Extents = extents;
        }

        internal ObjectId ObjectId { get; }
        internal Extents3d Extents { get; }
    }

    private readonly struct RectangleFrameInfo
    {
        private RectangleFrameInfo(
            ObjectId objectId,
            Point3d center,
            Extents3d extents,
            double area,
            bool requiresEntityValidation,
            Point3d[] boundaryPoints)
        {
            ObjectId = objectId;
            Center = center;
            Extents = extents;
            Area = area;
            RequiresEntityValidation = requiresEntityValidation;
            BoundaryPoints = boundaryPoints;
        }

        internal static RectangleFrameInfo CreateEntityBacked(
            ObjectId objectId,
            Point3d center,
            Extents3d extents,
            double area,
            IReadOnlyList<Point3d> boundaryPoints)
            => new(objectId, center, extents, area, requiresEntityValidation: true, boundaryPoints.ToArray());

        internal static RectangleFrameInfo CreateInferred(Extents3d extents)
            => new(
                ObjectId.Null,
                GetExtentsCenter(extents),
                extents,
                GetExtentsArea(extents),
                requiresEntityValidation: false,
                Array.Empty<Point3d>());

        internal static RectangleFrameInfo CreateInferred(Extents3d extents, double area)
            => new(
                ObjectId.Null,
                GetExtentsCenter(extents),
                extents,
                area,
                requiresEntityValidation: false,
                Array.Empty<Point3d>());

        internal bool IsResolved => Area > GeometryTolerance;
        internal bool RequiresEntityValidation { get; }
        internal ObjectId ObjectId { get; }
        internal Point3d Center { get; }
        internal Extents3d Extents { get; }
        internal double Area { get; }
        internal IReadOnlyList<Point3d> BoundaryPoints { get; }
        internal bool HasBoundary => BoundaryPoints != null && BoundaryPoints.Count >= 3;
    }

    private static double GetExtentsArea(Extents3d extents)
        => Math.Max(0.0, extents.MaxPoint.X - extents.MinPoint.X) *
           Math.Max(0.0, extents.MaxPoint.Y - extents.MinPoint.Y);

    private static double Area2d(this Extents3d extents)
        => Math.Max(0.0, extents.MaxPoint.X - extents.MinPoint.X) *
           Math.Max(0.0, extents.MaxPoint.Y - extents.MinPoint.Y);
}
