using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadColor = Autodesk.AutoCAD.Colors.Color;
using CadColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;
using GdiColor = System.Drawing.Color;

namespace C_toolsAaaPlugin;

internal static class AaaBlockThumbnailService
{
    private const int PreviewWidth = 256;
    private const int PreviewHeight = 192;
    private const float PreviewPadding = 18f;
    private const int MaxExplodeDepth = 6;
    private static readonly GdiColor DefaultPreviewStroke = GdiColor.FromArgb(255, 255, 255, 255);
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static BitmapSource? Load(AaaBlockCatalogItem item)
    {
        if (item == null)
            return null;

        var previewPath = item.PreviewAssetPath;
        if (string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath))
            return null;

        var cacheKey = item.PreviewCacheKey;
        return Cache.GetOrAdd(cacheKey, _ => LoadCore(previewPath));
    }

    private static BitmapSource? LoadCore(string filePath)
    {
        try
        {
            using var database = new Database(false, true);
            database.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, true, "");

            var rendered = TryRenderGeometryPreview(database);
            if (rendered != null)
                return rendered;

            using var bitmap = database.ThumbnailBitmap;
            var thumbnail = CreateBitmapSource(bitmap);
            if (thumbnail != null)
                return thumbnail;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块缩略图失败", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块缩略图失败（权限）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块缩略图失败", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块缩略图失败", ex);
        }

        using var fallback = CreateFallbackPreview(Path.GetFileNameWithoutExtension(filePath));
        return CreateBitmapSource(fallback);
    }

    private static BitmapSource? TryRenderGeometryPreview(Database database)
    {
        var previewEntities = new List<PreviewEntity>();

        try
        {
            using var transaction = database.TransactionManager.StartOpenCloseTransaction();
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (!blockTable.Has(BlockTableRecord.ModelSpace))
                return null;

            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            Extents3d? extents = null;

            foreach (ObjectId entityId in modelSpace)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity)
                    continue;

                CollectPreviewEntities(
                    entity,
                    ownsEntity: false,
                    previewEntities,
                    ref extents,
                    depth: 0,
                    transaction,
                    database,
                    inheritedByBlockColor: null,
                    inheritedLayerName: null);
            }

            transaction.Commit();

            if (previewEntities.Count == 0 || !extents.HasValue)
                return null;

            using var bitmap = DrawPreviewBitmap(previewEntities, extents.Value);
            return CreateBitmapSource(bitmap);
        }
        finally
        {
            foreach (var previewEntity in previewEntities)
                previewEntity.Entity.Dispose();
        }
    }

    private static void CollectPreviewEntities(
        Entity entity,
        bool ownsEntity,
        ICollection<PreviewEntity> previewEntities,
        ref Extents3d? extents,
        int depth,
        Transaction transaction,
        Database database,
        GdiColor? inheritedByBlockColor,
        string? inheritedLayerName)
    {
        var keepEntity = false;

        try
        {
            if (!ShouldRenderEntity(entity))
                return;

            var effectiveLayerName = ResolveEffectiveLayerName(entity, inheritedLayerName);
            var effectiveColor = ResolvePreviewColor(
                entity,
                transaction,
                database,
                inheritedByBlockColor,
                effectiveLayerName);

            if (entity is BlockReference blockReference && depth < MaxExplodeDepth && TryExplode(blockReference, out var exploded))
            {
                foreach (DBObject item in exploded)
                {
                    if (item is Entity childEntity)
                    {
                        CollectPreviewEntities(
                            childEntity,
                            ownsEntity: true,
                            previewEntities,
                            ref extents,
                            depth + 1,
                            transaction,
                            database,
                            effectiveColor,
                            effectiveLayerName);
                    }
                    else
                    {
                        item.Dispose();
                    }
                }

                return;
            }

            var previewEntity = ownsEntity ? entity : entity.Clone() as Entity;
            if (previewEntity == null)
                return;

            if (!TryExpandExtents(previewEntity, ref extents))
            {
                previewEntity.Dispose();
                return;
            }

            previewEntities.Add(new PreviewEntity(previewEntity, effectiveColor));
            keepEntity = true;
        }
        finally
        {
            if (ownsEntity && !keepEntity)
                entity.Dispose();
        }
    }

    private static bool TryExplode(BlockReference blockReference, out DBObjectCollection exploded)
    {
        exploded = new DBObjectCollection();

        try
        {
            blockReference.Explode(exploded);
            return exploded.Count > 0;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 展开图块缩略图预览失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 展开图块缩略图预览失败（无效操作）", ex);
        }

        exploded.Dispose();
        exploded = new DBObjectCollection();
        return false;
    }

    private static bool ShouldRenderEntity(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 判断图块预览可见性失败", ex);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 判断图块预览可见性失败（无效操作）", ex);
            return true;
        }
    }

    private static bool TryExpandExtents(Entity entity, ref Extents3d? extents)
    {
        try
        {
            var entityExtents = entity.GeometricExtents;
            extents = extents.HasValue
                ? UnionExtents(extents.Value, entityExtents)
                : entityExtents;
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Bitmap DrawPreviewBitmap(IReadOnlyCollection<PreviewEntity> previewEntities, Extents3d extents)
    {
        var bitmap = new Bitmap(PreviewWidth, PreviewHeight);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(GdiColor.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var viewport = PreviewViewport.Create(extents);
        foreach (var previewEntity in previewEntities)
            DrawEntity(graphics, previewEntity, viewport);

        return bitmap;
    }

    private static void DrawEntity(Graphics graphics, PreviewEntity previewEntity, PreviewViewport viewport)
    {
        var entity = previewEntity.Entity;
        using var pen = new Pen(previewEntity.Color, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (entity)
        {
            case Curve curve when TryDrawCurve(graphics, curve, pen, viewport):
                return;
            case Solid solid:
                DrawSolid(graphics, solid, pen, viewport);
                return;
            case DBText text:
                DrawText(graphics, text.TextString, text.Position, pen.Color, viewport);
                return;
            case MText mtext:
                DrawText(graphics, mtext.Contents, mtext.Location, pen.Color, viewport);
                return;
            default:
                DrawBoundsFallback(graphics, entity, pen, viewport);
                return;
        }
    }

    private static bool TryDrawCurve(Graphics graphics, Curve curve, Pen pen, PreviewViewport viewport)
    {
        try
        {
            var start = curve.StartParam;
            var end = curve.EndParam;
            var segments = DetermineCurveSegments(curve, start, end);
            if (segments < 1)
                return false;

            var points = new PointF[segments + 1];
            for (var index = 0; index <= segments; index++)
            {
                var parameter = start + (end - start) * index / segments;
                points[index] = viewport.Project(curve.GetPointAtParameter(parameter));
            }

            if (points.Length < 2)
                return false;

            graphics.DrawLines(pen, points);
            if (IsClosedCurve(curve))
                graphics.DrawLine(pen, points[^1], points[0]);

            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DrawSolid(Graphics graphics, Solid solid, Pen pen, PreviewViewport viewport)
    {
        try
        {
            var points = new[]
            {
                viewport.Project(solid.GetPointAt(0)),
                viewport.Project(solid.GetPointAt(1)),
                viewport.Project(solid.GetPointAt(2)),
                viewport.Project(solid.GetPointAt(3))
            };

            graphics.DrawPolygon(pen, points);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
    }

    private static void DrawText(
        Graphics graphics,
        string? text,
        Point3d position,
        GdiColor color,
        PreviewViewport viewport)
    {
        var content = (text ?? "").Trim();
        if (content.Length == 0)
            return;

        var drawText = content.Length > 8 ? content[..8] : content;
        var anchor = viewport.Project(position);
        using var font = new System.Drawing.Font("Microsoft YaHei UI", 9f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };

        var layoutRect = new RectangleF(anchor.X, anchor.Y - 8f, PreviewWidth - anchor.X - 6f, 16f);
        graphics.DrawString(drawText, font, brush, layoutRect, format);
    }

    private static void DrawBoundsFallback(Graphics graphics, Entity entity, Pen pen, PreviewViewport viewport)
    {
        try
        {
            var extents = entity.GeometricExtents;
            var topLeft = viewport.Project(new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0));
            var bottomRight = viewport.Project(new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0));
            var rectangle = RectangleF.FromLTRB(
                Math.Min(topLeft.X, bottomRight.X),
                Math.Min(topLeft.Y, bottomRight.Y),
                Math.Max(topLeft.X, bottomRight.X),
                Math.Max(topLeft.Y, bottomRight.Y));

            if (rectangle.Width < 2f || rectangle.Height < 2f)
                return;

            graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
    }

    private static int DetermineCurveSegments(Curve curve, double startParam, double endParam)
    {
        if (curve is Line)
            return 1;
        if (curve is Circle)
            return 48;
        if (curve is Arc)
            return 36;
        if (curve is Ellipse)
            return 40;
        if (curve is Spline)
            return 64;

        var span = Math.Abs(endParam - startParam);
        if (span <= 1d)
            return 12;
        if (span <= 8d)
            return 24;
        return 48;
    }

    private static bool IsClosedCurve(Curve curve)
    {
        try
        {
            return curve.Closed;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    private static GdiColor ResolvePreviewColor(
        Entity entity,
        Transaction transaction,
        Database database,
        GdiColor? inheritedByBlockColor,
        string effectiveLayerName)
    {
        return DefaultPreviewStroke;
    }

    private static string ResolveEffectiveLayerName(Entity entity, string? inheritedLayerName)
    {
        try
        {
            var layerName = (entity.Layer ?? "").Trim();
            if (layerName.Length == 0)
                return (inheritedLayerName ?? "").Trim();

            if (string.Equals(layerName, "0", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(inheritedLayerName))
                return inheritedLayerName!;

            return layerName;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return (inheritedLayerName ?? "").Trim();
        }
        catch (InvalidOperationException)
        {
            return (inheritedLayerName ?? "").Trim();
        }
    }

    private static GdiColor ResolveLayerPreviewColor(Transaction transaction, Database database, string? layerName)
    {
        var trimmedLayerName = (layerName ?? "").Trim();
        if (trimmedLayerName.Length == 0)
            return DefaultPreviewStroke;

        try
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(trimmedLayerName))
                return DefaultPreviewStroke;

            if (transaction.GetObject(layerTable[trimmedLayerName], OpenMode.ForRead) is not LayerTableRecord layerRecord)
                return DefaultPreviewStroke;

            return ConvertCadColorToPreview(layerRecord.Color);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return DefaultPreviewStroke;
        }
        catch (InvalidOperationException)
        {
            return DefaultPreviewStroke;
        }
    }

    private static GdiColor ConvertCadColorToPreview(CadColor color)
    {
        try
        {
            if (color.ColorMethod == CadColorMethod.Foreground || color.ColorIndex == 7)
                return DefaultPreviewStroke;

            if (color.ColorMethod == CadColorMethod.ByLayer || color.ColorMethod == CadColorMethod.ByBlock)
                return DefaultPreviewStroke;

            return GdiColor.FromArgb(255, color.Red, color.Green, color.Blue);
        }
        catch
        {
            return DefaultPreviewStroke;
        }
    }

    private static Bitmap CreateFallbackPreview(string? displayName)
    {
        var bitmap = new Bitmap(PreviewWidth, PreviewHeight);
        using var graphics = Graphics.FromImage(bitmap);
        var borderColor = GdiColor.FromArgb(128, 176, 188, 204);
        var accentColor = DefaultPreviewStroke;
        using var borderPen = new Pen(borderColor, 1.2f);
        using var accentPen = new Pen(accentColor, 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(accentColor);
        using var font = new System.Drawing.Font("Microsoft YaHei UI", 11f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        graphics.Clear(GdiColor.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawRectangle(borderPen, 34, 24, PreviewWidth - 68, PreviewHeight - 74);
        graphics.DrawLine(accentPen, 50, PreviewHeight - 52, PreviewWidth - 50, 42);
        graphics.DrawLine(accentPen, 50, 42, PreviewWidth - 92, PreviewHeight - 52);
        graphics.DrawEllipse(borderPen, PreviewWidth / 2f - 10f, PreviewHeight / 2f - 18f, 20f, 20f);

        var label = (displayName ?? "").Trim();
        label = label.Length > 10 ? label[..10] : label;
        if (label.Length == 0)
            label = "BLOCK";

        graphics.DrawString(
            label,
            font,
            brush,
            new RectangleF(26f, PreviewHeight - 44f, PreviewWidth - 52f, 24f),
            format);

        return bitmap;
    }

    private static BitmapSource? CreateBitmapSource(Bitmap? bitmap)
    {
        if (bitmap == null)
            return null;

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private static Extents3d UnionExtents(Extents3d left, Extents3d right)
    {
        return new Extents3d(
            new Point3d(
                Math.Min(left.MinPoint.X, right.MinPoint.X),
                Math.Min(left.MinPoint.Y, right.MinPoint.Y),
                Math.Min(left.MinPoint.Z, right.MinPoint.Z)),
            new Point3d(
                Math.Max(left.MaxPoint.X, right.MaxPoint.X),
                Math.Max(left.MaxPoint.Y, right.MaxPoint.Y),
                Math.Max(left.MaxPoint.Z, right.MaxPoint.Z)));
    }

    private readonly struct PreviewViewport
    {
        private PreviewViewport(double minX, double minY, double maxY, float scale, float offsetX, float offsetY)
        {
            _minX = minX;
            _minY = minY;
            _maxY = maxY;
            _scale = scale;
            _offsetX = offsetX;
            _offsetY = offsetY;
        }

        private readonly double _minX;
        private readonly double _minY;
        private readonly double _maxY;
        private readonly float _scale;
        private readonly float _offsetX;
        private readonly float _offsetY;

        internal static PreviewViewport Create(Extents3d extents)
        {
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;

            var width = maxX - minX;
            var height = maxY - minY;
            if (width < 1e-6)
                width = 1d;
            if (height < 1e-6)
                height = 1d;

            var scaleX = (PreviewWidth - 2f * PreviewPadding) / width;
            var scaleY = (PreviewHeight - 2f * PreviewPadding) / height;
            var scale = (float)Math.Min(scaleX, scaleY);

            var contentWidth = (float)(width * scale);
            var contentHeight = (float)(height * scale);
            var offsetX = (PreviewWidth - contentWidth) / 2f;
            var offsetY = (PreviewHeight - contentHeight) / 2f;

            return new PreviewViewport(minX, minY, maxY, scale, offsetX, offsetY);
        }

        internal PointF Project(Point3d point)
        {
            return new PointF(
                _offsetX + (float)((point.X - _minX) * _scale),
                _offsetY + (float)((_maxY - point.Y) * _scale));
        }
    }

    private readonly struct PreviewEntity
    {
        internal PreviewEntity(Entity entity, GdiColor color)
        {
            Entity = entity;
            Color = color;
        }

        internal Entity Entity { get; }

        internal GdiColor Color { get; }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
