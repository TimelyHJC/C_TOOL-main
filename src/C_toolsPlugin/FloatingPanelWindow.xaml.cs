using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsShared;

namespace C_toolsPlugin;

public partial class FloatingPanelWindow : Window, IModelessWindowPlacement, IModelessWindowHostAware
{
    public string PlacementKey => "C_TOOL_FloatingPanel";

    private enum StatusTone
    {
        Neutral,
        Success,
        Error
    }

    private readonly ResettableObservableCollection<CommandCatalogRow> _allRows = new();
    private readonly ListCollectionView _viewLayer;
    private readonly ListCollectionView _viewCadNative;
    private readonly ListCollectionView _viewVCommand;
    private readonly ListCollectionView _viewPluginCommand;

    private string _filterText = "";
    private bool _nonLayerDataGridsBound;
    private readonly DispatcherTimer _filterDebounceTimer;
    /// <summary>递增后仅最新一次 <see cref="RefreshCommandCatalog"/> 结果会写回 UI，避免连点「刷新」或重叠任务乱序。</summary>
    private int _catalogRefreshId;
    private bool _hasPendingLayerRowDeletion;
    private readonly Brush _statusNeutralBrush;
    private readonly Brush _statusSuccessBrush;
    private readonly Brush _statusErrorBrush;

