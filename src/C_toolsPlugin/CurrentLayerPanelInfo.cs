using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using AdWin = Autodesk.Windows;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
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
    public IReadOnlyList<string> LayerNames { get; init; } = Array.Empty<string>();
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

internal sealed class RibbonCommandHandler : ICommand
{
    private readonly Action _execute;

    internal RibbonCommandHandler(Action execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
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

                var layerRecord = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, db.Clayer, OpenMode.ForRead);
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
                var viewportInfo = ReadCurrentViewportLayerInfo(doc, tr, db.Clayer, layerRecord.Color);

                return new CurrentLayerSnapshot
                {
                    DocumentName = properties.DocumentName,
                    LayerName = properties.Layer,
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
                    LayerNames = LoadLayerNames(tr, db)
                };
            },
            requireDocumentLock: true);
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
            C_toolsDiagnostics.LogNonFatal("Ribbon：格式化当前图纸名称失败（参数错误）", ex);
            return "当前活动图纸";
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Ribbon：格式化当前图纸名称失败（路径过长）", ex);
            return "当前活动图纸";
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Ribbon：格式化当前图纸名称失败（不支持）", ex);
            return "当前活动图纸";
        }
    }

    private static IReadOnlyList<string> LoadLayerNames(Transaction tr, Database db)
    {
        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
        var names = new List<string>();
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
            names.Add(name);
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
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
        foreach (var assembly in new[] { typeof(AdWin.ComponentManager).Assembly, typeof(LayerTableRecord).Assembly, Assembly.GetEntryAssembly() })
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
            C_toolsDiagnostics.LogNonFatal($"Ribbon：读取层状态图标失败（{cultureName}，无效操作）", ex);
            return null;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"Ribbon：读取层状态图标失败（{cultureName}，IO）", ex);
            return null;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"Ribbon：读取层状态图标失败（{cultureName}，不支持）", ex);
            return null;
        }
    }
}

internal static class CurrentLayerRibbonManager
{
    private const string RibbonTabId = "C_toolsPlugin.Ribbon.CurrentLayer.Tab";
    private const string RibbonPropertiesPanelId = "C_toolsPlugin.Ribbon.CurrentLayer.PropertiesPanel";
    private const string RibbonStylePanelId = "C_toolsPlugin.Ribbon.CurrentLayer.StylePanel";
    private const string RibbonLayerActionsPanelId = "C_toolsPlugin.Ribbon.CurrentLayer.LayerActionsPanel";
    private const double PropertiesCaptionColumnWidth = 96;
    private const double StyleCaptionColumnWidth = 84;
    private const int RefreshDebounceMilliseconds = 250;

    private static readonly object SyncRoot = new();

    private static bool s_initialized;
    private static int s_refreshVersion;
    private static DispatcherTimer? s_refreshDebounceTimer;
    private static AdWin.RibbonTab? s_tab;
    private static AdWin.RibbonLabel? s_propertyColorValueLabel;
    private static AdWin.RibbonLabel? s_propertyDimStyleValueLabel;
    private static AdWin.RibbonLabel? s_propertyTextStyleValueLabel;
    private static AdWin.RibbonLabel? s_propertyMLeaderStyleValueLabel;
    private static AdWin.RibbonLabel? s_layerValueLabel;
    private static AdWin.RibbonLabel? s_shortcutValueLabel;
    private static AdWin.RibbonLabel? s_documentValueLabel;
    private static AdWin.RibbonButton? s_layerOnButton;
    private static AdWin.RibbonButton? s_layerFreezeButton;
    private static AdWin.RibbonButton? s_layerViewportFreezeButton;
    private static AdWin.RibbonButton? s_layerLockButton;
    private static AdWin.RibbonButton? s_layerColorButton;
    private static AdWin.RibbonButton? s_layerViewportColorButton;
    private static readonly RibbonCommandHandler ToggleLayerOnCommand = new(() => ToggleCurrentLayer(LayerToggleAction.On));
    private static readonly RibbonCommandHandler ToggleLayerFreezeCommand = new(() => ToggleCurrentLayer(LayerToggleAction.Freeze));
    private static readonly RibbonCommandHandler ToggleViewportFreezeCommand = new(() => ToggleCurrentLayer(LayerToggleAction.ViewportFreeze));
    private static readonly RibbonCommandHandler ToggleLayerLockCommand = new(() => ToggleCurrentLayer(LayerToggleAction.Lock));
    private static readonly RibbonCommandHandler SetLayerColorCommand = new(() => SetCurrentLayerColor(useViewportOverride: false));
    private static readonly RibbonCommandHandler SetViewportLayerColorCommand = new(() => SetCurrentLayerColor(useViewportOverride: true));

