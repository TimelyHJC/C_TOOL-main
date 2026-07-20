using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using C_toolsShared;
using CadColor = Autodesk.AutoCAD.Colors.Color;

namespace C_toolsPlugin;

internal sealed class CurrentLayerSnapshot
{
    public string DocumentName { get; init; } = "—";
    public string LayerName { get; init; } = "—";
    public string Color { get; init; } = "—";
    public string Linetype { get; init; } = "—";
    public string LinetypeScale { get; init; } = "—";
    public string Lineweight { get; init; } = "—";
    public string DimStyle { get; init; } = "—";
    public string TextStyle { get; init; } = "—";
    public string MLeaderStyle { get; init; } = "—";
    public CadColor LayerColor { get; init; } = CadColor.FromColorIndex(ColorMethod.ByAci, 7);
    public CadColor ViewportColor { get; init; } = CadColor.FromColorIndex(ColorMethod.ByAci, 7);
    public string ShortcutAliasesText { get; init; } = "—";
    public string ShortcutStatusText { get; init; } = "—";
    public bool IsLayerOn { get; init; } = true;
    public bool IsFrozen { get; init; }
    public bool IsViewportFrozen { get; init; }
    public bool IsLocked { get; init; }
    public bool IsPlottable { get; init; } = true;
    public IReadOnlyList<CurrentLayerListItem> Layers { get; init; } = Array.Empty<CurrentLayerListItem>();
}

internal sealed class CurrentLayerListItem
{
    public string LayerName { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool IsOn { get; init; } = true;
    public bool IsFrozen { get; init; }
    public bool IsNewViewportFrozen { get; init; }
    public bool IsLocked { get; init; }

    public ImageSource? OnIcon => CurrentLayerStatusIconCatalog.TryGet(IsOn ? CurrentLayerStatusKind.Power : CurrentLayerStatusKind.PowerOff);
    public ImageSource? FreezeIcon => CurrentLayerStatusIconCatalog.TryGet(IsFrozen ? CurrentLayerStatusKind.Frozen : CurrentLayerStatusKind.Freeze);
    public ImageSource? NewViewportFreezeIcon => CurrentLayerStatusIconCatalog.TryGet(IsNewViewportFrozen ? CurrentLayerStatusKind.ViewportFrozen : CurrentLayerStatusKind.ViewportFreeze);
    public ImageSource? LockIcon => CurrentLayerStatusIconCatalog.TryGet(IsLocked ? CurrentLayerStatusKind.Lock : CurrentLayerStatusKind.Unlock);

    public string OnToolTip => IsOn ? "关闭图层" : "打开图层";
    public string FreezeToolTip => IsFrozen ? "解冻图层" : "冻结图层";
    public string NewViewportFreezeToolTip => IsNewViewportFrozen ? "取消新视口冻结" : "新视口冻结";
    public string LockToolTip => IsLocked ? "解锁图层" : "锁定图层";
}

internal sealed class CurrentLayerShortcutCandidate
{
    public string LayerName { get; init; } = "";
    public string AliasCell { get; init; } = "";
}

internal enum CurrentLayerStatusKind
{
    Power,
    PowerOff,
    Freeze,
    Frozen,
    ViewportFreeze,
    ViewportFrozen,
    Lock,
    Unlock,
    Plot,
    Color
}

internal enum LayerToggleAction
{
    On,
    Freeze,
    ViewportFreeze,
    Lock
}

internal enum LayerTableToggleAction
{
    On,
    Freeze,
    NewViewportFreeze,
    Lock
}

internal static class CurrentLayerSnapshotService
{
    internal static CurrentLayerSnapshot Capture(Document doc, IReadOnlyList<CurrentLayerShortcutCandidate> candidates)
    {
        return CadDatabaseScope.Read(
            doc,
            (db, tr) =>
            {
                if (db.Clayer.IsNull)
                    throw new InvalidOperationException("当前图层对象为空。");

                var layerId = ResolveCurrentContextLayerId(doc, tr, db);
                var layerRecord = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead);
                var layerName = (layerRecord.Name ?? "").Trim();

                var matchedRows = candidates
                    .Where(x => string.Equals((x.LayerName ?? "").Trim(), layerName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var aliases = matchedRows
                    .SelectMany(x => CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(x.AliasCell))
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var properties = CadObjectPropertiesSnapshotService.Capture(doc, db, tr);
                var viewportInfo = ReadCurrentViewportLayerInfo(doc, tr, layerId, layerRecord.Color);

                return new CurrentLayerSnapshot
                {
                    DocumentName = properties.DocumentName,
                    LayerName = layerName.Length == 0 ? properties.Layer : layerName,
                    Color = properties.Color,
                    Linetype = properties.Linetype,
                    LinetypeScale = properties.LinetypeScale,
                    Lineweight = properties.Lineweight,
                    DimStyle = properties.DimStyle,
                    TextStyle = properties.TextStyle,
                    MLeaderStyle = properties.MLeaderStyle,
                    LayerColor = layerRecord.Color,
                    ViewportColor = viewportInfo.Color,
                    ShortcutAliasesText = aliases.Count == 0 ? "未配置" : string.Join(" / ", aliases),
                    ShortcutStatusText = BuildShortcutStatusText(matchedRows.Count, aliases.Count),
                    IsLayerOn = !layerRecord.IsOff,
                    IsFrozen = layerRecord.IsFrozen,
                    IsViewportFrozen = viewportInfo.IsFrozen,
                    IsLocked = layerRecord.IsLocked,
                    IsPlottable = layerRecord.IsPlottable,
                    Layers = LoadLayers(tr, db, layerId)
                };
            },
            requireDocumentLock: true);
    }

    internal static ObjectId ResolveCurrentContextLayerId(Document doc, Transaction tr, Database db)
    {
        if (TryGetFirstSelectedEntityLayerId(doc, tr, out var selectedLayerId))
            return selectedLayerId;

        if (db.Clayer.IsNull || !db.Clayer.IsValid || db.Clayer.IsErased)
            throw new InvalidOperationException("当前图层对象为空。");

        return db.Clayer;
    }

    private static bool TryGetFirstSelectedEntityLayerId(Document doc, Transaction tr, out ObjectId layerId)
    {
        layerId = ObjectId.Null;

        if (!TryGetImpliedSelectionObjectIds(doc, out var objectIds))
            return false;

        foreach (var objectId in objectIds)
        {
            if (objectId.IsNull || !objectId.IsValid || objectId.IsErased)
                continue;

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null)
            {
                continue;
            }

            var entityLayerId = entity.LayerId;
            if (entityLayerId.IsNull || !entityLayerId.IsValid || entityLayerId.IsErased)
                continue;

            layerId = entityLayerId;
            return true;
        }

        return false;
    }

