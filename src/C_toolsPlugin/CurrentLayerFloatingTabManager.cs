using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using CadColor = Autodesk.AutoCAD.Colors.Color;

namespace C_toolsPlugin;

internal static class CurrentLayerFloatingTabManager
{
    private const string LegacyRibbonTabId = "C_toolsPlugin.Ribbon.CurrentLayer.Tab";
    private const int RefreshDebounceMilliseconds = 250;
    private static readonly Guid PaletteGuid = new("5F968F67-5A8B-45E5-8E1C-48EF45E874B7");

    private static readonly object SyncRoot = new();
    private static readonly HashSet<Document> SelectionHookedDocuments = new();

    private static bool s_initialized;
    private static int s_refreshVersion;
    private static Dispatcher? s_uiDispatcher;
    private static DispatcherTimer? s_refreshDebounceTimer;
    private static PaletteSet? s_palette;
    private static CurrentLayerPaletteControl? s_control;

    internal static void Initialize()
    {
        if (s_initialized)
            return;

        s_initialized = true;
        s_uiDispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        TryRemoveLegacyRibbonTab();
        RegisterSelectionHooks(AcAp.DocumentManager.MdiActiveDocument);

        AcAp.Idle += OnFirstIdle;
        AcAp.DocumentManager.DocumentCreated += OnDocumentChanged;
        AcAp.DocumentManager.DocumentActivated += OnDocumentChanged;
        AcAp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
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
        AcAp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        AcAp.SystemVariableChanged -= OnSystemVariableChanged;
        UnregisterAllSelectionHooks();
        StopPendingRefresh();
        TryRemoveLegacyRibbonTab();
        ClosePalette();
    }

