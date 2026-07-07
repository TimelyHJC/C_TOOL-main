using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Gi = Autodesk.AutoCAD.GraphicsInterface;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static class QuickWallCommandService
{
    private const string CommandName = PluginCommandIds.QuickWall;
    private const string SettingsKeyword = "S";
    private const string SettingsKeywordDisplay = "设置";
    private const string ToggleLayerModeKeyword = "X";
    private const string SwapWidthsKeyword = "F";
    private const string SwapWidthsKeywordDisplay = "宽度互换";
    private const string QuickWallHatchDescription = "新隔墙填充";
    private const string SolidPatternName = "SOLID";
    private const double PointTolerance = 1e-6;
    private const short GuidePreviewColorIndex = 8;

    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var settings = QuickWallSettingsStore.LoadOrDefault();

        try
        {
            while (true)
            {
                var guideStatus = PromptGuidePolyline(doc, ref settings, out var guidePolyline);
                if (guideStatus == PathPromptStatus.EndCommand)
                    return;

                if (guideStatus == PathPromptStatus.Cancel || guidePolyline == null)
                {
                    ed.WriteMessage($"\nC_TOOL：{CommandName} 已取消。");
                    return;
                }

                using (guidePolyline)
                {
                    if (!PromptPrimarySideWithPreview(doc, guidePolyline, ref settings, out var primarySideSign))
                    {
                        ed.WriteMessage($"\nC_TOOL：{CommandName} 已取消。");
                        return;
                    }

                    var resolved = ResolveSettings(doc, settings);
                    CreateWallEntities(doc, guidePolyline, primarySideSign, resolved);
                    ed.WriteMessage(
                        $"\nC_TOOL：墙体已生成。线层 [{resolved.WallLayerName}]，填充层 [{resolved.HatchLayerName}]，宽度 {FormatWidthSummary(resolved)}，颜色 {FormatColorSummary(resolved)}。指定下一墙体第一点，回车结束。");
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（参数错误）", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
    }

    private static PathPromptStatus PromptGuidePolyline(
        Document doc,
        ref QuickWallSettingsDto settings,
        out Polyline? guidePolyline)
    {
        guidePolyline = null;
        var ed = doc.Editor;
        var points = new List<Point3d>();
        double baseZ = 0;
        var hasBaseZ = false;

        while (true)
        {
            var pointPrompt = PromptGuidePoint(ed, points, settings);
            switch (pointPrompt.Action)
            {
                case GuidePointPromptAction.Point:
                    var nextPoint = pointPrompt.Point;
                    if (!hasBaseZ)
                    {
                        baseZ = nextPoint.Z;
                        hasBaseZ = true;
                    }

                    nextPoint = FlattenPoint(nextPoint, baseZ);
                    if (points.Count > 0 && points[^1].DistanceTo(nextPoint) <= PointTolerance)
                    {
                        ed.WriteMessage("\nC_TOOL：相邻点不能重合，请重新指定。");
                        continue;
                    }

                    points.Add(nextPoint);
                    break;

                case GuidePointPromptAction.Settings:
                    ShowSettingsDialog(doc, ref settings);
                    break;

                case GuidePointPromptAction.ToggleMode:
                    ed.WriteMessage("\nC_TOOL：" + ToggleLayerMode(settings));
                    break;

                case GuidePointPromptAction.SwapWidths:
                    ed.WriteMessage("\nC_TOOL：" + SwapConfiguredWidths(settings));
                    break;

                case GuidePointPromptAction.Finish:
                    if (points.Count == 0)
                        return PathPromptStatus.EndCommand;

                    if (points.Count < 2)
                    {
                        ed.WriteMessage("\nC_TOOL：至少需要两个点。");
                        continue;
                    }

                    guidePolyline = CreateGuidePolyline(points);
                    return PathPromptStatus.Success;

                default:
                    return PathPromptStatus.Cancel;
            }
        }
    }

    private static GuidePointPromptResult PromptGuidePoint(
        Editor ed,
        IReadOnlyList<Point3d> confirmedPoints,
        QuickWallSettingsDto settings)
    {
        return confirmedPoints.Count == 0
            ? PromptFirstGuidePoint(ed, settings)
            : PromptNextGuidePointWithPreview(ed, confirmedPoints, settings);
    }

    private static GuidePointPromptResult PromptFirstGuidePoint(Editor ed, QuickWallSettingsDto settings)
    {
        var options = new PromptPointOptions(GetFirstGuidePointPromptMessage(settings))
        {
            AllowNone = true,
            AppendKeywordsToMessage = true
        };
        AddGuideKeywords(options.Keywords, GetLayerModeDisplay(settings));

        var result = ed.GetPoint(options);
        if (result.Status == PromptStatus.OK)
            return GuidePointPromptResult.FromPoint(result.Value);

        if (result.Status == PromptStatus.Keyword)
        {
            var keyword = NormalizeKeyword(result.StringResult);
            if (IsSettingsKeyword(keyword))
                return GuidePointPromptResult.ForSettings();

            if (IsToggleLayerModeKeyword(keyword, GetLayerModeDisplay(settings)))
                return GuidePointPromptResult.ForToggleMode();

            if (IsSwapWidthsKeyword(keyword))
                return GuidePointPromptResult.ForSwapWidths();
        }

        return result.Status switch
        {
            PromptStatus.None => GuidePointPromptResult.ForFinish(),
            _ => GuidePointPromptResult.ForCancel()
        };
    }

    private static GuidePointPromptResult PromptNextGuidePointWithPreview(
        Editor ed,
        IReadOnlyList<Point3d> confirmedPoints,
        QuickWallSettingsDto settings)
    {
        using var jig = new QuickWallGuidePolylineJig(
            confirmedPoints,
            allowFinish: confirmedPoints.Count >= 2,
            GetLayerModeDisplay(settings));
        var dragResult = ed.Drag(jig);

        if (jig.RequestedSettings)
            return GuidePointPromptResult.ForSettings();

        if (jig.RequestedToggleMode)
            return GuidePointPromptResult.ForToggleMode();

        if (jig.RequestedSwapWidths)
            return GuidePointPromptResult.ForSwapWidths();

        if (jig.RequestedFinish)
            return GuidePointPromptResult.ForFinish();

        return dragResult.Status == PromptStatus.OK && jig.HasCurrentPoint
            ? GuidePointPromptResult.FromPoint(jig.CurrentPoint)
            : GuidePointPromptResult.ForCancel();
    }

    private static bool PromptPrimarySideWithPreview(
        Document doc,
        Polyline guidePolyline,
        ref QuickWallSettingsDto settings,
        out int primarySideSign)
    {
        primarySideSign = 1;
        var ed = doc.Editor;

        while (true)
        {
            var resolved = ResolveSettings(doc, settings);
            using var jig = new QuickWallPreviewJig(
                guidePolyline,
                resolved.PrimaryWidth,
                resolved.SecondaryWidth,
                resolved.WallColorIndex,
                GetLayerModeDisplay(settings));
            var dragResult = ed.Drag(jig);

            if (jig.RequestedSettings)
            {
                ShowSettingsDialog(doc, ref settings);
                continue;
            }

            if (jig.RequestedToggleMode)
            {
                ed.WriteMessage("\nC_TOOL：" + ToggleLayerMode(settings));
                continue;
            }

            if (jig.RequestedSwapWidths)
            {
                ed.WriteMessage("\nC_TOOL：" + SwapConfiguredWidths(settings));
                continue;
            }

            if (dragResult.Status != PromptStatus.OK)
                return false;

            if (!jig.TryGetResolvedSideSign(out primarySideSign, out var error))
            {
                ed.WriteMessage("\nC_TOOL：" + error);
                continue;
            }

            return true;
        }
    }

    private static void ShowSettingsDialog(Document doc, ref QuickWallSettingsDto settings)
    {
        var ed = doc.Editor;

        try
        {
            var currentLayerName = GetCurrentLayerName(doc);
            var layerEntries = CatalogLayerMerge.LoadValidLayerShortcutEntries();
            var wallLayerOptions = LoadWallLayerOptions(doc, layerEntries, currentLayerName);
            var window = new QuickWallSettingsWindow(
                settings,
                wallLayerOptions,
                currentLayerName);
            var accepted = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);

            if (accepted != true || window.SavedSettings == null)
                return;

            QuickWallSettingsStore.Save(window.SavedSettings);
            settings = window.SavedSettings;
            var resolvedSettings = ResolveSettings(doc, settings);
            ed.WriteMessage(
                $"\nC_TOOL：墙体设置已更新。线层 [{ResolveLayerName(settings.WallLayerName, currentLayerName)}]，填充层 [{resolvedSettings.HatchLayerName}]，宽度 {FormatWidthSummary(resolvedSettings)}，颜色 {FormatColorSummary(resolvedSettings)}。");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 墙体设置（路径参数）", ex);
            ed.WriteMessage($"\nC_TOOL：保存墙体设置失败：{ex.Message}");
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 墙体设置（路径过长）", ex);
            ed.WriteMessage($"\nC_TOOL：保存墙体设置失败：{ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 墙体设置（路径格式）", ex);
            ed.WriteMessage($"\nC_TOOL：保存墙体设置失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 墙体设置（权限）", ex);
            ed.WriteMessage($"\nC_TOOL：保存墙体设置失败：{ex.Message}");
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 墙体设置", ex);
            ed.WriteMessage($"\nC_TOOL：保存墙体设置失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_SQT 墙体设置失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：打开墙体设置失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_SQT 墙体设置失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：打开墙体设置失败：{ex.Message}");
        }
    }

    private static QuickWallResolvedSettings ResolveSettings(Document doc, QuickWallSettingsDto settings)
    {
        var currentLayerName = GetCurrentLayerName(doc);
        var layerEntries = CatalogLayerMerge.LoadValidLayerShortcutEntries();
        var wallLayerName = ResolveLayerName(settings.WallLayerName, currentLayerName);
        var quickWallHatchEntry = TryFindQuickWallHatchEntry(layerEntries);
        var wallLayerEntry = FindLayerShortcutEntry(layerEntries, wallLayerName);
        var hatchLayerName = (quickWallHatchEntry?.LayerName ?? "").Trim();
        if (hatchLayerName.Length == 0)
            hatchLayerName = wallLayerName;

        var hatchLayerEntry = quickWallHatchEntry;
        if (hatchLayerEntry == null)
        {
            hatchLayerEntry = string.Equals(hatchLayerName, wallLayerName, StringComparison.OrdinalIgnoreCase)
                ? wallLayerEntry
                : FindLayerShortcutEntry(layerEntries, hatchLayerName);
        }

        var effectiveWidths = ResolveEffectiveWidths(settings);

        return new QuickWallResolvedSettings(
            wallLayerName,
            hatchLayerName,
            effectiveWidths.PrimaryWidth,
            effectiveWidths.SecondaryWidth,
            settings.ColorIndex,
            wallLayerEntry,
            hatchLayerEntry,
            ResolveHatchStyle(quickWallHatchEntry));
    }

    private static void CreateWallEntities(
        Document doc,
        Polyline guidePolyline,
        int primarySideSign,
        QuickWallResolvedSettings settings)
    {
        if (!TryBuildWallOutlines(
                guidePolyline,
                primarySideSign,
                settings.PrimaryWidth,
                settings.SecondaryWidth,
                out var outlines,
                out var buildError) || outlines == null || outlines.Count == 0)
        {
            throw new InvalidOperationException(buildError);
        }

        using (doc.LockDocument())
        {
            var db = doc.Database;
            foreach (var outline in outlines)
            {
                using (outline)
                {
                    var outlineId = CreateWallOutlineEntity(db, outline, settings);
                    try
                    {
                        CreateWallHatchEntity(db, outlineId, settings, associative: true);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (IsNotInDatabaseError(ex))
                    {
                        C_toolsDiagnostics.LogNonFatal("F_SQT 关联填充创建失败，回退为非关联填充", ex);
                        CreateWallHatchEntity(db, outlineId, settings, associative: false);
                    }
                }
            }
        }
    }

    private static ObjectId CreateWallOutlineEntity(Database db, Polyline outline, QuickWallResolvedSettings settings)
    {
        return CadDatabaseScope.Write(
            db,
            (database, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(database, tr);

                EnsureLayer(tr, database, settings.WallLayerName, settings.WallLayerEntry);

                outline.SetDatabaseDefaults(database);
                outline.Layer = settings.WallLayerName;
                outline.Closed = true;
                ApplyColor(outline, settings.WallColorIndex);

                currentSpace.AppendEntity(outline);
                tr.AddNewlyCreatedDBObject(outline, true);
                return outline.ObjectId;
            });
    }

    private static void CreateWallHatchEntity(
        Database db,
        ObjectId outlineId,
        QuickWallResolvedSettings settings,
        bool associative)
    {
        CadDatabaseScope.Write(
            db,
            (database, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(database, tr);
                var outline = CadDatabaseScope.OpenAs<Polyline>(tr, outlineId, OpenMode.ForRead);

                EnsureLayer(tr, database, settings.HatchLayerName, settings.HatchLayerEntry);

                var hatch = new Hatch();
                hatch.SetDatabaseDefaults(database);
                hatch.Layer = settings.HatchLayerName;
                hatch.Normal = outline.Normal;
                hatch.Elevation = outline.Elevation;
                ApplyColor(hatch, settings.HatchColorIndex);

                currentSpace.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);

                ApplyHatchStyle(hatch, settings.HatchStyle);
                hatch.Associative = associative;

                var loopIds = new ObjectIdCollection();
                loopIds.Add(outlineId);
                hatch.AppendLoop(HatchLoopTypes.Outermost, loopIds);
                hatch.EvaluateHatch(true);
                MoveEntityToBottom(tr, currentSpace, hatch.ObjectId);
            });
    }

    private static void MoveEntityToBottom(Transaction tr, BlockTableRecord currentSpace, ObjectId entityId)
    {
        try
        {
            var drawOrderTable = CadDatabaseScope.OpenAs<DrawOrderTable>(tr, currentSpace.DrawOrderTableId, OpenMode.ForWrite);
            var entityIds = new ObjectIdCollection();
            entityIds.Add(entityId);
            drawOrderTable.MoveToBottom(entityIds);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_SQT 调整填充绘制顺序失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_SQT 调整填充绘制顺序失败（CAD）", ex);
        }
    }

    private static void EnsureLayer(Transaction tr, Database db, string layerName, LayerShortcutEntry? layerEntry = null)
    {
        var trimmedLayerName = layerName.Trim();
        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(trimmedLayerName))
            return;

        layerTable.UpgradeOpen();
        var layerRecord = new LayerTableRecord
        {
            Name = trimmedLayerName
        };

        if (layerEntry != null)
            LayerStyleHelper.ApplyToNewLayer(tr, db, layerRecord, layerEntry);

        layerTable.Add(layerRecord);
        tr.AddNewlyCreatedDBObject(layerRecord, true);
    }

    private static Polyline CreateGuidePolyline(IReadOnlyList<Point3d> points)
    {
        var polyline = new Polyline(points.Count);
        polyline.Normal = Vector3d.ZAxis;
        polyline.Elevation = points[0].Z;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            polyline.AddVertexAt(i, new Point2d(point.X, point.Y), 0, 0, 0);
        }

        return polyline;
    }

    private static Polyline CreateGuidePreviewPolyline(IReadOnlyList<Point3d> confirmedPoints, Point3d? previewPoint = null)
    {
        var vertexCount = confirmedPoints.Count + (previewPoint.HasValue ? 1 : 0);
        var polyline = new Polyline(vertexCount);
        polyline.Normal = Vector3d.ZAxis;

        if (confirmedPoints.Count > 0)
            polyline.Elevation = confirmedPoints[0].Z;
        else if (previewPoint.HasValue)
            polyline.Elevation = previewPoint.Value.Z;

        var vertexIndex = 0;
        for (var i = 0; i < confirmedPoints.Count; i++)
        {
            var point = confirmedPoints[i];
            polyline.AddVertexAt(vertexIndex++, new Point2d(point.X, point.Y), 0, 0, 0);
        }

        if (previewPoint.HasValue)
        {
            var point = previewPoint.Value;
            polyline.AddVertexAt(vertexIndex, new Point2d(point.X, point.Y), 0, 0, 0);
        }

        return polyline;
    }

    private static bool TryBuildWallOutlines(
        Polyline guidePolyline,
        int primarySideSign,
        double primaryWidth,
        double secondaryWidth,
        out List<Polyline>? outlines,
        out string error)
    {
        outlines = null;
        error = "";

        if (guidePolyline.NumberOfVertices < 2)
        {
            error = "墙体基线至少需要两个点。";
            return false;
        }

        if (double.IsNaN(primaryWidth) || double.IsInfinity(primaryWidth) || primaryWidth <= 0)
        {
            error = "宽度必须大于 0。";
            return false;
        }

        if (!TryGetOffsetPolyline(guidePolyline, -primarySideSign * primaryWidth, out var primaryBoundary, out error) ||
            primaryBoundary == null)
        {
            return false;
        }

        var builtOutlines = new List<Polyline>(secondaryWidth > PointTolerance ? 2 : 1);
        using (primaryBoundary)
        {
            using var guideBoundary = ClonePolyline(guidePolyline);
            try
            {
                var primaryOutline = StitchOutline(primaryBoundary, guideBoundary, guidePolyline.Elevation);
                if (primaryOutline.NumberOfVertices < 3)
                {
                    primaryOutline.Dispose();
                    error = "第一层墙体轮廓点数不足，无法生成。";
                    return false;
                }

                builtOutlines.Add(primaryOutline);

                if (secondaryWidth > PointTolerance)
                {
                    if (!TryGetOffsetPolyline(
                            primaryBoundary,
                            -primarySideSign * secondaryWidth,
                            out var secondaryBoundary,
                            out error) || secondaryBoundary == null)
                    {
                        DisposePolylines(builtOutlines);
                        return false;
                    }

                    using (secondaryBoundary)
                    {
                        var secondaryOutline = StitchOutline(secondaryBoundary, primaryBoundary, guidePolyline.Elevation);
                        if (secondaryOutline.NumberOfVertices < 3)
                        {
                            secondaryOutline.Dispose();
                            error = "第二层墙体轮廓点数不足，无法生成。";
                            DisposePolylines(builtOutlines);
                            return false;
                        }

                        builtOutlines.Add(secondaryOutline);
                    }
                }

                outlines = builtOutlines;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                DisposePolylines(builtOutlines);
                error = "墙体轮廓拼接失败：" + ex.Message;
                C_toolsDiagnostics.LogNonFatal("F_SQT 拼接墙体轮廓失败（无效操作）", ex);
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                DisposePolylines(builtOutlines);
                error = "墙体轮廓拼接失败：" + ex.Message;
                C_toolsDiagnostics.LogNonFatal("F_SQT 拼接墙体轮廓失败（CAD）", ex);
                return false;
            }
        }
    }

    private static void DisposePolylines(List<Polyline> polylines)
    {
        for (var i = 0; i < polylines.Count; i++)
            polylines[i].Dispose();

        polylines.Clear();
    }

    private static bool TryGetOffsetPolyline(
        Polyline guidePolyline,
        double signedDistance,
        out Polyline? offsetPolyline,
        out string error)
    {
        offsetPolyline = null;
        error = "";

        if (Math.Abs(signedDistance) <= PointTolerance)
        {
            offsetPolyline = ClonePolyline(guidePolyline);
            return true;
        }

        DBObjectCollection? offsetCurves = null;
        try
        {
            offsetCurves = guidePolyline.GetOffsetCurves(signedDistance);
            foreach (DBObject curve in offsetCurves)
            {
                if (curve is not Polyline polyline)
                    continue;

                offsetPolyline = ClonePolyline(polyline);
                return true;
            }

            error = "墙体偏移失败，未得到有效的多段线。";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = "墙体偏移失败：" + ex.Message;
            C_toolsDiagnostics.LogNonFatal("F_SQT 偏移墙体基线失败（无效操作）", ex);
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            error = "墙体偏移失败：" + ex.Message;
            C_toolsDiagnostics.LogNonFatal("F_SQT 偏移墙体基线失败（CAD）", ex);
            return false;
        }
        finally
        {
            if (offsetCurves != null)
            {
                foreach (DBObject curve in offsetCurves)
                    curve.Dispose();
            }
        }
    }

    private static Polyline StitchOutline(Polyline firstBoundary, Polyline secondBoundary, double elevation)
    {
        var vertices = new List<Point2d>();
        AppendBoundaryVertices(vertices, firstBoundary, reverse: false);
        AppendBoundaryVertices(vertices, secondBoundary, reverse: true);

        if (vertices.Count > 1 && ArePointsEqual(vertices[0], vertices[^1]))
            vertices.RemoveAt(vertices.Count - 1);

        var outline = new Polyline(vertices.Count);
        outline.Normal = Vector3d.ZAxis;
        outline.Elevation = elevation;

        for (var i = 0; i < vertices.Count; i++)
            outline.AddVertexAt(i, vertices[i], 0, 0, 0);

        outline.Closed = true;
        return outline;
    }

    private static void AppendBoundaryVertices(List<Point2d> target, Polyline polyline, bool reverse)
    {
        if (!reverse)
        {
            for (var i = 0; i < polyline.NumberOfVertices; i++)
                AddVertexIfDistinct(target, polyline.GetPoint2dAt(i));
            return;
        }

        for (var i = polyline.NumberOfVertices - 1; i >= 0; i--)
            AddVertexIfDistinct(target, polyline.GetPoint2dAt(i));
    }

    private static void AddVertexIfDistinct(List<Point2d> target, Point2d point)
    {
        if (target.Count > 0 && ArePointsEqual(target[^1], point))
            return;

        target.Add(point);
    }

    private static bool ArePointsEqual(Point2d a, Point2d b) =>
        a.GetDistanceTo(b) <= PointTolerance;

    private static Polyline ClonePolyline(Polyline source)
    {
        if (source.Clone() is Polyline clone)
            return clone;

        var fallback = new Polyline(source.NumberOfVertices);
        fallback.Normal = source.Normal;
        fallback.Elevation = source.Elevation;

        for (var i = 0; i < source.NumberOfVertices; i++)
            fallback.AddVertexAt(i, source.GetPoint2dAt(i), 0, 0, 0);

        fallback.Closed = source.Closed;
        return fallback;
    }

    private static int ResolvePrimarySideSign(Polyline guidePolyline, Point3d samplePoint, int fallbackSign)
    {
        var resolvedFallback = fallbackSign < 0 ? -1 : 1;
        if (!TryGetClosestSegment(guidePolyline, samplePoint, out var segmentStart, out var segmentEnd, out var closestPoint))
            return resolvedFallback;

        var tangent = segmentEnd - segmentStart;
        if (tangent.Length <= PointTolerance)
            return resolvedFallback;

        var normal = new Vector3d(-tangent.Y, tangent.X, 0);
        if (normal.Length <= PointTolerance)
            return resolvedFallback;

        normal = normal.GetNormal();
        var dot = (samplePoint - closestPoint).DotProduct(normal);
        if (Math.Abs(dot) <= PointTolerance)
            return resolvedFallback;

        return dot >= 0 ? 1 : -1;
    }

    private static bool TryGetClosestSegment(
        Polyline guidePolyline,
        Point3d samplePoint,
        out Point3d segmentStart,
        out Point3d segmentEnd,
        out Point3d closestPoint)
    {
        segmentStart = Point3d.Origin;
        segmentEnd = Point3d.Origin;
        closestPoint = Point3d.Origin;

        var bestDistanceSquared = double.MaxValue;
        var found = false;

        for (var i = 0; i < guidePolyline.NumberOfVertices - 1; i++)
        {
            var start = guidePolyline.GetPoint3dAt(i);
            var end = guidePolyline.GetPoint3dAt(i + 1);
            var delta = end - start;
            var lengthSquared = delta.LengthSqrd;
            if (lengthSquared <= PointTolerance * PointTolerance)
                continue;

            var raw = (samplePoint - start).DotProduct(delta) / lengthSquared;
            var parameter = raw < 0 ? 0 : raw > 1 ? 1 : raw;
            var projected = start + (delta * parameter);
            var dx = samplePoint.X - projected.X;
            var dy = samplePoint.Y - projected.Y;
            var dz = samplePoint.Z - projected.Z;
            var distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);

            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            segmentStart = start;
            segmentEnd = end;
            closestPoint = projected;
            found = true;
        }

        return found;
    }

    private static Point3d FlattenPoint(Point3d point, double elevation) =>
        new(point.X, point.Y, elevation);

    private static string GetCurrentLayerName(Document doc)
    {
        return CadSystemVariableService.GetTrimmedStringOrDefault(SystemVariableNames.Clayer, "0");
    }

    private static string ResolveLayerName(string configuredLayer, string currentLayerName)
    {
        var trimmed = (configuredLayer ?? "").Trim();
        return trimmed.Length == 0 ? currentLayerName : trimmed;
    }

    private static LayerShortcutEntry? FindLayerShortcutEntry(
        IReadOnlyList<LayerShortcutEntry> layerEntries,
        string layerName)
    {
        var trimmedLayerName = (layerName ?? "").Trim();
        if (trimmedLayerName.Length == 0)
            return null;

        return layerEntries.FirstOrDefault(x =>
            string.Equals((x.LayerName ?? "").Trim(), trimmedLayerName, StringComparison.OrdinalIgnoreCase));
    }

    private static LayerShortcutEntry? TryFindQuickWallHatchEntry(IReadOnlyList<LayerShortcutEntry> layerEntries)
    {
        LayerShortcutEntry? matchedEntry = null;
        for (var i = 0; i < layerEntries.Count; i++)
        {
            var entry = layerEntries[i];
            if (HatchStyleSnapshot.TryParseJson(entry.HatchStyle) == null)
                continue;

            var description = (entry.Description ?? "").Trim();
            if (!string.Equals(description, QuickWallHatchDescription, StringComparison.Ordinal))
                continue;

            if (matchedEntry != null)
                return null;

            matchedEntry = entry;
        }

        return matchedEntry;
    }

    private static HatchStyleSnapshot ResolveHatchStyle(LayerShortcutEntry? hatchLayerEntry)
    {
        var style = HatchStyleSnapshot.TryParseJson(hatchLayerEntry?.HatchStyle);
        if (style != null)
            return style;

        return new HatchStyleSnapshot
        {
            PatternName = SolidPatternName,
            Scale = 1.0,
            AngleDegrees = 0.0
        };
    }

    private static void ApplyColor(Entity entity, int? colorIndex)
    {
        if (colorIndex is >= 1 and <= 255)
        {
            entity.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex.Value);
            return;
        }

        entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
    }

    private static void ApplyHatchStyle(Hatch hatch, HatchStyleSnapshot hatchStyle)
    {
        var patternName = (hatchStyle.PatternName ?? "").Trim();
        if (patternName.Length == 0)
            patternName = SolidPatternName;

        Autodesk.AutoCAD.Runtime.Exception? lastCadException = null;
        foreach (var patternType in new[] { HatchPatternType.PreDefined, HatchPatternType.CustomDefined })
        {
            try
            {
                hatch.SetHatchPattern(patternType, patternName);
                var scale = hatchStyle.Scale;
                if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
                    scale = 1.0;

                var angleDegrees = hatchStyle.AngleDegrees;
                if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees))
                    angleDegrees = 0.0;

                hatch.PatternScale = scale;
                hatch.PatternAngle = angleDegrees * (Math.PI / 180.0);
                return;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                lastCadException = ex;
            }
        }

        if (lastCadException != null)
            C_toolsDiagnostics.LogNonFatal("F_SQT 应用填充样式失败，回退为 SOLID", lastCadException);

        hatch.SetHatchPattern(HatchPatternType.PreDefined, SolidPatternName);
        hatch.PatternScale = 1.0;
        hatch.PatternAngle = 0.0;
    }

    private static IReadOnlyList<string> LoadWallLayerOptions(
        Document doc,
        IReadOnlyList<LayerShortcutEntry> layerEntries,
        string currentLayerName)
    {
        var shortcutLayers = layerEntries
            .Select(x => (x.LayerName ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return shortcutLayers.Count > 0
            ? shortcutLayers
            : LoadDatabaseLayerNames(doc.Database, currentLayerName);
    }

    private static IReadOnlyList<string> LoadDatabaseLayerNames(Database db, string fallbackLayerName)
    {
        var names = new List<string>();

        try
        {
            names.AddRange(
                CadDatabaseScope.Read(
                    db,
                    (database, tr) =>
                    {
                        var layerNames = new List<string>();
                        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, database.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId layerId in layerTable)
                        {
                            if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead, out var layer) ||
                                layer == null)
                            {
                                continue;
                            }

                            var name = (layer.Name ?? "").Trim();
                            if (name.Length > 0)
                                layerNames.Add(name);
                        }

                        return layerNames;
                    }));
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_SQT 读取图层下拉数据失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_SQT 读取图层下拉数据失败（CAD）", ex);
        }

        var fallback = (fallbackLayerName ?? "").Trim();
        if (fallback.Length > 0 && !names.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            names.Add(fallback);

        if (names.Count == 0)
            names.Add("0");

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsNotInDatabaseError(Autodesk.AutoCAD.Runtime.Exception ex) =>
        ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.NotInDatabase ||
        ex.Message.IndexOf("eNotInDatabase", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string FormatWidthSummary(QuickWallResolvedSettings settings)
    {
        if (settings.SecondaryWidth <= PointTolerance)
            return $"单层 {FormatWidth(settings.PrimaryWidth)}";

        return $"{FormatWidth(settings.PrimaryWidth)} + {FormatWidth(settings.SecondaryWidth)}（第二层沿第一层外侧继续偏移）";
    }

    private static string FormatWidth(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatColorSummary(QuickWallResolvedSettings settings) =>
        settings.ObjectColorIndex.HasValue
            ? $"固定 ACI {settings.ObjectColorIndex.Value}"
            : "按 C_TOOL 图层颜色";

    internal static (double PrimaryWidth, double SecondaryWidth) ResolveEffectiveWidths(QuickWallSettingsDto settings)
    {
        var activeSecondaryWidth = GetActiveSecondaryWidth(settings);
        if (activeSecondaryWidth > PointTolerance)
            return (settings.PrimaryWidth, activeSecondaryWidth);

        return (Math.Max(settings.PrimaryWidth, GetConfiguredSecondaryWidth(settings)), 0.0);
    }

    private static double GetActiveSecondaryWidth(QuickWallSettingsDto settings) =>
        settings.UseSecondaryWidth && HasConfiguredSecondaryWidth(settings)
            ? settings.SecondaryWidth!.Value
            : 0.0;

    private static double GetConfiguredSecondaryWidth(QuickWallSettingsDto settings) =>
        HasConfiguredSecondaryWidth(settings)
            ? settings.SecondaryWidth!.Value
            : 0.0;

    private static bool HasConfiguredSecondaryWidth(QuickWallSettingsDto settings) =>
        settings.SecondaryWidth.HasValue && settings.SecondaryWidth.Value > PointTolerance;

    private static string GetLayerModeDisplay(QuickWallSettingsDto settings) =>
        GetActiveSecondaryWidth(settings) > PointTolerance ? "双层" : "单层";

    private static string GetFirstGuidePointPromptMessage(QuickWallSettingsDto settings) =>
        "\nC_TOOL 绘制墙体";

    private static string GetNextGuidePointPromptMessage(bool allowFinish, string layerModeDisplay) =>
        "\n下一点：";

    private static string GetPrimarySidePromptMessage(string layerModeDisplay) =>
        "\nC_TOOL：移动光标指定宽度方向，单击生成墙体";

    private static void AddGuideKeywords(KeywordCollection keywords, string layerModeDisplay)
    {
        keywords.Add(
            SettingsKeyword,
            SettingsKeyword,
            FormatKeywordDisplay(SettingsKeywordDisplay, SettingsKeyword));
        keywords.Add(
            ToggleLayerModeKeyword,
            ToggleLayerModeKeyword,
            FormatKeywordDisplay(layerModeDisplay, ToggleLayerModeKeyword));
        keywords.Add(
            SwapWidthsKeyword,
            SwapWidthsKeyword,
            FormatKeywordDisplay(SwapWidthsKeywordDisplay, SwapWidthsKeyword));
    }

    private static string FormatKeywordDisplay(string label, string shortcut) =>
        $"{label}({shortcut})";

    private static bool IsSettingsKeyword(string keyword) =>
        string.Equals(keyword, SettingsKeyword, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(keyword, SettingsKeywordDisplay, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            keyword,
            FormatKeywordDisplay(SettingsKeywordDisplay, SettingsKeyword),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsToggleLayerModeKeyword(string keyword, string layerModeDisplay) =>
        string.Equals(keyword, ToggleLayerModeKeyword, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(keyword, layerModeDisplay, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            keyword,
            FormatKeywordDisplay(layerModeDisplay, ToggleLayerModeKeyword),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSwapWidthsKeyword(string keyword) =>
        string.Equals(keyword, SwapWidthsKeyword, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(keyword, SwapWidthsKeywordDisplay, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            keyword,
            FormatKeywordDisplay(SwapWidthsKeywordDisplay, SwapWidthsKeyword),
            StringComparison.OrdinalIgnoreCase);

    private static string ToggleLayerMode(QuickWallSettingsDto settings)
    {
        if (!HasConfiguredSecondaryWidth(settings))
        {
            settings.UseSecondaryWidth = false;
            return "未设置第二宽度，按单层墙体处理。";
        }

        settings.UseSecondaryWidth = !settings.UseSecondaryWidth;
        TrySaveSettingsSilently(settings);
        var effectiveWidths = ResolveEffectiveWidths(settings);

        return settings.UseSecondaryWidth
            ? $"已切换双层墙体：{FormatWidth(settings.PrimaryWidth)} + {FormatWidth(settings.SecondaryWidth!.Value)}。"
            : $"已切换单层墙体：{FormatWidth(effectiveWidths.PrimaryWidth)}。";
    }

    internal static string SwapConfiguredWidths(QuickWallSettingsDto settings)
    {
        if (!HasConfiguredSecondaryWidth(settings))
            return "未设置第二宽度，无法互换宽度。";

        var originalPrimaryWidth = settings.PrimaryWidth;
        settings.PrimaryWidth = settings.SecondaryWidth!.Value;
        settings.SecondaryWidth = originalPrimaryWidth;
        TrySaveSettingsSilently(settings);

        var effectiveWidths = ResolveEffectiveWidths(settings);
        return settings.UseSecondaryWidth
            ? $"已互换宽度：{FormatWidth(settings.PrimaryWidth)} + {FormatWidth(settings.SecondaryWidth!.Value)}。"
            : $"已互换宽度，单层宽度 {FormatWidth(effectiveWidths.PrimaryWidth)}。";
    }

    private static void TrySaveSettingsSilently(QuickWallSettingsDto settings)
    {
        try
        {
            QuickWallSettingsStore.Save(settings);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 单双层切换状态失败（路径参数）", ex);
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 单双层切换状态失败（路径过长）", ex);
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 单双层切换状态失败（路径格式）", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 单双层切换状态失败（权限）", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_SQT 单双层切换状态失败", ex);
        }
    }

    private static string NormalizeKeyword(string? keyword) =>
        (keyword ?? "").Trim();

    private enum PathPromptStatus
    {
        Success,
        EndCommand,
        Cancel
    }

    private enum GuidePointPromptAction
    {
        Point,
        Settings,
        ToggleMode,
        SwapWidths,
        Finish,
        Cancel
    }

    private readonly struct GuidePointPromptResult
    {
        private GuidePointPromptResult(GuidePointPromptAction action, Point3d point)
        {
            Action = action;
            Point = point;
        }

        internal GuidePointPromptAction Action { get; }

        internal Point3d Point { get; }

        internal static GuidePointPromptResult FromPoint(Point3d point) =>
            new(GuidePointPromptAction.Point, point);

        internal static GuidePointPromptResult ForSettings() =>
            new(GuidePointPromptAction.Settings, Point3d.Origin);

        internal static GuidePointPromptResult ForToggleMode() =>
            new(GuidePointPromptAction.ToggleMode, Point3d.Origin);

        internal static GuidePointPromptResult ForSwapWidths() =>
            new(GuidePointPromptAction.SwapWidths, Point3d.Origin);

        internal static GuidePointPromptResult ForFinish() =>
            new(GuidePointPromptAction.Finish, Point3d.Origin);

        internal static GuidePointPromptResult ForCancel() =>
            new(GuidePointPromptAction.Cancel, Point3d.Origin);
    }

    private readonly struct QuickWallResolvedSettings
    {
        internal QuickWallResolvedSettings(
            string wallLayerName,
            string hatchLayerName,
            double primaryWidth,
            double secondaryWidth,
            int? objectColorIndex,
            LayerShortcutEntry? wallLayerEntry,
            LayerShortcutEntry? hatchLayerEntry,
            HatchStyleSnapshot hatchStyle)
        {
            WallLayerName = wallLayerName;
            HatchLayerName = hatchLayerName;
            PrimaryWidth = primaryWidth;
            SecondaryWidth = secondaryWidth;
            ObjectColorIndex = objectColorIndex is >= 1 and <= 255 ? objectColorIndex : null;
            WallLayerEntry = wallLayerEntry;
            HatchLayerEntry = hatchLayerEntry;
            WallColorIndex = ObjectColorIndex ?? wallLayerEntry?.ColorIndex;
            HatchColorIndex = hatchLayerEntry?.ColorIndex;
            HatchStyle = hatchStyle;
        }

        internal string WallLayerName { get; }

        internal string HatchLayerName { get; }

        internal double PrimaryWidth { get; }

        internal double SecondaryWidth { get; }

        internal int? ObjectColorIndex { get; }

        internal LayerShortcutEntry? WallLayerEntry { get; }

        internal LayerShortcutEntry? HatchLayerEntry { get; }

        internal int? WallColorIndex { get; }

        internal int? HatchColorIndex { get; }

        internal HatchStyleSnapshot HatchStyle { get; }
    }

    private sealed class QuickWallGuidePolylineJig : DrawJig, IDisposable
    {
        private readonly Point3d[] _confirmedPoints;
        private readonly bool _allowFinish;
        private readonly string _layerModeDisplay;
        private Point3d _currentPoint;

        internal QuickWallGuidePolylineJig(
            IReadOnlyList<Point3d> confirmedPoints,
            bool allowFinish,
            string layerModeDisplay)
        {
            _confirmedPoints = confirmedPoints.ToArray();
            _allowFinish = allowFinish;
            _layerModeDisplay = layerModeDisplay;
        }

        internal bool RequestedSettings { get; private set; }

        internal bool RequestedToggleMode { get; private set; }

        internal bool RequestedSwapWidths { get; private set; }

        internal bool RequestedFinish { get; private set; }

        internal bool HasCurrentPoint { get; private set; }

        internal Point3d CurrentPoint => _currentPoint;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(GetNextGuidePointPromptMessage(_allowFinish, _layerModeDisplay))
            {
                BasePoint = _confirmedPoints[^1],
                UseBasePoint = true,
                Cursor = CursorType.Crosshair,
                AppendKeywordsToMessage = true,
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.GovernedByOrthoMode |
                                    UserInputControls.NullResponseAccepted
            };
            AddGuideKeywords(options.Keywords, _layerModeDisplay);

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Keyword)
            {
                var keyword = NormalizeKeyword(result.StringResult);
                if (IsSettingsKeyword(keyword))
                    RequestedSettings = true;
                else if (IsToggleLayerModeKeyword(keyword, _layerModeDisplay))
                    RequestedToggleMode = true;
                else if (IsSwapWidthsKeyword(keyword))
                    RequestedSwapWidths = true;

                return SamplerStatus.NoChange;
            }

            if (result.Status == PromptStatus.None && _allowFinish)
            {
                RequestedFinish = true;
                return SamplerStatus.NoChange;
            }

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            var flattenedPoint = FlattenPoint(result.Value, _confirmedPoints[0].Z);
            if (HasCurrentPoint && flattenedPoint.DistanceTo(_currentPoint) <= PointTolerance)
                return SamplerStatus.NoChange;

            _currentPoint = flattenedPoint;
            HasCurrentPoint = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(Gi.WorldDraw draw)
        {
            try
            {
                using var previewPolyline = CreateGuidePreviewPolyline(
                    _confirmedPoints,
                    HasCurrentPoint ? _currentPoint : null);
                previewPolyline.ColorIndex = GuidePreviewColorIndex;
                draw.Geometry.Draw(previewPolyline);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_SQT 基线预览绘制失败（无效操作）", ex);
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_SQT 基线预览绘制失败（CAD）", ex);
                return false;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class QuickWallPreviewJig : DrawJig, IDisposable
    {
        private readonly Polyline _guidePolyline;
        private readonly double _primaryWidth;
        private readonly double _secondaryWidth;
        private readonly int? _previewColorIndex;
        private readonly string _layerModeDisplay;
        private Point3d _cursorPoint;
        private bool _hasCursorPoint;
        private int _primarySideSign = 1;

        internal QuickWallPreviewJig(
            Polyline guidePolyline,
            double primaryWidth,
            double secondaryWidth,
            int? previewColorIndex,
            string layerModeDisplay)
        {
            _guidePolyline = ClonePolyline(guidePolyline);
            _primaryWidth = primaryWidth;
            _secondaryWidth = secondaryWidth;
            _previewColorIndex = previewColorIndex;
            _layerModeDisplay = layerModeDisplay;
        }

        internal bool RequestedSettings { get; private set; }

        internal bool RequestedToggleMode { get; private set; }

        internal bool RequestedSwapWidths { get; private set; }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(GetPrimarySidePromptMessage(_layerModeDisplay))
            {
                Cursor = CursorType.Crosshair,
                AppendKeywordsToMessage = true,
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.GovernedByOrthoMode
            };
            AddGuideKeywords(options.Keywords, _layerModeDisplay);

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Keyword)
            {
                var keyword = NormalizeKeyword(result.StringResult);
                if (IsSettingsKeyword(keyword))
                    RequestedSettings = true;
                else if (IsToggleLayerModeKeyword(keyword, _layerModeDisplay))
                    RequestedToggleMode = true;
                else if (IsSwapWidthsKeyword(keyword))
                    RequestedSwapWidths = true;

                return SamplerStatus.NoChange;
            }

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            var flattenedPoint = FlattenPoint(result.Value, _guidePolyline.Elevation);
            if (_hasCursorPoint && flattenedPoint.DistanceTo(_cursorPoint) <= PointTolerance)
                return SamplerStatus.NoChange;

            _cursorPoint = flattenedPoint;
            _hasCursorPoint = true;
            return SamplerStatus.OK;
        }

        internal bool TryGetResolvedSideSign(out int sideSign, out string error)
        {
            sideSign = _primarySideSign;
            error = "";

            if (!_hasCursorPoint)
            {
                error = "请移动鼠标选择宽度所在侧。";
                return false;
            }

            sideSign = ResolvePrimarySideSign(_guidePolyline, _cursorPoint, _primarySideSign);
            if (!TryBuildWallOutlines(
                    _guidePolyline,
                    sideSign,
                    _primaryWidth,
                    _secondaryWidth,
                    out var previewOutlines,
                    out error) || previewOutlines == null || previewOutlines.Count == 0)
            {
                return false;
            }

            DisposePolylines(previewOutlines);
            _primarySideSign = sideSign;
            return true;
        }

        protected override bool WorldDraw(Gi.WorldDraw draw)
        {
            try
            {
                if (!_hasCursorPoint)
                    return true;

                var nextSideSign = ResolvePrimarySideSign(_guidePolyline, _cursorPoint, _primarySideSign);
                if (!TryBuildWallOutlines(
                        _guidePolyline,
                        nextSideSign,
                        _primaryWidth,
                        _secondaryWidth,
                        out var previewOutlines,
                        out _) || previewOutlines == null || previewOutlines.Count == 0)
                {
                    return true;
                }

                try
                {
                    for (var i = 0; i < previewOutlines.Count; i++)
                    {
                        var previewOutline = previewOutlines[i];
                        previewOutline.ColorIndex = _previewColorIndex is >= 1 and <= 255
                            ? (short)_previewColorIndex.Value
                            : (short)3;
                        draw.Geometry.Draw(previewOutline);
                    }
                }
                finally
                {
                    DisposePolylines(previewOutlines);
                }

                _primarySideSign = nextSideSign;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_SQT 预览绘制失败（无效操作）", ex);
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_SQT 预览绘制失败（CAD）", ex);
                return false;
            }
        }

        public void Dispose()
        {
            _guidePolyline.Dispose();
        }
    }
}