    internal static bool TryGetImpliedSelectionObjectIds(Document doc, out ObjectId[] objectIds)
    {
        objectIds = Array.Empty<ObjectId>();

        Autodesk.AutoCAD.EditorInput.PromptSelectionResult? implied;
        try
        {
            implied = doc.Editor.SelectImplied();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }

        if (implied == null ||
            implied.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK ||
            implied.Value == null ||
            implied.Value.Count == 0)
        {
            return false;
        }

        objectIds = implied.Value.GetObjectIds();
        return objectIds.Length > 0;
    }

    private static string BuildShortcutStatusText(int matchedRowCount, int aliasCount)
    {
        if (matchedRowCount == 0)
            return "未在已保存的图层快捷键中配置该图层。";
        if (aliasCount == 0)
            return "已保存该图层，但当前没有快捷键。";
        return $"已匹配 {aliasCount} 个快捷键。";
    }

    private static CurrentViewportLayerInfo ReadCurrentViewportLayerInfo(
        Document doc,
        Transaction tr,
        ObjectId layerId,
        CadColor layerColor)
    {
        if (!TryOpenCurrentViewport(doc, tr, OpenMode.ForRead, out var viewport) || viewport == null)
            return new CurrentViewportLayerInfo(false, layerColor);

        var isFrozen = viewport.IsLayerFrozenInViewport(layerId);
        var color = layerColor;
        if (CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead, out var layer) &&
            layer != null &&
            layer.HasViewportOverrides(viewport.ObjectId))
        {
            var overrides = layer.GetViewportOverrides(viewport.ObjectId);
            if (overrides.IsColorOverridden)
                color = overrides.Color;
        }

        return new CurrentViewportLayerInfo(isFrozen, color);
    }

    internal static bool TryOpenCurrentViewport(
        Document doc,
        Transaction tr,
        OpenMode openMode,
        out Viewport? viewport)
    {
        viewport = null;
        var viewportId = doc.Editor.CurrentViewportObjectId;
        if (viewportId.IsNull || !viewportId.IsValid || viewportId.IsErased)
            return false;

        return CadDatabaseScope.TryOpenAs(tr, viewportId, openMode, out viewport) && viewport != null;
    }

    private readonly record struct CurrentViewportLayerInfo(bool IsFrozen, CadColor Color);

    private static string FormatDocumentName(Document doc)
    {
        try
        {
            var fileName = Path.GetFileName(doc.Name ?? "");
            return string.IsNullOrWhiteSpace(fileName) ? "未命名图纸" : fileName;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层状态：格式化当前图纸名称失败（参数错误）", ex);
            return "当前活动图纸";
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层状态：格式化当前图纸名称失败（路径过长）", ex);
            return "当前活动图纸";
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层状态：格式化当前图纸名称失败（不支持）", ex);
            return "当前活动图纸";
        }
    }