    internal static void RequestRefresh()
    {
        if (!s_initialized)
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
                        C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：读取当前图层失败（无效操作）", ex);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：读取当前图层失败", ex);
                    }

                    PostWindowUpdate(refreshVersion, snapshot, error);
                },
                null);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：调度当前图层刷新失败（无效操作）", ex);
            PostWindowUpdate(refreshVersion, null, ex.Message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：调度当前图层刷新失败", ex);
            PostWindowUpdate(refreshVersion, null, ex.Message);
        }
    }

    internal static void ToggleCurrentLayer(LayerToggleAction action)
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
                        var layerId = CurrentLayerSnapshotService.ResolveCurrentContextLayerId(doc, tr, db);
                        var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForWrite);
                        switch (action)
                        {
                            case LayerToggleAction.On:
                                layer.IsOff = !layer.IsOff;
                                break;
                            case LayerToggleAction.Freeze:
                                layer.IsFrozen = !layer.IsFrozen;
                                break;
                            case LayerToggleAction.ViewportFreeze:
                                ToggleCurrentViewportFreeze(doc, tr, layerId);
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

    internal static void SetCurrentLayerColor(bool useViewportOverride)
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
                        var layerId = CurrentLayerSnapshotService.ResolveCurrentContextLayerId(doc, tr, db);
                        var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForWrite);
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

    internal static void ChangeLayer(string layerName)
    {
        var targetLayerName = (layerName ?? "").Trim();
        if (targetLayerName.Length == 0)
            return;

        ExecuteLayerAction(
            "切换图层",
            doc =>
            {
                CadDatabaseScope.Write(
                    doc,
                    (db, tr) =>
                    {
                        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
                        if (!TryResolveLayerId(tr, layerTable, targetLayerName, out var targetLayerId))
                            throw new InvalidOperationException($"未找到图层“{targetLayerName}”。");

                        if (CurrentLayerSnapshotService.TryGetImpliedSelectionObjectIds(doc, out var objectIds))
                        {
                            ApplyLayerToSelectedEntities(tr, objectIds, targetLayerId);
                            return;
                        }

                        db.Clayer = targetLayerId;
                    },
                    requireDocumentLock: true);

                doc.Editor.Regen();
            });
    }

    internal static void ToggleLayerState(string layerName, LayerTableToggleAction action)
    {
        var targetLayerName = (layerName ?? "").Trim();
        if (targetLayerName.Length == 0)
            return;

        ExecuteLayerAction(
            action switch
            {
                LayerTableToggleAction.On => "切换图层开关",
                LayerTableToggleAction.Freeze => "切换图层冻结",
                LayerTableToggleAction.NewViewportFreeze => "切换图层新视口冻结",
                LayerTableToggleAction.Lock => "切换图层锁定",
                _ => "切换图层状态"
            },
            doc =>
            {
                CadDatabaseScope.Write(
                    doc,
                    (db, tr) =>
                    {
                        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
                        if (!TryResolveLayerId(tr, layerTable, targetLayerName, out var targetLayerId))
                            throw new InvalidOperationException($"未找到图层“{targetLayerName}”。");

                        var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, targetLayerId, OpenMode.ForWrite);
                        switch (action)
                        {
                            case LayerTableToggleAction.On:
                                layer.IsOff = !layer.IsOff;
                                break;
                            case LayerTableToggleAction.Freeze:
                                layer.IsFrozen = !layer.IsFrozen;
                                break;
                            case LayerTableToggleAction.NewViewportFreeze:
                                layer.ViewportVisibilityDefault = !layer.ViewportVisibilityDefault;
                                break;
                            case LayerTableToggleAction.Lock:
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

    private static void RequestRefreshDebounced()
    {
        if (!s_initialized)
            return;

        var dispatcher = ResolveDispatcher();
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RequestRefreshDebounced));
            return;
        }

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

    private static void RegisterSelectionHooks(Document? doc)
    {
        if (doc == null)
            return;

        lock (SyncRoot)
        {
            if (!SelectionHookedDocuments.Add(doc))
                return;
        }

        try
        {
            doc.ImpliedSelectionChanged += OnImpliedSelectionChanged;
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
                SelectionHookedDocuments.Remove(doc);
            C_toolsDiagnostics.LogNonFatal("当前图层可停靠面板：监听选择变化失败", ex);
        }
    }

    private static void UnregisterSelectionHooks(Document? doc)
    {
        if (doc == null)
            return;

        lock (SyncRoot)
        {
            if (!SelectionHookedDocuments.Remove(doc))
                return;
        }

        try
        {
            doc.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层可停靠面板：取消监听选择变化失败", ex);
        }
    }

    private static void UnregisterAllSelectionHooks()
    {
        List<Document> docs;
        lock (SyncRoot)
        {
            docs = SelectionHookedDocuments.ToList();
            SelectionHookedDocuments.Clear();
        }

        foreach (var doc in docs)
        {
            try
            {
                doc.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("当前图层可停靠面板：取消监听选择变化失败", ex);
            }
        }
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
        AcAp.Idle -= OnFirstIdle;
        TryRemoveLegacyRibbonTab();
        RegisterSelectionHooks(AcAp.DocumentManager.MdiActiveDocument);
        RequestRefresh();
    }

    private static void OnDocumentChanged(object? sender, DocumentCollectionEventArgs e)
    {
        RegisterSelectionHooks(e.Document);
        RequestRefreshDebounced();
    }

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        UnregisterSelectionHooks(e.Document);
    }

    private static void OnImpliedSelectionChanged(object? sender, EventArgs e) => RequestRefreshDebounced();

    private static void OnSystemVariableChanged(object? sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
    {
        try
        {
            var name = e?.Name?.Trim();
            if (name == null || name.Length == 0)
                return;

            if (string.Equals(name, SystemVariableNames.WsCurrent, StringComparison.OrdinalIgnoreCase))
                TryRemoveLegacyRibbonTab();

            if (!ShouldRefreshForSystemVariable(name))
                return;

            RequestRefreshDebounced();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：SystemVariableChanged 处理失败", ex);
        }
    }

    private static void PostWindowUpdate(int refreshVersion, CurrentLayerSnapshot? snapshot, string? error)
    {
        var dispatcher = ResolveDispatcher();
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => PostWindowUpdate(refreshVersion, snapshot, error)));
            return;
        }

        if (!s_initialized || refreshVersion != Volatile.Read(ref s_refreshVersion))
            return;

        var control = EnsurePaletteVisible();
        control.ApplySnapshot(snapshot, error);
    }

    private static CurrentLayerPaletteControl EnsurePaletteVisible()
    {
        lock (SyncRoot)
        {
            if (s_palette == null || s_control == null)
                CreatePalette();

            s_palette!.Visible = true;
            return s_control!;
        }
    }

    private static void CreatePalette()
    {
        s_control = new CurrentLayerPaletteControl();
        s_palette = new PaletteSet("C_TOOL 当前图层", PaletteGuid)
        {
            Style = PaletteSetStyles.ShowAutoHideButton
                    | PaletteSetStyles.ShowCloseButton
                    | PaletteSetStyles.ShowPropertiesMenu,
            DockEnabled = DockSides.Left | DockSides.Right,
            MinimumSize = new Size(300, 260),
            Size = new Size(390, 430)
        };
        s_palette.AddVisual("当前图层", s_control);
    }

    private static void ClosePalette()
    {
        var dispatcher = ResolveDispatcher();
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ClosePalette));
            return;
        }

        lock (SyncRoot)
        {
            if (s_palette == null)
                return;

            try
            {
                s_palette.Visible = false;
                if (s_palette is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("关闭当前图层可停靠面板失败（无效操作）", ex);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("关闭当前图层可停靠面板失败", ex);
            }
            finally
            {
                s_palette = null;
                s_control = null;
            }
        }
    }

    private static Dispatcher ResolveDispatcher()
    {
        return s_uiDispatcher ??= System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    private static void TryRemoveLegacyRibbonTab()
    {
        try
        {
            var componentManagerType = Type.GetType("Autodesk.Windows.ComponentManager, AdWindows", throwOnError: false);
            var ribbon = componentManagerType?
                .GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            var tabsObject = ribbon?
                .GetType()
                .GetProperty("Tabs", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(ribbon);

            if (tabsObject is not IEnumerable tabs)
                return;

            var tabsToRemove = new List<object>();
            foreach (var tab in tabs)
            {
                var id = tab?
                    .GetType()
                    .GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(tab) as string;
                if (tab != null && string.Equals(id, LegacyRibbonTabId, StringComparison.Ordinal))
                    tabsToRemove.Add(tab);
            }

            foreach (var tab in tabsToRemove)
                RemoveLegacyRibbonTab(tabsObject, tab);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("移除旧 C_TOOL Ribbon 页失败", ex);
        }
    }

    private static void RemoveLegacyRibbonTab(object tabsObject, object tab)
    {
        if (tabsObject is IList list)
        {
            list.Remove(tab);
            return;
        }

        tabsObject
            .GetType()
            .GetMethod("Remove", BindingFlags.Public | BindingFlags.Instance, null, new[] { tab.GetType() }, null)?
            .Invoke(tabsObject, new[] { tab });
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

    private static bool TryResolveLayerId(
        Transaction tr,
        LayerTable layerTable,
        string layerName,
        out ObjectId layerId)
    {
        layerId = ObjectId.Null;

        if (layerTable.Has(layerName))
        {
            layerId = layerTable[layerName];
            return !layerId.IsNull && layerId.IsValid && !layerId.IsErased;
        }

        foreach (ObjectId candidateId in layerTable)
        {
            if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, candidateId, OpenMode.ForRead, out var record) ||
                record == null)
            {
                continue;
            }

            if (!string.Equals((record.Name ?? "").Trim(), layerName, StringComparison.OrdinalIgnoreCase))
                continue;

            layerId = candidateId;
            return !layerId.IsNull && layerId.IsValid && !layerId.IsErased;
        }

        return false;
    }

    private static void ApplyLayerToSelectedEntities(
        Transaction tr,
        IEnumerable<ObjectId> objectIds,
        ObjectId targetLayerId)
    {
        var visited = new HashSet<ObjectId>();
        foreach (var objectId in objectIds)
        {
            if (objectId.IsNull || !objectId.IsValid || objectId.IsErased || !visited.Add(objectId))
                continue;

            try
            {
                if (CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForWrite, out var entity) &&
                    entity != null &&
                    entity.LayerId != targetLayerId)
                {
                    entity.LayerId = targetLayerId;
                }
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：修改所选对象图层失败（无效操作）", ex);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("当前图层悬浮选项卡：修改所选对象图层失败（CAD）", ex);
            }
        }
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

    private static CadColor ReadCurrentLayerColor(Document doc, bool useViewportOverride)
    {
        return CadDatabaseScope.Read(
            doc,
            (db, tr) =>
            {
                var layerId = CurrentLayerSnapshotService.ResolveCurrentContextLayerId(doc, tr, db);
                var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead);
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
                        C_toolsDiagnostics.LogNonFatal($"当前图层悬浮选项卡：{operationName}失败", ex);
                        CadCommandContext.TryWriteMessage(
                            doc,
                            $"\nC_TOOL：{operationName}失败：{ex.Message}",
                            $"当前图层悬浮选项卡：写入{operationName}错误消息失败");
                        RequestRefresh();
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"当前图层悬浮选项卡：调度{operationName}失败", ex);
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