    internal static void Initialize()
    {
        if (s_initialized)
            return;

        s_initialized = true;
        AcAp.Idle += OnFirstIdle;
        AcAp.DocumentManager.DocumentCreated += OnDocumentChanged;
        AcAp.DocumentManager.DocumentActivated += OnDocumentChanged;
        AcAp.SystemVariableChanged += OnSystemVariableChanged;
    }

    internal static void Terminate()
    {
        if (!s_initialized)
            return;

        s_initialized = false;
        AcAp.Idle -= OnFirstIdle;
        AcAp.DocumentManager.DocumentCreated -= OnDocumentChanged;
        AcAp.DocumentManager.DocumentActivated -= OnDocumentChanged;
        AcAp.SystemVariableChanged -= OnSystemVariableChanged;
        StopPendingRefresh();
        RemoveRibbonTab();
    }

    internal static void RequestRefresh()
    {
        if (!EnsureRibbonCreated())
            return;

        var refreshVersion = Interlocked.Increment(ref s_refreshVersion);
        var candidates = LoadShortcutCandidates();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    CurrentLayerSnapshot? snapshot = null;
                    string? error = null;

                    try
                    {
                        var doc = AcAp.DocumentManager.MdiActiveDocument;
                        if (doc == null)
                            error = "当前没有活动图纸。";
                        else
                            snapshot = CurrentLayerSnapshotService.Capture(doc, candidates);
                    }
                    catch (InvalidOperationException ex)
                    {
                        error = ex.Message;
                        C_toolsDiagnostics.LogNonFatal("Ribbon：读取当前图层失败（无效操作）", ex);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        C_toolsDiagnostics.LogNonFatal("Ribbon：读取当前图层失败", ex);
                    }

                    PostRibbonUpdate(refreshVersion, snapshot, error);
                },
                null);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Ribbon：调度当前图层刷新失败（无效操作）", ex);
            PostRibbonUpdate(refreshVersion, null, ex.Message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("Ribbon：调度当前图层刷新失败", ex);
            PostRibbonUpdate(refreshVersion, null, ex.Message);
        }
    }

    private static void RequestRefreshDebounced()
    {
        if (!s_initialized)
            return;

        var ribbon = AdWin.ComponentManager.Ribbon;
        var dispatcher = ribbon?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RequestRefreshDebounced));
            return;
        }

        if (!EnsureRibbonCreated())
            return;

        if (s_refreshDebounceTimer == null)
        {
            s_refreshDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(RefreshDebounceMilliseconds)
            };
            s_refreshDebounceTimer.Tick += OnRefreshDebounceTimerTick;
        }

        s_refreshDebounceTimer.Stop();
        s_refreshDebounceTimer.Start();
    }

    private static void OnRefreshDebounceTimerTick(object? sender, EventArgs e)
    {
        StopPendingRefresh();
        RequestRefresh();
    }

    private static void StopPendingRefresh()
    {
        if (s_refreshDebounceTimer == null)
            return;

        s_refreshDebounceTimer.Stop();
        s_refreshDebounceTimer.Tick -= OnRefreshDebounceTimerTick;
        s_refreshDebounceTimer = null;
    }

    private static void OnFirstIdle(object? sender, EventArgs e)
    {
        if (!EnsureRibbonCreated())
            return;

        AcAp.Idle -= OnFirstIdle;
        RequestRefresh();
    }

    private static void OnDocumentChanged(object? sender, DocumentCollectionEventArgs e) => RequestRefreshDebounced();

    private static void OnSystemVariableChanged(object? sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
    {
        try
        {
            var name = e?.Name?.Trim();
            if (name == null || name.Length == 0)
                return;

            if (string.Equals(name, SystemVariableNames.WsCurrent, StringComparison.OrdinalIgnoreCase))
                EnsureRibbonCreated();

            if (!ShouldRefreshForSystemVariable(name))
                return;

            RequestRefreshDebounced();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("Ribbon：SystemVariableChanged 处理失败", ex);
        }
    }

    private static void PostRibbonUpdate(int refreshVersion, CurrentLayerSnapshot? snapshot, string? error)
    {
        var ribbon = AdWin.ComponentManager.Ribbon;
        if (ribbon == null)
            return;

        ribbon.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (refreshVersion != Volatile.Read(ref s_refreshVersion))
                return;
            if (!EnsureRibbonCreated())
                return;

            if (snapshot == null)
            {
                string errorText;
                if (string.IsNullOrWhiteSpace(error))
                    errorText = "读取失败";
                else
                    errorText = error!.Trim();
                UpdatePanels(null, errorText);
                UpdateLabel(s_shortcutValueLabel, "—", "图层快捷键");
                UpdateLabel(s_layerValueLabel, "—", errorText);
                UpdateLabel(s_documentValueLabel, errorText, errorText);
                UpdateLayerActionButtons(null);
                return;
            }

            UpdatePanels(snapshot);
            UpdateLayerActionButtons(snapshot);
            UpdateLabel(s_shortcutValueLabel, snapshot.ShortcutAliasesText, snapshot.ShortcutStatusText);
            UpdateLabel(s_layerValueLabel, snapshot.LayerName, snapshot.LayerName);
            UpdateLabel(s_documentValueLabel, snapshot.DocumentName, "当前活动图纸");
        }));
    }

    private static bool EnsureRibbonCreated()
    {
        var ribbon = AdWin.ComponentManager.Ribbon;
        if (ribbon == null)
            return false;

        lock (SyncRoot)
        {
            var tabs = ribbon.Tabs.Cast<AdWin.RibbonTab>()
                .Where(x => string.Equals(x.Id, RibbonTabId, StringComparison.Ordinal))
                .ToList();

            if (tabs.Any(x => ReferenceEquals(x, s_tab)))
            {
                foreach (var extra in tabs.Where(x => !ReferenceEquals(x, s_tab)).ToList())
                    ribbon.Tabs.Remove(extra);
                return true;
            }

            foreach (var tab in tabs)
                ribbon.Tabs.Remove(tab);

            ResetReferences();

            s_tab = BuildRibbonTab();
            ribbon.Tabs.Add(s_tab);
            return true;
        }
    }

    private static void RemoveRibbonTab()
    {
        var ribbon = AdWin.ComponentManager.Ribbon;
        if (ribbon == null)
        {
            ResetReferences();
            return;
        }

        lock (SyncRoot)
        {
            var tabs = ribbon.Tabs.Cast<AdWin.RibbonTab>()
                .Where(x => string.Equals(x.Id, RibbonTabId, StringComparison.Ordinal))
                .ToList();

            foreach (var tab in tabs)
                ribbon.Tabs.Remove(tab);

            ResetReferences();
        }
    }

    private static AdWin.RibbonTab BuildRibbonTab()
    {
        var tab = new AdWin.RibbonTab
        {
            Id = RibbonTabId,
            Name = "C_TOOL",
            Title = "C_TOOL",
            IsVisible = true
        };

        tab.Panels.Add(BuildPropertiesPanel());
        tab.Panels.Add(BuildLayerActionsPanel());
        tab.Panels.Add(BuildStylePanel());
        return tab;
    }

    private static AdWin.RibbonPanel BuildPropertiesPanel()
    {
        var source = new AdWin.RibbonPanelSource
        {
            Id = RibbonPropertiesPanelId,
            Title = string.Empty
        };

        var row = CreateLeftAlignedRowPanel();
        AddCaptionValueRow(row, "快捷键", PropertiesCaptionColumnWidth, out s_shortcutValueLabel, "正在读取...", 140);
        row.Items.Add(new AdWin.RibbonRowBreak());
        AddCaptionValueRow(row, "当前图层", PropertiesCaptionColumnWidth, out s_layerValueLabel, "正在读取...", 220);
        row.Items.Add(new AdWin.RibbonRowBreak());
        AddCaptionValueRow(row, "图纸", PropertiesCaptionColumnWidth, out s_documentValueLabel, "等待刷新...", 250);
        row.Items.Add(new AdWin.RibbonRowBreak());
        AddCaptionValueRow(row, "颜色", PropertiesCaptionColumnWidth, out s_propertyColorValueLabel, "—", 120);

        source.Items.Add(row);
        return new AdWin.RibbonPanel { Source = source };
    }

    private static AdWin.RibbonPanel BuildLayerActionsPanel()
    {
        var source = new AdWin.RibbonPanelSource
        {
            Id = RibbonLayerActionsPanelId,
            Title = string.Empty
        };

        var row = CreateLeftAlignedRowPanel();
        row.Items.Add(CreateIconButton(
            "开",
            "切换当前图层开/关",
            CurrentLayerStatusKind.Power,
            ToggleLayerOnCommand,
            out s_layerOnButton));
        row.Items.Add(CreateIconButton(
            "冻结",
            "切换当前图层冻结/解冻",
            CurrentLayerStatusKind.Freeze,
            ToggleLayerFreezeCommand,
            out s_layerFreezeButton));
        row.Items.Add(CreateIconButton(
            "视口冻结",
            "切换当前图层在当前视口冻结/解冻",
            CurrentLayerStatusKind.ViewportFreeze,
            ToggleViewportFreezeCommand,
            out s_layerViewportFreezeButton));
        row.Items.Add(new AdWin.RibbonRowBreak());
        row.Items.Add(CreateIconButton(
            "锁定",
            "切换当前图层锁定/解锁",
            CurrentLayerStatusKind.Lock,
            ToggleLayerLockCommand,
            out s_layerLockButton));
        row.Items.Add(CreateIconButton(
            "颜色",
            "设置当前图层颜色",
            CurrentLayerStatusKind.Color,
            SetLayerColorCommand,
            out s_layerColorButton));
        row.Items.Add(CreateIconButton(
            "视口颜色",
            "设置当前图层在当前视口的颜色",
            CurrentLayerStatusKind.Color,
            SetViewportLayerColorCommand,
            out s_layerViewportColorButton));

        source.Items.Add(row);
        return new AdWin.RibbonPanel { Source = source };
    }

    private static AdWin.RibbonPanel BuildStylePanel()
    {
        var source = new AdWin.RibbonPanelSource
        {
            Id = RibbonStylePanelId,
            Title = string.Empty
        };

        var row = CreateLeftAlignedRowPanel();
        AddCaptionValueRow(row, "标注", StyleCaptionColumnWidth, out s_propertyDimStyleValueLabel, "—", 160);
        row.Items.Add(new AdWin.RibbonRowBreak());
        AddCaptionValueRow(row, "文字", StyleCaptionColumnWidth, out s_propertyTextStyleValueLabel, "—", 160);
        row.Items.Add(new AdWin.RibbonRowBreak());
        AddCaptionValueRow(row, "引线", StyleCaptionColumnWidth, out s_propertyMLeaderStyleValueLabel, "—", 160);

        source.Items.Add(row);
        return new AdWin.RibbonPanel { Source = source };
    }

    private static AdWin.RibbonRowPanel CreateLeftAlignedRowPanel()
    {
        return new AdWin.RibbonRowPanel
        {
            GroupLocation = Autodesk.Private.Windows.RibbonItemGroupLocation.First,
            IsTopJustified = true,
            AreItemsArrangedFromRightToLeft = false,
            Width = double.NaN,
            MinWidth = 0
        };
    }

    private static void AddCaptionValueRow(
        AdWin.RibbonRowPanel row,
        string caption,
        double captionColumnWidth,
        out AdWin.RibbonLabel valueLabel,
        string valueText,
        double valueWidth)
    {
        row.Items.Add(CreateCaptionLabel(caption, captionColumnWidth));
        row.Items.Add(CreateValueLabel(out valueLabel, valueText, valueWidth));
    }

    private static AdWin.RibbonLabel CreateCaptionLabel(string text, double width)
    {
        return new AdWin.RibbonLabel
        {
            Text = text,
            ShowText = true,
            ToolTip = text,
            Width = width,
            MinWidth = width
        };
    }

    private static AdWin.RibbonLabel CreateValueLabel(out AdWin.RibbonLabel label, string text, double width)
    {
        label = new AdWin.RibbonLabel
        {
            Text = text,
            ShowText = true,
            ToolTip = text,
            MinWidth = width
        };
        return label;
    }

    private static AdWin.RibbonButton CreateIconButton(
        string text,
        string toolTip,
        CurrentLayerStatusKind iconKind,
        ICommand command,
        out AdWin.RibbonButton button)
    {
        var icon = CurrentLayerStatusIconCatalog.TryGet(iconKind);
        button = new AdWin.RibbonButton
        {
            Text = text,
            ToolTip = toolTip,
            ShowText = true,
            ShowImage = true,
            Image = icon,
            LargeImage = icon,
            CommandHandler = command,
            Size = AdWin.RibbonItemSize.Standard,
            ResizeStyle = AdWin.RibbonItemResizeStyles.NoResize,
            MinWidth = 74
        };
        return button;
    }

    private static void UpdateLabel(AdWin.RibbonLabel? label, string text, string toolTip)
    {
        if (label == null)
            return;

        label.Text = text;
        label.ToolTip = toolTip;
    }

    private static void UpdatePanels(CurrentLayerSnapshot? snapshot, string? unavailableReason = null)
    {
        if (snapshot == null)
        {
            var tip = string.IsNullOrWhiteSpace(unavailableReason) ? "当前没有可用的对象特性信息。" : unavailableReason!.Trim();
            UpdateLabel(s_propertyColorValueLabel, "—", tip);
            UpdateLabel(s_propertyDimStyleValueLabel, "—", tip);
            UpdateLabel(s_propertyTextStyleValueLabel, "—", tip);
            UpdateLabel(s_propertyMLeaderStyleValueLabel, "—", tip);
            return;
        }

        UpdateLabel(s_propertyColorValueLabel, snapshot.Color, snapshot.Color);
        UpdateLabel(s_propertyDimStyleValueLabel, snapshot.DimStyle, snapshot.DimStyle);
        UpdateLabel(s_propertyTextStyleValueLabel, snapshot.TextStyle, snapshot.TextStyle);
        UpdateLabel(s_propertyMLeaderStyleValueLabel, snapshot.MLeaderStyle, snapshot.MLeaderStyle);
    }

    private static void UpdateLayerActionButtons(CurrentLayerSnapshot? snapshot)
    {
        var enabled = snapshot != null;
        SetButtonState(
            s_layerOnButton,
            enabled,
            enabled && snapshot?.IsLayerOn == true,
            snapshot?.IsLayerOn == true ? CurrentLayerStatusKind.Power : CurrentLayerStatusKind.PowerOff,
            enabled ? "切换当前图层开/关" : "当前没有可用图层");
        SetButtonState(
            s_layerFreezeButton,
            enabled,
            enabled && snapshot?.IsFrozen == false,
            snapshot?.IsFrozen == true ? CurrentLayerStatusKind.Frozen : CurrentLayerStatusKind.Freeze,
            enabled ? "切换当前图层冻结/解冻" : "当前没有可用图层");
        SetButtonState(
            s_layerViewportFreezeButton,
            enabled,
            enabled && snapshot?.IsViewportFrozen == false,
            snapshot?.IsViewportFrozen == true ? CurrentLayerStatusKind.ViewportFrozen : CurrentLayerStatusKind.ViewportFreeze,
            enabled ? "切换当前图层在当前视口冻结/解冻" : "当前没有可用视口");
        SetButtonState(
            s_layerLockButton,
            enabled,
            enabled && snapshot?.IsLocked == true,
            snapshot?.IsLocked == true ? CurrentLayerStatusKind.Lock : CurrentLayerStatusKind.Unlock,
            enabled ? "切换当前图层锁定/解锁" : "当前没有可用图层");
        SetColorButtonState(s_layerColorButton, enabled, snapshot?.LayerColor, "设置当前图层颜色");
        SetColorButtonState(s_layerViewportColorButton, enabled, snapshot?.ViewportColor, "设置当前图层在当前视口的颜色");
    }

    private static void SetButtonState(
        AdWin.RibbonButton? button,
        bool isEnabled,
        bool isChecked,
        CurrentLayerStatusKind iconKind,
        string toolTip)
    {
        if (button == null)
            return;

        var icon = CurrentLayerStatusIconCatalog.TryGet(iconKind);
        button.IsEnabled = isEnabled;
        button.IsCheckable = true;
        button.IsChecked = isChecked;
        button.Image = icon;
        button.LargeImage = icon;
        button.ToolTip = toolTip;
    }

    private static void SetColorButtonState(AdWin.RibbonButton? button, bool isEnabled, CadColor? color, string toolTip)
    {
        if (button == null)
            return;

        var image = color == null
            ? CurrentLayerStatusIconCatalog.TryGet(CurrentLayerStatusKind.Color)
            : CreateColorSwatchImage(color);
        button.IsEnabled = isEnabled;
        button.IsCheckable = false;
        button.IsChecked = false;
        button.Image = image;
        button.LargeImage = image;
        button.ToolTip = toolTip;
    }

    private static ImageSource CreateColorSwatchImage(CadColor color)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var brush = new SolidColorBrush(ToMediaColor(color));
            brush.Freeze();
            var borderPen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238)), 1);
            borderPen.Freeze();
            context.DrawRectangle(brush, borderPen, new System.Windows.Rect(2, 2, 14, 14));
        }

        drawing.Freeze();
        return new DrawingImage(drawing);
    }

    private static System.Windows.Media.Color ToMediaColor(CadColor color)
    {
        try
        {
            if (color.IsByColor || color.ColorMethod == ColorMethod.ByColor)
                return System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);

            var index = color.ColorIndex;
            if (index <= 0 || index > 255)
                index = 7;

            var drawingColor = CadColor.GetColorValue(index, System.Drawing.Color.Black);
            return System.Windows.Media.Color.FromRgb(drawingColor.R, drawingColor.G, drawingColor.B);
        }
        catch
        {
            return System.Windows.Media.Color.FromRgb(255, 255, 255);
        }
    }

    private static void ResetReferences()
    {
        s_tab = null;
        s_propertyColorValueLabel = null;
        s_propertyDimStyleValueLabel = null;
        s_propertyTextStyleValueLabel = null;
        s_propertyMLeaderStyleValueLabel = null;
        s_layerValueLabel = null;
        s_shortcutValueLabel = null;
        s_documentValueLabel = null;
        s_layerOnButton = null;
        s_layerFreezeButton = null;
        s_layerViewportFreezeButton = null;
        s_layerLockButton = null;
        s_layerColorButton = null;
        s_layerViewportColorButton = null;
    }

    private static List<CurrentLayerShortcutCandidate> LoadShortcutCandidates()
    {
        return LayerShortcutStore.Load()
            .Where(x => !string.IsNullOrWhiteSpace(x.LayerName))
            .Select(x => new CurrentLayerShortcutCandidate
            {
                LayerName = x.LayerName.Trim(),
                AliasCell = x.Alias
            })
            .ToList();
    }

    private static void ToggleCurrentLayer(LayerToggleAction action)
    {
        ExecuteLayerAction(
            action switch
            {
                LayerToggleAction.On => "切换当前图层开关",
                LayerToggleAction.Freeze => "切换当前图层冻结",
                LayerToggleAction.ViewportFreeze => "切换当前视口冻结",
                LayerToggleAction.Lock => "切换当前图层锁定",
                _ => "切换当前图层状态"
            },
            doc =>
            {
                CadDatabaseScope.Write(
                    doc,
                    (db, tr) =>
                    {
                        var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, db.Clayer, OpenMode.ForWrite);
                        switch (action)
                        {
                            case LayerToggleAction.On:
                                layer.IsOff = !layer.IsOff;
                                break;
                            case LayerToggleAction.Freeze:
                                layer.IsFrozen = !layer.IsFrozen;
                                break;
                            case LayerToggleAction.ViewportFreeze:
                                ToggleCurrentViewportFreeze(doc, tr, db.Clayer);
                                break;
                            case LayerToggleAction.Lock:
                                layer.IsLocked = !layer.IsLocked;
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(action), action, null);
                        }
                    },
                    requireDocumentLock: true);

                doc.Editor.Regen();
            });
    }

    private static void ToggleCurrentViewportFreeze(Document doc, Transaction tr, ObjectId layerId)
    {
        if (!CurrentLayerSnapshotService.TryOpenCurrentViewport(doc, tr, OpenMode.ForWrite, out var viewport) ||
            viewport == null)
        {
            throw new InvalidOperationException("当前没有可用视口。");
        }

        var layerIds = new ObjectIdCollection { layerId };
        if (viewport.IsLayerFrozenInViewport(layerId))
            viewport.ThawLayersInViewport(layerIds.GetEnumerator());
        else
            viewport.FreezeLayersInViewport(layerIds.GetEnumerator());
    }

    private static void SetCurrentLayerColor(bool useViewportOverride)
    {
        ExecuteLayerAction(
            useViewportOverride ? "设置当前视口图层颜色" : "设置当前图层颜色",
            doc =>
            {
                var initialColor = ReadCurrentLayerColor(doc, useViewportOverride);
                var dialog = new ColorDialog
                {
                    IncludeByBlockByLayer = !useViewportOverride,
                    Color = initialColor
                };
                dialog.SetDialogTabs(ColorDialog.ColorTabs.ACITab | ColorDialog.ColorTabs.TrueColorTab | ColorDialog.ColorTabs.ColorBookTab);

                if (dialog.ShowModal() != true)
                    return;

                var selectedColor = dialog.Color;
                CadDatabaseScope.Write(
                    doc,
                    (db, tr) =>
                    {
                        var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, db.Clayer, OpenMode.ForWrite);
                        if (!useViewportOverride)
                        {
                            layer.Color = selectedColor;
                            return;
                        }

                        if (!CurrentLayerSnapshotService.TryOpenCurrentViewport(doc, tr, OpenMode.ForRead, out var viewport) ||
                            viewport == null)
                        {
                            throw new InvalidOperationException("当前没有可用视口。");
                        }

                        var overrides = layer.GetViewportOverrides(viewport.ObjectId);
                        overrides.Color = selectedColor;
                        overrides.IsColorOverridden = true;
                    },
                    requireDocumentLock: true);

                doc.Editor.Regen();
            });
    }

    private static CadColor ReadCurrentLayerColor(Document doc, bool useViewportOverride)
    {
        return CadDatabaseScope.Read(
            doc,
            (db, tr) =>
            {
                var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, db.Clayer, OpenMode.ForRead);
                if (!useViewportOverride)
                    return layer.Color;

                if (CurrentLayerSnapshotService.TryOpenCurrentViewport(doc, tr, OpenMode.ForRead, out var viewport) &&
                    viewport != null &&
                    layer.HasViewportOverrides(viewport.ObjectId))
                {
                    var overrides = layer.GetViewportOverrides(viewport.ObjectId);
                    if (overrides.IsColorOverridden)
                        return overrides.Color;
                }

                return layer.Color;
            },
            requireDocumentLock: true);
    }

    private static void ExecuteLayerAction(string operationName, Action<Document> action)
    {
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                        return;

                    try
                    {
                        action(doc);
                        RequestRefresh();
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal($"Ribbon：{operationName}失败", ex);
                        CadCommandContext.TryWriteMessage(doc, $"\nC_TOOL：{operationName}失败：{ex.Message}", $"Ribbon：写入{operationName}错误消息失败");
                        RequestRefresh();
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"Ribbon：调度{operationName}失败", ex);
        }
    }

    private static bool ShouldRefreshForSystemVariable(string name)
    {
        return string.Equals(name, SystemVariableNames.Clayer, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, SystemVariableNames.WsCurrent, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "CECOLOR", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "CVPORT", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "DIMSTYLE", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "TEXTSTYLE", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, MLeaderStyleHelper.SysVarCurrentMLeaderStyle, StringComparison.OrdinalIgnoreCase);
    }
}