    private static IReadOnlyList<CurrentLayerListItem> LoadLayers(Transaction tr, Database db, ObjectId currentLayerId)
    {
        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
        var layers = new List<CurrentLayerListItem>();
        foreach (ObjectId layerId in layerTable)
        {
            if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead, out var record) ||
                record == null)
            {
                continue;
            }

            var name = (record.Name ?? "").Trim();
            if (name.Length == 0)
                continue;

            layers.Add(new CurrentLayerListItem
            {
                LayerName = name,
                IsCurrent = layerId == currentLayerId,
                IsOn = !record.IsOff,
                IsFrozen = record.IsFrozen,
                IsNewViewportFrozen = !record.ViewportVisibilityDefault,
                IsLocked = record.IsLocked
            });
        }

        return layers
            .GroupBy(x => x.LayerName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.LayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal static class CurrentLayerStatusIconCatalog
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<CurrentLayerStatusKind, ImageSource?> Cache = new();

    internal static ImageSource? TryGet(CurrentLayerStatusKind kind)
    {
        lock (SyncRoot)
        {
            if (Cache.TryGetValue(kind, out var cached))
                return cached;

            var image = LoadIcon(kind);
            Cache[kind] = image;
            return image;
        }
    }

    private static ImageSource? LoadIcon(CurrentLayerStatusKind kind)
    {
        var acadRoot = FindAcadRoot();
        if (acadRoot is null || acadRoot.Length == 0)
            return null;

            var resourceKey = kind switch
            {
                CurrentLayerStatusKind.Power => "resources/lamgr_listviewicons_10_dark.tif",
                CurrentLayerStatusKind.PowerOff => "resources/lamgr_listviewicons_19_dark.tif",
                CurrentLayerStatusKind.Freeze => "resources/lamgr_listviewicons_7_dark.tif",
                CurrentLayerStatusKind.Frozen => "resources/lamgr_listviewicons_23_dark.tif",
                CurrentLayerStatusKind.ViewportFreeze => "resources/lamgr_listviewicons_8_dark.tif",
                CurrentLayerStatusKind.ViewportFrozen => "resources/lamgr_listviewicons_12_dark.tif",
                CurrentLayerStatusKind.Lock => "resources/lamgr_listviewicons_6_dark.tif",
                CurrentLayerStatusKind.Unlock => "resources/lamgr_listviewicons_9_dark.tif",
                CurrentLayerStatusKind.Plot => "resources/lamgr_listviewicons_52_dark.tif",
                CurrentLayerStatusKind.Color => "resources/images/color.tif",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };

        foreach (var cultureName in GetCultureCandidates())
        {
            var image = TryLoadIconFromCulture(acadRoot, cultureName, resourceKey);
            if (image != null)
                return image;
        }

        return null;
    }

    private static string? FindAcadRoot()
    {
        foreach (var assembly in new[] { typeof(LayerTableRecord).Assembly, typeof(Document).Assembly, Assembly.GetEntryAssembly() })
        {
            try
            {
                var location = assembly?.Location;
                if (string.IsNullOrWhiteSpace(location))
                    continue;

                var directory = Path.GetDirectoryName(location);
                if (!string.IsNullOrWhiteSpace(directory))
                    return directory;
            }
            catch (NotSupportedException)
            {
            }
        }

        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(processPath))
                return Path.GetDirectoryName(processPath);
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private static IReadOnlyList<string> GetCultureCandidates()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCultureName(results, seen, System.Globalization.CultureInfo.CurrentUICulture.Name);
        AddCultureName(results, seen, System.Globalization.CultureInfo.CurrentCulture.Name);
        AddCultureName(results, seen, "zh-CN");
        AddCultureName(results, seen, "en-US");
        return results;
    }

    private static void AddCultureName(List<string> results, HashSet<string> seen, string? cultureName)
    {
        var normalized = (cultureName ?? "").Trim();
        if (normalized.Length == 0)
            return;

        if (seen.Add(normalized))
            results.Add(normalized);
    }

    private static ImageSource? TryLoadIconFromCulture(string acadRoot, string cultureName, string resourceKey)
    {
        var assemblyPath = Path.Combine(acadRoot, cultureName, "AcLayer.resources.dll");
        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            var manager = new ResourceManager($"AcLayer.g.{cultureName}", assembly);
            if (manager.GetObject(resourceKey, System.Globalization.CultureInfo.InvariantCulture) is not Stream stream)
                return null;

            using (stream)
            using (var buffer = new MemoryStream())
            {
                if (stream.CanSeek)
                    stream.Position = 0;

                stream.CopyTo(buffer);
                buffer.Position = 0;

                var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    return null;

                var image = decoder.Frames[0];
                image.Freeze();
                return image;
            }
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"当前图层状态：读取层状态图标失败（{cultureName}，无效操作）", ex);
            return null;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"当前图层状态：读取层状态图标失败（{cultureName}，IO）", ex);
            return null;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"当前图层状态：读取层状态图标失败（{cultureName}，不支持）", ex);
            return null;
        }
    }
}
