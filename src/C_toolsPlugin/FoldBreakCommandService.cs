using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static class FoldBreakCommandService
{
    private const string CommandName = PluginCommandIds.FoldBreakSymbol;
    private const string SettingsKeyword = "S";
    private const double GeometryTolerance = 1e-6;
    private const double AxisAlignmentTolerance = 1e-4;
    private const double AxisDirectionTolerance = 1e-4;

    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var settings = FoldBreakSettingsStore.LoadOrDefault();

        try
        {
            var promptStatus = PromptRectangleRanges(doc, ref settings, out var rectangles, out var invalidCount);
            if (promptStatus == SelectionPromptStatus.EndCommand)
                return;

            if (promptStatus == SelectionPromptStatus.Cancel)
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 已取消。");
                return;
            }

            if (rectangles.Count == 0)
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 未识别到有效的矩形范围。");
                return;
            }

            var createdCount = CreateSymbols(doc, rectangles, settings);
            var ignoredText = invalidCount > 0 ? $"，已忽略 {invalidCount} 个非矩形对象" : "";
            ed.WriteMessage(
                $"\nC_TOOL：{CommandName} 已生成 {createdCount} 个折空符号，颜色 ACI {settings.ColorIndex}{ignoredText}。");
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

    private static SelectionPromptStatus PromptRectangleRanges(
        Document doc,
        ref FoldBreakSettingsDto settings,
        out List<RectangleRange> rectangles,
        out int invalidCount)
    {
        rectangles = new List<RectangleRange>();
        invalidCount = 0;
        var ed = doc.Editor;

        while (true)
        {
            var options = new PromptPointOptions(
                $"\nC_TOOL：点选矩形范围内部，或按 {SettingsKeyword} 设置：")
            {
                AppendKeywordsToMessage = true,
                AllowNone = true
            };
            options.Keywords.Add(SettingsKeyword);

            var result = ed.GetPoint(options);
            if (result.Status == PromptStatus.OK)
            {
                if (TryResolveRectangleRangeByPoint(doc, result.Value, ed.CurrentUserCoordinateSystem, out var range))
                {
                    rectangles.Add(range);
                    return SelectionPromptStatus.Success;
                }

                ed.WriteMessage($"\nC_TOOL：{CommandName} 未识别到矩形范围，请在矩形内部重新点选。");
                continue;
            }

            if (result.Status == PromptStatus.Keyword)
            {
                var keyword = NormalizeKeyword(result.StringResult);
                if (IsSettingsKeyword(keyword))
                {
                    ShowSettingsDialog(doc, ref settings);
                    continue;
                }

                continue;
            }

            return result.Status is PromptStatus.Cancel
                ? SelectionPromptStatus.Cancel
                : SelectionPromptStatus.EndCommand;
        }
    }

    private static bool TryResolveRectangleRangeByPoint(
        Document doc,
        Point3d pickPoint,
        Matrix3d currentUserCoordinateSystem,
        out RectangleRange range)
    {
        range = default;
        var analysisTransforms = BuildAnalysisTransforms(currentUserCoordinateSystem);
        var resolvedRange = default(RectangleRange);
        var resolved = CadDatabaseScope.Read(
            doc,
            (db, tr) =>
                TryFindBestContainingRectangleRangeAtPointInCurrentSpace(tr, db, pickPoint, out resolvedRange) ||
                TryInferRectangleRangeFromLineNetworkAtPointInCurrentSpace(
                    tr,
                    db,
                    pickPoint,
                    analysisTransforms,
                    out resolvedRange));

        if (!resolved)
            return false;

        range = resolvedRange;
        return true;
    }

    private static bool TryFindBestContainingRectangleRangeAtPointInCurrentSpace(
        Transaction tr,
        Database db,
        Point3d pickPoint,
        out RectangleRange range)
    {
        range = default;
        var found = false;
        var bestArea = double.MaxValue;

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, db.CurrentSpaceId, OpenMode.ForRead, out var currentSpace) ||
            currentSpace == null)
        {
            return false;
        }

        foreach (ObjectId entityId in currentSpace)
        {
            if (!TryGetRectangleRange(tr, entityId, out var candidate) ||
                !candidate.ContainsWorldPoint(pickPoint, AxisAlignmentTolerance))
            {
                continue;
            }

            if (found && candidate.Area >= bestArea - GeometryTolerance)
                continue;

            range = candidate;
            bestArea = candidate.Area;
            found = true;
        }

        return found;
    }

    private static bool TryInferRectangleRangeFromLineNetworkAtPointInCurrentSpace(
        Transaction tr,
        Database db,
        Point3d pickPoint,
        IReadOnlyList<AnalysisTransform> analysisTransforms,
        out RectangleRange range)
    {
        range = default;
        var found = false;
        var bestArea = double.MaxValue;
        for (var index = 0; index < analysisTransforms.Count; index++)
        {
            var transform = analysisTransforms[index];
            if (!TryCollectAxisAlignedSegmentsInCurrentSpace(tr, db, transform.WorldToAnalysis, out var segments) ||
                segments.Count == 0)
            {
                continue;
            }

            var analysisPoint = pickPoint.TransformBy(transform.WorldToAnalysis);
            if (!RectangleCenterAlignCommandService.TryInferAxisAlignedRectangleBounds(
                    analysisPoint,
                    segments,
                    out var analysisExtents))
            {
                continue;
            }

            var candidate = RectangleRange.FromAnalysisExtents(
                analysisExtents,
                transform.AnalysisToWorld,
                transform.WorldToAnalysis);
            if (found && candidate.Area >= bestArea - GeometryTolerance)
                continue;

            range = candidate;
            bestArea = candidate.Area;
            found = true;
        }

        return found;
    }

    private static bool TryCollectAxisAlignedSegmentsInCurrentSpace(
        Transaction tr,
        Database db,
        Matrix3d worldToAnalysis,
        out List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments)
    {
        segments = new List<RectangleCenterAlignCommandService.AxisAlignedSegment>();

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, db.CurrentSpaceId, OpenMode.ForRead, out var currentSpace) ||
            currentSpace == null)
        {
            return false;
        }

        foreach (ObjectId entityId in currentSpace)
            AppendAxisAlignedSegments(tr, entityId, worldToAnalysis, segments);

        return true;
    }

    private static void AppendAxisAlignedSegments(
        Transaction tr,
        ObjectId entityId,
        Matrix3d worldToAnalysis,
        List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments)
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
                AppendDirectionalAxisAlignedSegment(
                    xline.BasePoint,
                    xline.UnitDir,
                    worldToAnalysis,
                    isRay: false,
                    segments);
                break;
            case Ray ray:
                AppendDirectionalAxisAlignedSegment(
                    ray.BasePoint,
                    ray.UnitDir,
                    worldToAnalysis,
                    isRay: true,
                    segments);
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

    private static void AppendPolylineAxisAlignedSegments(
        Polyline polyline,
        Matrix3d worldToAnalysis,
        List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments)
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
        List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments)
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
        List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments)
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

    private static void AppendDirectionalAxisAlignedSegment(
        Point3d basePoint,
        Vector3d direction,
        Matrix3d worldToAnalysis,
        bool isRay,
        List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments)
    {
        if (RectangleCenterAlignCommandService.TryCreateAxisAlignedDirectionalSegment(
                basePoint.TransformBy(worldToAnalysis),
                direction.TransformBy(worldToAnalysis),
                isRay,
                out var segment))
        {
            segments.Add(segment);
        }
    }

    private static void TryAppendAxisAlignedSegment(
        List<RectangleCenterAlignCommandService.AxisAlignedSegment> segments,
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
            segments.Add(RectangleCenterAlignCommandService.AxisAlignedSegment.CreateVertical(
                (startPoint.X + endPoint.X) * 0.5,
                startPoint.Y,
                endPoint.Y));
            return;
        }

        if (Math.Abs(deltaY / length) <= AxisDirectionTolerance && Math.Abs(deltaX) > GeometryTolerance)
        {
            segments.Add(RectangleCenterAlignCommandService.AxisAlignedSegment.CreateHorizontal(
                (startPoint.Y + endPoint.Y) * 0.5,
                startPoint.X,
                endPoint.X));
        }
    }

    private static IReadOnlyList<AnalysisTransform> BuildAnalysisTransforms(Matrix3d currentUserCoordinateSystem)
    {
        return new[]
        {
            new AnalysisTransform(Matrix3d.Identity, Matrix3d.Identity),
            new AnalysisTransform(currentUserCoordinateSystem.Inverse(), currentUserCoordinateSystem)
        };
    }

    private static void ShowSettingsDialog(Document doc, ref FoldBreakSettingsDto settings)
    {
        var ed = doc.Editor;

        try
        {
            var window = new FoldBreakSettingsWindow(settings);
            var accepted = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);

            if (accepted != true || window.SavedSettings == null)
                return;

            FoldBreakSettingsStore.Save(window.SavedSettings);
            settings = window.SavedSettings;
            ed.WriteMessage($"\nC_TOOL：F_DK 设置已更新，比例 {FormatRatio(settings)}，颜色 ACI {settings.ColorIndex}。");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DK 折空符号设置（参数错误）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DK 设置失败：{ex.Message}");
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DK 折空符号设置（路径过长）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DK 设置失败：{ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DK 折空符号设置（路径格式）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DK 设置失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DK 折空符号设置（权限）", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DK 设置失败：{ex.Message}");
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_DK 折空符号设置", ex);
            ed.WriteMessage($"\nC_TOOL：保存 F_DK 设置失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DK 折空符号设置失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：打开 F_DK 设置失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DK 折空符号设置失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：打开 F_DK 设置失败：{ex.Message}");
        }
    }

    private static int CreateSymbols(
        Document doc,
        IReadOnlyList<RectangleRange> rectangles,
        FoldBreakSettingsDto settings)
    {
        var createdCount = 0;
        CadDatabaseScope.Write(
            doc,
            (db, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                for (var i = 0; i < rectangles.Count; i++)
                {
                    var symbol = CreateSymbolEntity(db, rectangles[i], settings);
                    currentSpace.AppendEntity(symbol);
                    tr.AddNewlyCreatedDBObject(symbol, true);
                    createdCount++;
                }
            },
            requireDocumentLock: true);

        return createdCount;
    }

    private static Polyline CreateSymbolEntity(Database db, RectangleRange rectangle, FoldBreakSettingsDto settings)
    {
        var points = rectangle.BuildFoldPoints(settings);

        var polyline = new Polyline(points.Length);
        polyline.SetDatabaseDefaults(db);
        polyline.Normal = Vector3d.ZAxis;
        polyline.Elevation = points.Average(point => point.Z);
        polyline.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)settings.ColorIndex);
        for (var i = 0; i < points.Length; i++)
            polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0.0, 0.0, 0.0);

        return polyline;
    }

    internal static Point3d[] BuildFoldPoints(
        double left,
        double bottom,
        double right,
        double top,
        double elevation,
        FoldBreakSettingsDto settings)
    {
        settings = FoldBreakSettingsStore.Normalize(settings);
        var width = Math.Max(0.0, right - left);
        var height = Math.Max(0.0, top - bottom);
        var horizontalTotal = settings.HorizontalLeftPart + settings.HorizontalRightPart;
        var verticalTotal = settings.VerticalTopPart + settings.VerticalBottomPart;
        var turnX = left + (width * settings.HorizontalLeftPart / horizontalTotal);
        var turnY = top - (height * settings.VerticalTopPart / verticalTotal);

        return
        [
            new Point3d(left, bottom, elevation),
            new Point3d(turnX, turnY, elevation),
            new Point3d(right, top, elevation)
        ];
    }

    private static bool TryGetRectangleRange(Transaction tr, ObjectId objectId, out RectangleRange range)
    {
        range = default;
        if (objectId.IsNull ||
            !CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
            entity == null)
        {
            return false;
        }

        return entity switch
        {
            Polyline polyline => TryGetPolylineRectangleRange(polyline, out range),
            Polyline2d polyline2d => TryGetPolyline2dRectangleRange(tr, polyline2d, out range),
            Polyline3d polyline3d => TryGetPolyline3dRectangleRange(tr, polyline3d, out range),
            _ => false
        };
    }

    private static bool TryGetPolylineRectangleRange(Polyline polyline, out RectangleRange range)
    {
        range = default;
        if (!polyline.Closed || polyline.NumberOfVertices < 4 || polyline.NumberOfVertices > 5)
            return false;

        var points = new List<Point3d>(polyline.NumberOfVertices);
        for (var i = 0; i < polyline.NumberOfVertices; i++)
        {
            if (Math.Abs(polyline.GetBulgeAt(i)) > GeometryTolerance)
                return false;

            AddPointIfDistinct(points, polyline.GetPoint3dAt(i));
        }

        RemoveDuplicateClosingPoint(points);
        return TryCreateRectangleRange(points, out range);
    }

    private static bool TryGetPolyline2dRectangleRange(Transaction tr, Polyline2d polyline2d, out RectangleRange range)
    {
        range = default;
        if (!polyline2d.Closed)
            return false;

        var points = new List<Point3d>();
        foreach (ObjectId vertexId in polyline2d)
        {
            if (!CadDatabaseScope.TryOpenAs<Vertex2d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null ||
                Math.Abs(vertex.Bulge) > GeometryTolerance)
            {
                return false;
            }

            AddPointIfDistinct(points, vertex.Position);
        }

        RemoveDuplicateClosingPoint(points);
        return TryCreateRectangleRange(points, out range);
    }

    private static bool TryGetPolyline3dRectangleRange(Transaction tr, Polyline3d polyline3d, out RectangleRange range)
    {
        range = default;
        if (!polyline3d.Closed)
            return false;

        var points = new List<Point3d>();
        foreach (ObjectId vertexId in polyline3d)
        {
            if (!CadDatabaseScope.TryOpenAs<PolylineVertex3d>(tr, vertexId, OpenMode.ForRead, out var vertex) ||
                vertex == null)
            {
                return false;
            }

            AddPointIfDistinct(points, vertex.Position);
        }

        RemoveDuplicateClosingPoint(points);
        return TryCreateRectangleRange(points, out range);
    }

    private static bool TryCreateRectangleRange(IReadOnlyList<Point3d> points, out RectangleRange range)
    {
        range = default;
        if (points.Count != 4)
            return false;

        if (!TryGetTwoDistinctCoordinates(points, useX: true, out var xs) ||
            !TryGetTwoDistinctCoordinates(points, useX: false, out var ys))
        {
            return false;
        }

        var left = xs[0];
        var right = xs[1];
        var bottom = ys[0];
        var top = ys[1];
        if (right <= left + GeometryTolerance || top <= bottom + GeometryTolerance)
            return false;

        if (!HasCorner(points, left, bottom) ||
            !HasCorner(points, left, top) ||
            !HasCorner(points, right, bottom) ||
            !HasCorner(points, right, top))
        {
            return false;
        }

        var elevation = points.Average(point => point.Z);
        range = new RectangleRange(left, bottom, right, top, elevation);
        return true;
    }

    private static bool TryGetTwoDistinctCoordinates(
        IReadOnlyList<Point3d> points,
        bool useX,
        out double[] coordinates)
    {
        var values = new List<double>(2);
        for (var i = 0; i < points.Count; i++)
        {
            var value = useX ? points[i].X : points[i].Y;
            var found = false;
            for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
            {
                if (Math.Abs(values[valueIndex] - value) > GeometryTolerance)
                    continue;

                values[valueIndex] = (values[valueIndex] + value) * 0.5;
                found = true;
                break;
            }

            if (!found)
                values.Add(value);
        }

        values.Sort();
        coordinates = values.ToArray();
        return coordinates.Length == 2;
    }

    private static bool HasCorner(IReadOnlyList<Point3d> points, double x, double y)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (Math.Abs(points[i].X - x) <= GeometryTolerance &&
                Math.Abs(points[i].Y - y) <= GeometryTolerance)
            {
                return true;
            }
        }

        return false;
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

    private static string NormalizeKeyword(string? keyword) => (keyword ?? string.Empty).Trim();

    private static bool IsSettingsKeyword(string? text) =>
        string.Equals(NormalizeKeyword(text), SettingsKeyword, StringComparison.OrdinalIgnoreCase);

    private static string FormatRatio(FoldBreakSettingsDto settings) =>
        $"左右 {settings.HorizontalLeftPart:0.###}:{settings.HorizontalRightPart:0.###}，上下 {settings.VerticalTopPart:0.###}:{settings.VerticalBottomPart:0.###}";

    private enum SelectionPromptStatus
    {
        Success,
        EndCommand,
        Cancel
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

    private readonly struct RectangleRange
    {
        private readonly Matrix3d _analysisToWorld;
        private readonly Matrix3d _worldToAnalysis;

        internal RectangleRange(double left, double bottom, double right, double top, double elevation)
            : this(left, bottom, right, top, elevation, Matrix3d.Identity, Matrix3d.Identity)
        {
        }

        private RectangleRange(
            double left,
            double bottom,
            double right,
            double top,
            double elevation,
            Matrix3d analysisToWorld,
            Matrix3d worldToAnalysis)
        {
            Left = left;
            Bottom = bottom;
            Right = right;
            Top = top;
            Elevation = elevation;
            _analysisToWorld = analysisToWorld;
            _worldToAnalysis = worldToAnalysis;
        }

        internal double Left { get; }
        internal double Bottom { get; }
        internal double Right { get; }
        internal double Top { get; }
        internal double Elevation { get; }

        internal double Area => Math.Max(0.0, Right - Left) * Math.Max(0.0, Top - Bottom);

        internal static RectangleRange FromAnalysisExtents(
            Extents3d extents,
            Matrix3d analysisToWorld,
            Matrix3d worldToAnalysis)
        {
            return new RectangleRange(
                extents.MinPoint.X,
                extents.MinPoint.Y,
                extents.MaxPoint.X,
                extents.MaxPoint.Y,
                (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5,
                analysisToWorld,
                worldToAnalysis);
        }

        internal Point3d[] BuildFoldPoints(FoldBreakSettingsDto settings)
        {
            var points = FoldBreakCommandService.BuildFoldPoints(
                Left,
                Bottom,
                Right,
                Top,
                Elevation,
                settings);

            for (var i = 0; i < points.Length; i++)
                points[i] = points[i].TransformBy(_analysisToWorld);

            return points;
        }

        internal bool ContainsWorldPoint(Point3d point, double tolerance)
        {
            var analysisPoint = point.TransformBy(_worldToAnalysis);
            return analysisPoint.X >= Left - tolerance &&
                   analysisPoint.X <= Right + tolerance &&
                   analysisPoint.Y >= Bottom - tolerance &&
                   analysisPoint.Y <= Top + tolerance;
        }
    }
}