    public FloatingPanelWindow()
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);

        if (MainTabs != null && MainTabs.SelectedIndex < 0)
            MainTabs.SelectedIndex = 0;

        _statusNeutralBrush = TryFindResource("Cad.Muted") as Brush ?? Brushes.Gray;
        _statusSuccessBrush = TryFindResource("Cad.StatusSuccess") as Brush ?? Brushes.LightSkyBlue;
        _statusErrorBrush = TryFindResource("Cad.StatusError") as Brush ?? Brushes.OrangeRed;

        _filterDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterDebounceTimer.Tick += (_, _) =>
        {
            _filterDebounceTimer.Stop();
            _filterText = TxtCatalogFilter.Text.Trim();
            RefreshAllViews();
        };
        Closed += (_, _) => _filterDebounceTimer.Stop();

        _viewLayer = new ListCollectionView(_allRows) { Filter = FilterLayer };
        _viewCadNative = new ListCollectionView(_allRows) { Filter = FilterCadNative };
        _viewVCommand = new ListCollectionView(_allRows) { Filter = FilterVCommand };
        _viewPluginCommand = new ListCollectionView(_allRows) { Filter = FilterPluginCommand };

        GridLayer.ItemsSource = _viewLayer;
        UpdateCatalogFilterPlaceholder();
        UpdateSecondaryActionsVisibility();
        UpdateSaveTargetDisplay();

        var sortByFilter = new CatalogRowFilterComparer(this);
        _viewLayer.CustomSort = sortByFilter;
        _viewCadNative.CustomSort = sortByFilter;
        _viewVCommand.CustomSort = sortByFilter;
        _viewPluginCommand.CustomSort = sortByFilter;

        Loaded += OnLoaded;
        SetStatus("正在载入命令表...");
    }

    /// <summary>首屏只挂「图层命令」网格；切换至其它 Tab 时再绑定，减少首帧 WPF 开销。</summary>
    private void EnsureNonLayerDataGridsBound()
    {
        if (_nonLayerDataGridsBound)
            return;
        _nonLayerDataGridsBound = true;
        GridCadNative.ItemsSource = _viewCadNative;
        GridVCommand.ItemsSource = _viewVCommand;
        GridPluginCommand.ItemsSource = _viewPluginCommand;
        RefreshNonLayerViews();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex > 0)
            EnsureNonLayerDataGridsBound();
        UpdateSecondaryActionsVisibility();
        UpdateSaveTargetDisplay();
    }

    private void PickHatchStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;
        if (btn.DataContext is not CommandCatalogRow row || !IsLayerShortcutRow(row))
            return;

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            MessageBox.Show(this, "无活动图纸，无法在图纸中选择填充。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HatchStylePickSession.Begin(row, Dispatcher, this);
        HideForHatchStylePick();
        doc.SendStringToExecute("._" + PluginCommandIds.PickHatchStyle + "\n", true, false, false);
    }

    private void LayerDimensionCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CommandCatalogRow row } && IsLayerShortcutRow(row))
            row.IsUserModified = true;
    }

    internal void EnsureDefaultView()
    {
        Title = "C_TOOL 快捷键配置";
        UpdateSecondaryActionsVisibility();
        UpdateSaveTargetDisplay();
        // 避免与 Show 同帧抢焦点拖慢首帧；激活延后一帧即可
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Activate();
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("浮层 Activate", ex);
            }
        }, DispatcherPriority.Background);
    }

    public void OnHostShowing()
    {
        EnsureDefaultView();
    }

    public void OnHostHiding()
    {
    }

    private void UpdateSecondaryActionsVisibility()
    {
        var isLayerTab = string.Equals(GetCurrentTabHeader(), "图层命令", StringComparison.Ordinal);
        var layerVisibility = isLayerTab ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (BtnLoadCurrentDrawingLayers != null)
            BtnLoadCurrentDrawingLayers.Visibility = layerVisibility;
        if (BtnImportLayerXlsx != null)
            BtnImportLayerXlsx.Visibility = layerVisibility;
        if (BtnAddLayerShortcut != null)
            BtnAddLayerShortcut.Visibility = layerVisibility;
        if (BtnResetLayerShortcuts != null)
            BtnResetLayerShortcuts.Visibility = layerVisibility;
    }

    private void UpdateSaveTargetDisplay()
    {
        if (SaveTargetText == null)
            return;

        var (text, toolTip) = BuildCurrentTabSaveTargetText();
        SaveTargetText.Text = text;
        SaveTargetText.ToolTip = toolTip;
    }

    private (string Text, string ToolTip) BuildCurrentTabSaveTargetText()
    {
        var header = GetCurrentTabHeader();

        static string NameOf(string path) => Path.GetFileName(path);

        var layerLispPath = Path.Combine(C_toolsPaths.LayerShortcutsDataFolder, LayerLispShortcuts.FileName);
        var cmdDescPath = CommandDescriptionStore.FilePath;

        return header switch
        {
            "图层命令" => (
                $"当前页保存：{NameOf(LayerShortcutStore.FilePath)} / {NameOf(layerLispPath)} / {NameOf(cmdDescPath)}",
                $"当前页保存文件：{Environment.NewLine}{LayerShortcutStore.FilePath}{Environment.NewLine}{layerLispPath}{Environment.NewLine}{cmdDescPath}"
            ),
            _ => (
                $"当前页保存：{NameOf(C_toolsPaths.UserAcadPgpPath)} / {NameOf(C_toolsPaths.UserSiblingC_toolsAliasesPgpPath)} / {NameOf(cmdDescPath)}",
                $"当前页保存文件：{Environment.NewLine}{C_toolsPaths.UserAcadPgpPath}{Environment.NewLine}{C_toolsPaths.UserSiblingC_toolsAliasesPgpPath}{Environment.NewLine}{cmdDescPath}"
            )
        };
    }

    private void SetStatus(string message, StatusTone tone = StatusTone.Neutral)
    {
        if (StatusText == null)
            return;

        StatusText.Text = message;
        StatusText.Foreground = tone switch
        {
            StatusTone.Success => _statusSuccessBrush,
            StatusTone.Error => _statusErrorBrush,
            _ => _statusNeutralBrush
        };
    }

    internal void HideForHatchStylePick() => Hide();

    internal void ShowAfterHatchStylePick()
    {
        Show();
        EnsureDefaultView();
    }

    private static bool IsLayerShortcutRow(CommandCatalogRow r) =>
        string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var owner = AcAp.MainWindow?.Handle ?? IntPtr.Zero;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(this) { Owner = owner };
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("设置浮层 Owner 为 AutoCAD 主窗口", ex);
        }

        WindowTitleBarHelper.TryApplyDarkTitleBar(this);

        Title = "C_TOOL 快捷键配置";
        // 先让窗口完成布局与首帧绘制，再开始读 PGP / 后台扫描，避免长时间白屏或卡顿感
        SetStatus("正在载入命令表...");
        Dispatcher.BeginInvoke(UpdateSecondaryActionsVisibility, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(RefreshCommandCatalog, DispatcherPriority.ApplicationIdle);
    }

    private bool RowMatchesFilter(CommandCatalogRow r)
    {
        if (_filterText.Length == 0)
            return true;
        return StringSearchCompat.ContainsOrdinalIgnoreCase(r.Alias, _filterText)
               || StringSearchCompat.ContainsOrdinalIgnoreCase(r.CommandName, _filterText)
               || StringSearchCompat.ContainsOrdinalIgnoreCase(r.Description, _filterText)
               || StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerName, _filterText)
               || StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerColor, _filterText)
               || StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerLinetype, _filterText)
               || StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerLineWeight, _filterText);
    }

    /// <summary>搜索排序：前缀匹配优先于仅包含子串；同档按命令名或图层名+别名排序。</summary>
    private int FilterMatchRank(CommandCatalogRow r)
    {
        if (_filterText.Length == 0)
            return 0;
        var f = _filterText;
        if (IsLayerShortcutRow(r))
        {
            if (r.Alias.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (r.LayerName.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.Alias, f))
                return 10;
            if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerName, f))
                return 11;
            if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.Description, f))
                return 12;
            if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerColor, f))
                return 13;
            if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerLinetype, f))
                return 14;
            if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.LayerLineWeight, f))
                return 15;
            return 99;
        }

        if (r.CommandName.StartsWith(f, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (r.Alias.StartsWith(f, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.CommandName, f))
            return 10;
        if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.Alias, f))
            return 11;
        if (StringSearchCompat.ContainsOrdinalIgnoreCase(r.Description, f))
            return 12;
        return 99;
    }

    private bool FilterLayer(object obj) =>
        obj is CommandCatalogRow r && IsLayerShortcutRow(r) && RowMatchesFilter(r);

    private bool FilterCadNative(object obj) =>
        obj is CommandCatalogRow r
        && string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagCadNative, StringComparison.Ordinal)
        && RowMatchesFilter(r);

    private bool FilterVCommand(object obj) =>
        obj is CommandCatalogRow r
        && string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagVCommand, StringComparison.Ordinal)
        && RowMatchesFilter(r);

    private bool FilterPluginCommand(object obj) =>
        obj is CommandCatalogRow r
        && string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagPluginCommand, StringComparison.Ordinal)
        && RowMatchesFilter(r);

    private void RefreshAllViews()
    {
        // 筛选/刷新视图时若单元格仍处于编辑模板，CollectionView.Refresh 可能触发未处理异常
        foreach (var dg in new[] { GridLayer, GridCadNative, GridVCommand, GridPluginCommand })
        {
            if (dg.ItemsSource == null)
                continue;
            try
            {
                dg.CommitEdit(DataGridEditingUnit.Cell, true);
                dg.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"RefreshAllViews：DataGrid 提交编辑失败（{dg.Name}）", ex);
                try
                {
                    dg.CancelEdit(DataGridEditingUnit.Row);
                }
                catch (Exception ex2)
                {
                    C_toolsDiagnostics.LogNonFatal($"RefreshAllViews：DataGrid CancelEdit 失败（{dg.Name}）", ex2);
                }
            }
        }

        _viewLayer.Refresh();
        if (_nonLayerDataGridsBound)
            RefreshNonLayerViews();
    }

    private void RefreshNonLayerViews()
    {
        _viewCadNative.Refresh();
        _viewVCommand.Refresh();
        _viewPluginCommand.Refresh();
    }

    private void TxtCatalogFilter_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCatalogFilterPlaceholder();
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void TxtCatalogFilter_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateCatalogFilterPlaceholder();

    private void UpdateCatalogFilterPlaceholder()
    {
        if (TxtCatalogFilterPlaceholder == null || TxtCatalogFilter == null)
            return;
        TxtCatalogFilterPlaceholder.Visibility =
            string.IsNullOrWhiteSpace(TxtCatalogFilter.Text) && !TxtCatalogFilter.IsKeyboardFocusWithin
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void RefreshCatalog_Click(object sender, RoutedEventArgs e)
    {
        CadCommandCatalogBuilder.InvalidateDotNetCommandScanCache();
        CommandCatalogSnapshotStore.Invalidate();
        RefreshCommandCatalog();
    }

    private string GetCurrentTabHeader()
    {
        return (MainTabs?.SelectedItem as TabItem)?.Header?.ToString()?.Trim() ?? "图层命令";
    }

    private void AddLayerShortcutRow_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingDataGridEdits();

        var dialog = new LayerShortcutAddWindow
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;

        var draft = dialog.Result;
        var row = new CommandCatalogRow(PluginCommandIds.LayerShortcutCatalogCommandLabel, "—", "手动", CadCommandCatalogBuilder.TagLayerShortcut)
        {
            Alias = draft.Alias,
            LayerName = draft.LayerName,
            LayerColor = draft.LayerColor,
            Description = draft.Description,
            LayerRunDimensionWhenNoSelection = draft.RunDimensionWhenNoSelection,
            IsUserModified = true
        };

        PrepareDataGridsBeforeRowCollectionMutation();
        _allRows.Add(row);
        RefreshAllViews();
        MainTabs.SelectedIndex = 0;
        GridLayer.SelectedItem = row;
        GridLayer.ScrollIntoView(row);
        SetStatus(
            string.IsNullOrWhiteSpace(row.Alias)
                ? $"已添加图层“{row.LayerName}”，请点「保存」写入 JSON。"
                : $"已添加图层快捷键 {row.Alias} -> {row.LayerName}，请点「保存」。",
            StatusTone.Success);
    }

    private void LoadCurrentDrawingLayers_Click(object sender, RoutedEventArgs e)
    {
        RunImportLayersFromActiveDrawing();
    }

    private void ImportLayerXlsx_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Title = "导入图层快捷键（xlsx）"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var (imported, msgs) = LayerShortcutXlsxImport.ImportFromPath(dlg.FileName);
            if (imported.Count == 0)
            {
                MessageBox.Show(this,
                    msgs.Count > 0 ? string.Join(Environment.NewLine, msgs) : "未导入任何行。",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                SetStatus("Excel 导入未增加行。");
                return;
            }

            CommitPendingDataGridEdits();
            PrepareDataGridsBeforeRowCollectionMutation();
            var added = MergeXlsxImportedRows(imported);
            var skippedDup = imported.Count - added;
            RefreshAllViews();
            MainTabs.SelectedIndex = 0;
            var note =
                $"已从 Excel 追加 {added} 条（文件内有效行 {imported.Count}，因与列表重复跳过 {skippedDup} 条）。请点「保存」。";
            if (msgs.Count > 0)
                note += " " + string.Join(" ", msgs);
            SetStatus(note, StatusTone.Success);
            if (msgs.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, msgs), Title, MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取 Excel 失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Excel 导入失败。", StatusTone.Error);
        }
    }

    private void ResetLayerShortcuts_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingDataGridEdits();

        var confirm = MessageBox.Show(
            this,
            $"将用 {LayerShortcutInitialData.FileName} 重置默认快捷键。\n\n会覆盖该文件中已填写的图层快捷键；如果文件中填写了命令别名，也会覆盖 C_TOOL 的 PGP 别名块。\n\n继续吗？",
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        var hasLayerFile = LayerShortcutInitialData.TryLoadEntries(out var entries, out var sourcePath, out var warnings);
        var hasCommandFile = LayerShortcutInitialData.TryLoadCommandAliases(
            out var commandAliases,
            out var commandSourcePath,
            out var commandWarnings);
        warnings.AddRange(commandWarnings);
        sourcePath ??= commandSourcePath;

        if (!hasLayerFile && !hasCommandFile)
        {
            MessageBox.Show(
                this,
                $"未找到可用的 {LayerShortcutInitialData.FileName}。\n\n请确认该文件位于：\n{LayerShortcutInitialData.PrimaryPath}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus($"未找到 {LayerShortcutInitialData.FileName}，未重置。", StatusTone.Error);
            return;
        }

        if (entries.Count == 0 && commandAliases.Count == 0)
        {
            var warningText = warnings.Count > 0 ? "\n\n" + string.Join("\n", warnings.Take(8)) : "";
            MessageBox.Show(
                this,
                $"{LayerShortcutInitialData.FileName} 中没有可用的快捷键行，未重置。{warningText}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus($"{LayerShortcutInitialData.FileName} 中没有可用快捷键，未重置。", StatusTone.Error);
            return;
        }

        try
        {
            var validEntries = entries
                .Where(x => CadPgpMerge.NormalizeAlias(x.Alias).Length > 0 && !string.IsNullOrWhiteSpace(x.LayerName))
                .ToList();
            var layerCount = 0;
            if (validEntries.Count > 0)
            {
                LayerShortcutStore.Save(entries);
                var emit = LayerLispShortcuts.EmitFromEntries(validEntries);
                layerCount = entries.Count;
                if (emit.Skipped.Count > 0)
                    warnings.AddRange(emit.Skipped.Select(x => $"LISP 未生成：{x}"));
                CurrentLayerFloatingTabManager.RequestRefresh();
                _hasPendingLayerRowDeletion = false;
                TryLoadLayerLispAfterReset();
            }

            var commandCount = ResetCommandAliasesFromInitialData(commandAliases, validEntries, warnings);
            if (commandCount > 0)
                TryReloadPgpAfterReset();

            RefreshCommandCatalog();

            var summary =
                $"已从 {Path.GetFileName(sourcePath ?? LayerShortcutInitialData.FileName)} 重置快捷键：图层 {layerCount} 条，命令别名 {commandCount} 条。";
            if (layerCount > 0)
                summary += $"\n已生成 {LayerLispShortcuts.FileName}。";
            if (warnings.Count > 0)
                summary += "\n\n注意：\n" + string.Join("\n", warnings.Take(8));

            SetStatus($"已重置快捷键：图层 {layerCount} 条，命令别名 {commandCount} 条。", StatusTone.Success);
            MessageBox.Show(this, summary, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"重置快捷键失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("重置快捷键失败。", StatusTone.Error);
            C_toolsDiagnostics.LogNonFatal("重置图层快捷键失败", ex);
        }
    }

    private void TryLoadLayerLispAfterReset()
    {
        AcAp.DocumentManager.ExecuteInApplicationContext(
            _ =>
            {
                try
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc != null)
                        LayerLispShortcuts.TryLoadInDocument(doc);
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("重置图层快捷键后热加载 LISP 失败", ex);
                }
            },
            null);
    }

    private static int ResetCommandAliasesFromInitialData(
        IReadOnlyList<PgpAliasDto> commandAliases,
        IReadOnlyList<LayerShortcutEntry> layerEntries,
        List<string> warnings)
    {
        if (commandAliases.Count == 0)
            return 0;

        var layerAliasKeys = new HashSet<string>(
            layerEntries.Select(x => CadPgpMerge.NormalizeAlias(x.Alias)).Where(x => x.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var filtered = new List<PgpAliasDto>();
        foreach (var dto in commandAliases)
        {
            var alias = CadPgpMerge.NormalizeAlias(dto.Alias);
            var target = CadPgpMerge.NormalizeTarget(dto.Target);
            if (alias.Length == 0 || target.Length == 0)
                continue;

            if (layerAliasKeys.Contains(alias))
            {
                warnings.Add($"命令别名 {alias} 与图层快捷键同名，已跳过命令侧。");
                continue;
            }

            filtered.Add(new PgpAliasDto { Alias = alias, Target = target });
        }

        if (filtered.Count == 0)
            return 0;

        var skipped = new List<string>();
        var block = CadPgpMerge.BuildAliasBlock(filtered, skipped);
        warnings.AddRange(skipped);

        var writtenCount = CadPgpMerge.ParsePgpAliasLinesFromText(block).Count;
        if (writtenCount == 0)
            return 0;

        var result = CadPgpMerge.ApplyToDiscoveredPgp(null, block);
        if (!result.Ok)
            throw new InvalidOperationException(result.Message);

        return writtenCount;
    }

    private void TryReloadPgpAfterReset()
    {
        AcAp.DocumentManager.ExecuteInApplicationContext(
            _ =>
            {
                try
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                        return;

                    var result = CadPgpReload.TryReloadPgp(doc);
                    if (!result.Ok)
                        C_toolsDiagnostics.LogNonFatal($"重置快捷键后重载 PGP 失败：{result.Message}", null);
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("重置快捷键后重载 PGP 失败", ex);
                }
            },
            null);
    }

    private static string LayerRowPairKey(CommandCatalogRow r) =>
        $"{CadPgpMerge.NormalizeAlias(r.Alias)}|{(r.LayerName ?? "").Trim()}";

    /// <summary>按「图层名称」去重：列表中已有同名图层则跳过。</summary>
    private (int Added, int Skipped) MergeDrawingImportedRows(IReadOnlyList<CommandCatalogRow> fromDrawing)
    {
        var existingNames = new HashSet<string>(
            _allRows.Where(IsLayerShortcutRow).Select(r => (r.LayerName ?? "").Trim()).Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;
        var rowsToAdd = new List<CommandCatalogRow>();
        foreach (var r in fromDrawing)
        {
            var ln = (r.LayerName ?? "").Trim();
            if (ln.Length == 0)
                continue;
            if (existingNames.Contains(ln))
            {
                skipped++;
                continue;
            }

            rowsToAdd.Add(r);
            existingNames.Add(ln);
            added++;
        }

        _allRows.AddRange(rowsToAdd);
        return (added, skipped);
    }

    /// <summary>按「快捷键 + 图层名」去重。</summary>
    private int MergeXlsxImportedRows(IReadOnlyList<CommandCatalogRow> imported)
    {
        var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _allRows.Where(IsLayerShortcutRow))
            pairs.Add(LayerRowPairKey(r));

        var added = 0;
        var rowsToAdd = new List<CommandCatalogRow>();
        foreach (var r in imported)
        {
            var pk = LayerRowPairKey(r);
            if (pairs.Contains(pk))
                continue;
            rowsToAdd.Add(r);
            pairs.Add(pk);
            added++;
        }

        _allRows.AddRange(rowsToAdd);
        return added;
    }

    private void RunImportLayersFromActiveDrawing()
    {
        AcAp.DocumentManager.ExecuteInApplicationContext(
            _ =>
            {
                try
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            MessageBox.Show(this, "无活动图形文档，无法读取图层。", Title, MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            SetStatus("无活动文档，未导入图层。", StatusTone.Error);
                        });
                        return;
                    }

                    var newRows = DrawingLayerImport.BuildLayerShortcutRowsFromCurrentDrawing(doc);
                    Dispatcher.BeginInvoke(() =>
                    {
                        var (added, skipped) = MergeDrawingImportedRows(newRows);
                        RefreshAllViews();
                        MainTabs.SelectedIndex = 0;
                        SetStatus(
                            $"已从当前图纸追加 {added} 条图层（跳过列表中已有同名图层 {skipped} 条）。请点「保存」写入 JSON 与 {LayerLispShortcuts.FileName}。",
                            StatusTone.Success);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show(this, $"读取图层失败：{ex.Message}", Title, MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        SetStatus("导入图层失败。", StatusTone.Error);
                    });
                }
            },
            null);
    }

    private void RefreshCommandCatalog()
    {
        UpdateSaveTargetDisplay();

        var refreshId = Interlocked.Increment(ref _catalogRefreshId);
        SetStatus("正在载入命令表...");
        AcAp.DocumentManager.ExecuteInApplicationContext(
            _ =>
            {
                var doc = AcAp.DocumentManager.MdiActiveDocument;
                List<PgpAliasDto> pgpAliases = new();
                var pgpNote = "";
                var pgpText = "";
                var swPgp = Stopwatch.StartNew();
                if (doc != null && CadPgpMerge.TryReadAcadPgp(doc, out string _, out pgpText))
                    pgpAliases = CadPgpMerge.ParsePgpAliasLines(pgpText);
                else if (doc == null)
                    pgpNote = "无活动文档，未读取 acad.pgp。";
                else
                    pgpNote = "未找到 acad.pgp。";
                swPgp.Stop();
                C_toolsDiagnostics.LogPerf("acad.pgp 读取与解析别名行", swPgp.ElapsedMilliseconds,
                    $"aliases={pgpAliases.Count}, pgpChars={pgpText.Length}");

                // PGP 需在应用上下文读；指纹、快照与反射扫描在线程池，避免卡死 WPF 首帧与拖拽
                _ = Task.Run(() =>
                {
                    string baseFingerprint;
                    try
                    {
                        baseFingerprint = CatalogLoadFingerprint.ComputeBase();
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("CatalogLoadFingerprint.ComputeBase", ex);
                        baseFingerprint = Guid.NewGuid().ToString();
                    }

                    var layerEntriesForCatalog = CatalogLayerMerge.LoadValidLayerShortcutEntries();
                    var layerQuickFirst = CatalogLayerMerge.BuildLayerShortcutRowsOnly(layerEntriesForCatalog);
                    if (layerQuickFirst.Count > 0)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (refreshId != Volatile.Read(ref _catalogRefreshId))
                                return;
                            if (!IsLoaded)
                                return;
                            try
                            {
                                ApplyCatalogRowsTimed(layerQuickFirst, "ApplyCatalogRows·图层首屏");
                                MainTabs.SelectedIndex = 0;
                                SetStatus("图层命令已显示，正在载入完整命令表…");
                            }
                            catch (Exception ex)
                            {
                                C_toolsDiagnostics.LogNonFatal("RefreshCommandCatalog 图层首屏", ex);
                            }
                        });
                    }

                    if (CommandCatalogSnapshotStore.TryLoad(baseFingerprint, out var cachedRows))
                    {
                        CadPgpMerge.FillAliasColumn(cachedRows, pgpText);
                        CommandAliasDefaults.ApplyMissingAliases(cachedRows);
                        CatalogLayerMerge.AppendLayerShortcutRows(cachedRows, layerEntriesForCatalog);
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (refreshId != Volatile.Read(ref _catalogRefreshId))
                                return;
                            if (!IsLoaded)
                                return;
                            try
                            {
                                ApplyCatalogRowsTimed(cachedRows, "ApplyCatalogRows·快照命中");
                                var n = cachedRows.Count;
                                SetStatus(
                                    string.IsNullOrEmpty(pgpNote)
                                        ? $"已从缓存载入 {n} 条"
                                        : $"{pgpNote} 已从缓存载入 {n} 条",
                                    StatusTone.Success);
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"载入列表失败：{ex.Message}", StatusTone.Error);
                                C_toolsDiagnostics.LogNonFatal("RefreshCommandCatalog 缓存 UI", ex);
                            }
                        });
                        return;
                    }

                    var totalCold = Stopwatch.StartNew();
                    List<CommandCatalogRow> rows;
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var dotNet = CadCommandCatalogBuilder.ScanDotNetCommandMethods();
                        sw.Stop();
                        C_toolsDiagnostics.LogPerf("dotNet CommandMethod 扫描", sw.ElapsedMilliseconds);

                        sw = Stopwatch.StartNew();
                        rows = CadCommandCatalogBuilder.MergeCatalog(pgpAliases, dotNet);
                        sw.Stop();
                        C_toolsDiagnostics.LogPerf("PGP 与目录合并", sw.ElapsedMilliseconds);

                        if (pgpText.Length > 0)
                        {
                            sw = Stopwatch.StartNew();
                            CadPgpMerge.FillAliasColumn(rows, pgpText);
                            sw.Stop();
                            C_toolsDiagnostics.LogPerf("FillAliasColumn", sw.ElapsedMilliseconds);
                        }

                        CommandAliasDefaults.ApplyMissingAliases(rows);

                        sw = Stopwatch.StartNew();
                        CatalogLayerMerge.AppendLayerShortcutRows(rows, layerEntriesForCatalog);
                        sw.Stop();
                        C_toolsDiagnostics.LogPerf("图层快捷键行", sw.ElapsedMilliseconds);

                        sw = Stopwatch.StartNew();
                        CommandDescriptionStore.ApplyToRows(rows);
                        sw.Stop();
                        C_toolsDiagnostics.LogPerf("command_descriptions", sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("RefreshCommandCatalog 命令扫描失败", ex);
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (refreshId != Volatile.Read(ref _catalogRefreshId))
                                return;
                            if (!IsLoaded)
                                return;
                            SetStatus($"{pgpNote} 命令扫描失败：{ex.Message}", StatusTone.Error);
                        });
                        return;
                    }

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (refreshId != Volatile.Read(ref _catalogRefreshId))
                            return;
                        if (!IsLoaded)
                            return;
                        try
                        {
                            ApplyCatalogRowsTimed(rows, "ApplyCatalogRows·冷路径首屏");
                            var n = rows.Count;
                            totalCold.Stop();
                            C_toolsDiagnostics.LogPerf("命令表冷路径(指纹未命中)", totalCold.ElapsedMilliseconds);
                            SetStatus(
                                string.IsNullOrEmpty(pgpNote)
                                    ? $"已载入 {n} 条"
                                    : $"{pgpNote} 已扫描 .NET 命令共 {n} 条",
                                StatusTone.Success);
                            CommandCatalogSnapshotStore.Save(baseFingerprint, rows);
                        }
                        catch (Exception ex)
                        {
                            SetStatus($"载入列表失败：{ex.Message}", StatusTone.Error);
                            C_toolsDiagnostics.LogNonFatal("RefreshCommandCatalog 首屏 UI", ex);
                        }
                    });
                });
            },
            null);
    }

    private void ApplyCatalogRowsTimed(IReadOnlyList<CommandCatalogRow> rows, string perfLabel)
    {
        var sw = Stopwatch.StartNew();
        ApplyCatalogRows(rows);
        sw.Stop();
        C_toolsDiagnostics.LogPerf(perfLabel, sw.ElapsedMilliseconds, $"n={rows.Count}");
    }

    private void ApplyCatalogRows(IReadOnlyList<CommandCatalogRow> rows)
    {
        CommitPendingDataGridEdits();
        var rowsToApply = MergeCatalogRowsWithPendingEdits(rows);
        PrepareDataGridsBeforeRowCollectionMutation();
        _allRows.ReplaceAll(rowsToApply);
        RefreshAllViews();
    }

    private IReadOnlyList<CommandCatalogRow> MergeCatalogRowsWithPendingEdits(IReadOnlyList<CommandCatalogRow> incoming)
    {
        var hasLayerChanges = _hasPendingLayerRowDeletion || _allRows.Any(r => IsLayerShortcutRow(r) && r.IsUserModified);
        var modifiedCommandRows = _allRows.Where(r => !IsLayerShortcutRow(r) && r.IsUserModified).ToList();
        if (!hasLayerChanges && modifiedCommandRows.Count == 0)
            return incoming;

        var merged = new List<CommandCatalogRow>(incoming.Count + modifiedCommandRows.Count);
        merged.AddRange(hasLayerChanges
            ? _allRows.Where(IsLayerShortcutRow)
            : incoming.Where(IsLayerShortcutRow));
        merged.AddRange(incoming.Where(r => !IsLayerShortcutRow(r)));

        foreach (var row in modifiedCommandRows)
        {
            var index = merged.FindIndex(x => CatalogCommandRowsMatch(x, row));
            if (index >= 0)
                merged[index] = row;
            else
                merged.Add(row);
        }

        return merged;
    }

    private static bool CatalogCommandRowsMatch(CommandCatalogRow left, CommandCatalogRow right)
    {
        if (IsLayerShortcutRow(left) || IsLayerShortcutRow(right))
            return false;
        if (!string.Equals(left.CategoryTag, right.CategoryTag, StringComparison.Ordinal))
            return false;
        return string.Equals(
            CadPgpMerge.NormalizeTarget(left.CommandName),
            CadPgpMerge.NormalizeTarget(right.CommandName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static List<PgpAliasDto> CollectAliasesFromGrid(
        IEnumerable<CommandCatalogRow> rows,
        ISet<string> layerShortcutAliasKeys)
    {
        var list = new List<PgpAliasDto>();
        foreach (var row in rows)
        {
            if (IsLayerShortcutRow(row))
                continue;
            var target = CadPgpMerge.NormalizeTarget(row.CommandName);
            if (target.Length == 0 || CadPgpMerge.IsLegacyLayerShortcutPgpMacro(target))
                continue;
            var cmdAsKey = CadPgpMerge.NormalizeAlias(row.CommandName);
            if (cmdAsKey.Length > 0 && layerShortcutAliasKeys.Contains(cmdAsKey))
                continue;
            foreach (var a in row.EnumerateAliasTokensForCommandSave())
            {
                if (layerShortcutAliasKeys.Contains(a))
                    continue;
                list.Add(new PgpAliasDto { Alias = a, Target = target });
            }
        }

        return list;
    }

    private static bool TryFindInvalidLayerShortcutAlias(IEnumerable<CommandCatalogRow> rows, out string alias, out string reason)
    {
        alias = "";
        reason = "";
        foreach (var row in rows)
        {
            if (!IsLayerShortcutRow(row))
                continue;
            foreach (var a in CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(row.Alias))
            {
                if (a.Length == 0)
                    continue;
                if (LayerAliasRules.IsValidGeneratedCommandAlias(a, out var why))
                    continue;
                alias = a;
                reason = why;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindProtectedCommandAlias(IEnumerable<CommandCatalogRow> rows, out string alias, out string commandName)
    {
        alias = "";
        commandName = "";
        foreach (var row in rows)
        {
            if (IsLayerShortcutRow(row))
                continue;
            foreach (var a in row.EnumerateAliasTokensForCommandSave())
            {
                if (!LayerAliasRules.IsProtectedCadCommandName(a))
                    continue;
                alias = a;
                commandName = (row.CommandName ?? "").Trim();
                return true;
            }
        }

        return false;
    }

    private static List<LayerShortcutEntry> CollectLayerShortcutsFromGrid(IEnumerable<CommandCatalogRow> rows)
    {
        var list = new List<LayerShortcutEntry>();
        foreach (var row in rows)
        {
            if (!IsLayerShortcutRow(row))
                continue;
            var ln = (row.LayerName ?? "").Trim();
            if (ln.Length == 0)
                continue;
            foreach (var a in CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(row.Alias))
            {
                if (a.Length == 0)
                    continue;
                list.Add(new LayerShortcutEntry
                {
                    Alias = a,
                    LayerName = ln,
                    ColorIndex = LayerStyleHelper.TryParseAciColor(row.LayerColor),
                    LinetypeName = string.IsNullOrWhiteSpace(row.LayerLinetype) ? null : row.LayerLinetype.Trim(),
                    LineWeight = string.IsNullOrWhiteSpace(row.LayerLineWeight) ? null : row.LayerLineWeight.Trim(),
                    Description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim(),
                    RunDimensionWhenNoSelection = row.LayerRunDimensionWhenNoSelection &&
                                                  string.IsNullOrWhiteSpace(row.LayerHatchStyleJson),
                    RunHatchWhenNoSelection = !string.IsNullOrWhiteSpace(row.LayerHatchStyleJson),
                    HatchStyle = string.IsNullOrWhiteSpace(row.LayerHatchStyleJson) ? null : row.LayerHatchStyleJson.Trim()
                });
            }
        }

        return list;
    }

    private void SaveToPgp_Click(object sender, RoutedEventArgs e)
    {
        EnsureNonLayerDataGridsBound();
        CommitPendingDataGridEdits();

        var rowsSnapshot = _allRows.ToList();

        // 仅禁止「填了快捷键却没有图层名」；仅有图层名、快捷键留空（如从图纸导入）允许保存，此类行不写 JSON。
        var badLayer = rowsSnapshot.Where(IsLayerShortcutRow).FirstOrDefault(r =>
        {
            var tokens = CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(r.Alias).ToList();
            var ln = (r.LayerName ?? "").Trim();
            var hasToken = tokens.Count > 0;
            return hasToken && ln.Length == 0;
        });
        if (badLayer != null)
        {
            MessageBox.Show(this, "「图层命令」行需同时填写图层快捷键（代号）与图层名称。", Title, MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (TryFindInvalidLayerShortcutAlias(rowsSnapshot, out var invalidLayerAlias, out var invalidLayerReason))
        {
            MessageBox.Show(this,
                $"图层快捷键「{invalidLayerAlias}」不能保存：{invalidLayerReason}。\n\n请换成短代号，例如 LF、LZ、A1。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (TryFindProtectedCommandAlias(rowsSnapshot, out var protectedCommandAlias, out var protectedCommandTarget))
        {
            MessageBox.Show(this,
                $"命令别名「{protectedCommandAlias}」不能保存：它与 CAD 自带命令同名。\n\n当前指向：{protectedCommandTarget}\n请换成短代号，例如 LF、LZ、A1。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dupCheck = SaveDuplicateChecker.Analyze(rowsSnapshot);
        if (dupCheck.HasIssues)
        {
            var cont = MessageBox.Show(this, dupCheck.DialogText, "保存前：发现重复",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (cont != MessageBoxResult.Yes)
                return;
        }

        var saveSummary = SaveSummaryBuilder.Build(rowsSnapshot);

        var layerEntries = CollectLayerShortcutsFromGrid(rowsSnapshot);

        try
        {
            LayerShortcutStore.Save(layerEntries);
            CurrentLayerFloatingTabManager.RequestRefresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存图层配置失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            CommandDescriptionStore.CollectFromRows(rowsSnapshot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存命令说明失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = LayerLispShortcuts.EmitFromEntries(layerEntries);

        var layerAliasKeys = CatalogLayerMerge.GetLayerShortcutAliasKeys(rowsSnapshot);
        var dtos = CollectAliasesFromGrid(rowsSnapshot, layerAliasKeys);
        var hasPgpAliasWrites = dtos.Count > 0;

        var block = CadPgpMerge.BuildAliasBlock(dtos);
        AcAp.DocumentManager.ExecuteInApplicationContext(
            _ =>
            {
                var doc = AcAp.DocumentManager.MdiActiveDocument;
                string? nativeLayerDescriptionSyncNote = null;
                string? layerAliasReloadNote = null;
                string? pgpReloadNote = null;
                if (doc == null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var message = hasPgpAliasWrites
                            ? $"图层配置与说明已保存，并已生成 {LayerLispShortcuts.FileName}。{Environment.NewLine}{Environment.NewLine}" +
                              "当前无活动图纸，所以这次还没有完成下面两件事：" + Environment.NewLine +
                              $"1. 热加载 {LayerLispShortcuts.FileName} 到当前图纸" + Environment.NewLine +
                              "2. 写入并重载 PGP 命令别名" + Environment.NewLine + Environment.NewLine +
                              "切回图纸后，图层别名会自动加载；如果本次还修改了命令别名，请再点一次保存。"
                            : $"图层配置与说明已保存，并已生成 {LayerLispShortcuts.FileName}。{Environment.NewLine}{Environment.NewLine}" +
                              $"当前无活动图纸，所以这次还没有把 {LayerLispShortcuts.FileName} 热加载到图纸里。" + Environment.NewLine +
                              "切回或激活图纸后会自动加载。";
                        UpdateSaveTargetDisplay();
                        SetStatus(
                            hasPgpAliasWrites
                                ? "图层别名已保存；命令别名尚未写入。"
                                : "图层配置已保存；待图纸激活后加载。",
                            hasPgpAliasWrites ? StatusTone.Error : StatusTone.Success);
                        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }

                try
                {
                    LayerLispShortcuts.TryLoadInDocument(doc);
                    layerAliasReloadNote = $"图层别名：已请求热加载 {LayerLispShortcuts.FileName} 到当前图纸。";
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("热加载图层别名 LISP 失败", ex);
                    layerAliasReloadNote = $"图层别名：已保存，但当前图纸热加载失败：{ex.Message}";
                }

                try
                {
                    LayerApplyService.SyncDescriptionsToCurrentDrawing(doc, layerEntries);
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("同步当前图纸原生图层说明失败", ex);
                    nativeLayerDescriptionSyncNote = $"已保存配置，但同步当前图纸图层说明失败：{ex.Message}";
                }

                var r = CadPgpMerge.ApplyToDiscoveredPgp(doc, block);
                if (!r.Ok)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var message =
                            (hasPgpAliasWrites
                                ? "图层别名已保存，并已优先按最新结果尝试热加载；当前失败只影响命令别名写入 PGP。"
                                : "图层别名已保存，并已优先按最新结果尝试热加载；当前失败只发生在额外的 PGP 同步，不影响图层别名使用。")
                            + Environment.NewLine + Environment.NewLine;
                        if (!string.IsNullOrWhiteSpace(layerAliasReloadNote))
                            message += layerAliasReloadNote + Environment.NewLine + Environment.NewLine;
                        message += "PGP：写入失败。" + Environment.NewLine + r.Message;
                        UpdateSaveTargetDisplay();
                        SetStatus(
                            hasPgpAliasWrites
                                ? "图层别名已生效；命令别名写入失败。"
                                : "图层别名已保存；PGP 同步失败。",
                            StatusTone.Error);
                        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }

                var pgpReload = CadPgpReload.TryReloadPgp(doc);
                pgpReloadNote = pgpReload.Ok
                    ? (hasPgpAliasWrites ? "命令别名：已写入 PGP，并已请求重载。" : null)
                    : hasPgpAliasWrites
                        ? $"命令别名：已写入 PGP，但当前会话未自动重载：{pgpReload.Message}"
                        : $"PGP：已同步，但当前会话未自动重载：{pgpReload.Message}";

                var summary = string.IsNullOrWhiteSpace(saveSummary) ? "保存成功。" : saveSummary;
                if (!string.IsNullOrWhiteSpace(nativeLayerDescriptionSyncNote))
                    summary += "\n\n" + nativeLayerDescriptionSyncNote;
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateSaveTargetDisplay();
                    SetStatus(
                        hasPgpAliasWrites
                            ? "图层别名已热加载；命令别名已保存。"
                            : "图层配置已保存。",
                        StatusTone.Success);
                    MessageBox.Show(this, summary, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                });
            },
            null);
    }

    private void GridLayer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        // 单元格编辑中由 TextBox 处理；编辑态下改绑定集合易在宿主内崩溃
        if (e.OriginalSource is TextBox)
            return;
        if (IsTextBoxAncestor(e.OriginalSource as DependencyObject))
            return;
        if (Keyboard.FocusedElement is TextBox && FindAncestorDataGrid(Keyboard.FocusedElement as DependencyObject) == GridLayer)
            return;

        if (TryDeleteSelectedLayerShortcutRows())
            e.Handled = true;
    }

    private void DeleteLayerRows_ContextClick(object sender, RoutedEventArgs e)
    {
        if (TryDeleteSelectedLayerShortcutRows())
            e.Handled = true;
    }

    private static bool IsTextBoxAncestor(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is TextBox)
                return true;
            d = VisualTreeHelper.GetParent(d);
        }

        return false;
    }

    private static DataGrid? FindAncestorDataGrid(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is DataGrid dg)
                return dg;
            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? root, Predicate<T>? predicate = null) where T : DependencyObject
    {
        if (root == null)
            return null;

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && (predicate == null || predicate(typed)))
                return typed;

            var nested = FindVisualChild(child, predicate);
            if (nested != null)
                return nested;
        }

        return null;
    }

    /// <summary>在从 ObservableCollection 移除行或 Clear 之前调用，避免 DataGrid 仍处于编辑模板时改集合导致宿主崩溃。</summary>
    private void PrepareDataGridsBeforeRowCollectionMutation()
    {
        foreach (var dg in new[] { GridLayer, GridCadNative, GridVCommand, GridPluginCommand })
        {
            try
            {
                dg.CancelEdit(DataGridEditingUnit.Row);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("PrepareDataGridsBeforeRowCollectionMutation：CancelEdit", ex);
            }
        }
    }

    private bool TryDeleteSelectedLayerShortcutRows()
    {
        var toRemove = GridLayer.SelectedItems
            .OfType<CommandCatalogRow>()
            .Where(IsLayerShortcutRow)
            .ToList();
        if (toRemove.Count == 0)
            return false;

        try
        {
            PrepareDataGridsBeforeRowCollectionMutation();
            foreach (var row in toRemove)
                _allRows.Remove(row);
            _hasPendingLayerRowDeletion = true;
            RefreshAllViews();
            SetStatus("已删除选中图层行，请点「保存」写入 JSON。", StatusTone.Success);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"删除图层行失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
    }

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit)
            return;
        if (e.Row.Item is CommandCatalogRow r)
        {
            if (e.Column is DataGridBoundColumn { Binding: Binding binding } &&
                string.Equals(binding.Path?.Path, nameof(CommandCatalogRow.Alias), StringComparison.Ordinal))
            {
                r.MarkAliasAsExplicit();
            }

            r.IsUserModified = true;
        }
    }

    /// <summary>
    /// 保存前提交各表正在编辑的单元格，否则 ObservableCollection 里别名/图层名可能仍是旧值（图层本来就不写 PGP，但 JSON 会不完整）。
    /// </summary>
    private void CommitPendingDataGridEdits()
    {
        if (Keyboard.FocusedElement is TextBox focusedTextBox)
        {
            try
            {
                BindingOperations.GetBindingExpression(focusedTextBox, TextBox.TextProperty)?.UpdateSource();
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("CommitPendingDataGridEdits：更新当前 TextBox 绑定源失败", ex);
            }
        }

        foreach (var dg in new[] { GridLayer, GridCadNative, GridVCommand, GridPluginCommand })
        {
            if (dg.ItemsSource == null)
                continue;
            try
            {
                dg.CommitEdit(DataGridEditingUnit.Cell, true);
                dg.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch
            {
                // 无活动编辑时部分环境会抛错，忽略即可
            }
        }
    }

    private sealed class CatalogRowFilterComparer : IComparer
    {
        private readonly FloatingPanelWindow _owner;

        public CatalogRowFilterComparer(FloatingPanelWindow owner) => _owner = owner;

        public int Compare(object? x, object? y)
        {
            if (x is not CommandCatalogRow a || y is not CommandCatalogRow b)
                return 0;
            var ra = _owner.FilterMatchRank(a);
            var rb = _owner.FilterMatchRank(b);
            if (ra != rb)
                return ra.CompareTo(rb);
            return string.Compare(SecondarySortKey(a), SecondarySortKey(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string SecondarySortKey(CommandCatalogRow r)
        {
            if (string.Equals(r.CategoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
                return $"{(r.LayerName ?? "").Trim()}|{(r.Alias ?? "").Trim()}";
            return (r.CommandName ?? "").Trim();
        }
    }

    private sealed class ResettableObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
            RaiseReset();
        }

        public void AddRange(IEnumerable<T> items)
        {
            var changed = false;
            foreach (var item in items)
            {
                Items.Add(item);
                changed = true;
            }

            if (changed)
                RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

}
