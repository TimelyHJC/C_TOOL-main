using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;
using C_toolsShared;

namespace C_toolsDddPlugin;

public partial class DddPanelWindow : Window, IModelessWindowPlacement, IModelessWindowHostAware
{
    public string PlacementKey => "C_TOOL_DddPanel";

    private const double FixedWindowPixelWidth = 600d;
    private const double FixedWindowPixelHeight = 1080d;

    private readonly ObservableCollection<DddRemarkRow> _remarks = new();
    private readonly ObservableCollection<DddPropRow> _props = new();
    private readonly ObservableCollection<DddMaterialRow> _materials = new();
    private readonly string? _initialInputText;

    private bool _persistSuspended;
    private bool _isSelectionMode;

    private DispatcherTimer? _persistTimer;

    private bool _suppressMLeaderStyleCombo;

    public DddPanelWindow(string? initialInputText = null)
    {
        _initialInputText = string.IsNullOrWhiteSpace(initialInputText) ? null : initialInputText;
        _persistSuspended = true;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            ApplyFixedWindowSize();
            WindowTitleBarHelper.TryApplyDarkTitleBar(this, applyCaptionColorToBorder: true);
        };
        ApplyFixedWindowSize();
        GridRemarks.ItemsSource = _remarks;
        GridProps.ItemsSource = _props;
        GridMaterials.ItemsSource = _materials;
        WireListPersistence();
        DddPanelListsStore.TryLoadInto(_remarks, _props, _materials, out var insertWithLeader);
        ChkInsertWithLeader.IsChecked = insertWithLeader;
        _persistSuspended = false;
        UpdateInputLabel();
        RefreshInteractionStatus();
        IsVisibleChanged += DddPanelWindow_IsVisibleChanged;
        Closed += DddPanelWindow_Closed;
        AcAp.SystemVariableChanged += OnApplicationSystemVariableChanged;
    }

    private void WireListPersistence()
    {
        _remarks.CollectionChanged += OnRemarksCollectionChanged;
        _props.CollectionChanged += OnPropsCollectionChanged;
        _materials.CollectionChanged += OnMaterialsCollectionChanged;
    }

    private void OnRemarksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DddRemarkRow r in e.NewItems)
                r.PropertyChanged += OnRemarkRowPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DddRemarkRow r in e.OldItems)
                r.PropertyChanged -= OnRemarkRowPropertyChanged;
        }

        if (!_persistSuspended)
            SchedulePersist();
    }

    private void OnPropsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DddPropRow r in e.NewItems)
                r.PropertyChanged += OnPropRowPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DddPropRow r in e.OldItems)
                r.PropertyChanged -= OnPropRowPropertyChanged;
        }

        if (!_persistSuspended)
            SchedulePersist();
    }

    private void OnMaterialsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DddMaterialRow r in e.NewItems)
                r.PropertyChanged += OnMaterialRowPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DddMaterialRow r in e.OldItems)
                r.PropertyChanged -= OnMaterialRowPropertyChanged;
        }

        if (!_persistSuspended)
            SchedulePersist();
    }

    private void OnRemarkRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DddNotifyRowBase.IsSelected))
            return;
        SchedulePersist();
    }

    private void OnPropRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DddNotifyRowBase.IsSelected))
            return;
        SchedulePersist();
    }

    private void OnMaterialRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DddNotifyRowBase.IsSelected))
            return;
        SchedulePersist();
    }

    private void SchedulePersist()
    {
        if (_persistSuspended)
            return;
        if (_persistTimer == null)
        {
            _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _persistTimer.Tick += (_, _) =>
            {
                _persistTimer.Stop();
                FlushPersist();
            };
        }

        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void FlushPersist()
    {
        if (_persistSuspended)
            return;
        DddPanelListsStore.Save(_remarks, _props, _materials, IsInsertWithLeaderEnabled());
    }

    private void DddPanelWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            FlushPersist();
            return;
        }

        RefreshMLeaderStyles();
    }

    private void DddPanelWindow_Closed(object? sender, EventArgs e)
    {
        DddInputLanguageHelper.RestoreEnglishToAcadMainWindow("恢复 V_DDD 英文输入法");
        DialogWindowPlacementHelper.TrySavePlacement(this);
        AcAp.SystemVariableChanged -= OnApplicationSystemVariableChanged;
        _persistTimer?.Stop();
        FlushPersist();
    }

    /// <summary>CMLEADERSTYLE 在 CAD 内变化时同步下拉框，避免必须切换窗口才更新。</summary>
    private void OnApplicationSystemVariableChanged(object? sender, SystemVariableChangedEventArgs e)
    {
        try
        {
            if (e?.Name == null)
                return;
            if (!string.Equals(e.Name.Trim(), MLeaderStyleHelper.SysVarCurrentMLeaderStyle, StringComparison.OrdinalIgnoreCase))
                return;
            Dispatcher.BeginInvoke(new Action(RefreshMLeaderStyles));
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("SystemVariableChanged（CMLEADERSTYLE）", ex);
        }
    }

    /// <summary>从 CAD 回到浮窗时刷新预选快照与引线样式下拉（与 CMLEADERSTYLE 一致）。</summary>
    private void DddPanelWindow_Activated(object? sender, EventArgs e)
    {
        RefreshMLeaderStyles();
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var d = AcAp.DocumentManager.MdiActiveDocument;
                    if (d != null)
                        DddDrawingSelectionSync.CaptureImpliedTextSelection(d);
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（浮窗激活刷新文字预选快照）", ex);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this);
        ApplyFixedWindowSize();
        RefreshMLeaderStyles();
        if (!string.IsNullOrWhiteSpace(_initialInputText))
            TxtAddText.Text = _initialInputText;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TxtAddText.Focus();
            TxtAddText.SelectAll();
            DddInputLanguageHelper.SwitchToChineseForWindow(this, "切换 V_DDD 中文输入法");
        }), DispatcherPriority.Input);
    }

    private static DddPanelListKind ResolveListKind(int selectedIndex) =>
        selectedIndex switch
        {
            0 => DddPanelListKind.Remarks,
            1 => DddPanelListKind.Props,
            2 => DddPanelListKind.Materials,
            _ => DddPanelListKind.Remarks
        };

    private DddPanelListKind GetCurrentListKind() => ResolveListKind(MainTabs.SelectedIndex);

    private string GetCurrentTabName() =>
        MainTabs.SelectedIndex switch
        {
            0 => DddPanelUiStrings.TabNameTextList,
            1 => DddPanelUiStrings.TabNamePropList,
            2 => DddPanelUiStrings.TabNameMaterialList,
            _ => DddPanelUiStrings.TabNameGeneric
        };

    private int GetCurrentListCount() =>
        MainTabs.SelectedIndex switch
        {
            0 => _remarks.Count,
            1 => _props.Count,
            2 => _materials.Count,
            _ => 0
        };

    private void CommitPendingDataGridEdits()
    {
        foreach (var dg in new[] { GridRemarks, GridProps, GridMaterials })
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
            }
        }
    }

    private void PrepareDataGridsBeforeRowCollectionMutation()
    {
        foreach (var dg in new[] { GridRemarks, GridProps, GridMaterials })
        {
            try
            {
                dg.CancelEdit(DataGridEditingUnit.Cell);
                dg.CancelEdit(DataGridEditingUnit.Row);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("PrepareDataGridsBeforeRowCollectionMutation", ex);
            }
        }
    }

    private void RunBulkCollectionMutation(Action action)
    {
        var oldSuspended = _persistSuspended;
        _persistSuspended = true;
        try
        {
            action();
        }
        finally
        {
            _persistSuspended = oldSuspended;
            if (!oldSuspended)
                SchedulePersist();
        }
    }

    private static void ClearCollection<T>(ObservableCollection<T> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            rows.RemoveAt(i);
    }

    private void ApplyFixedWindowSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;
        var widthDip = FixedWindowPixelWidth / scaleX;
        var heightDip = FixedWindowPixelHeight / scaleY;

        Width = widthDip;
        Height = heightDip;
        MinWidth = widthDip;
        MinHeight = heightDip;
        MaxWidth = widthDip;
        MaxHeight = heightDip;
        ResizeMode = ResizeMode.NoResize;
    }

    private void RefreshMLeaderStyles()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null || CmbMLeaderStyle == null)
            return;

        _suppressMLeaderStyleCombo = true;
        try
        {
            var names = MLeaderStyleHelper.GetStyleNames(doc.Database);
            CmbMLeaderStyle.ItemsSource = names;
            var cur = MLeaderStyleHelper.TryGetCurrentStyleName(doc);
            var configured = UserConfigurationStore.TryGetMLeaderStyleName();
            var selectedName = FindMLeaderStyleMatch(names, cur);
            if (selectedName == null)
                selectedName = FindMLeaderStyleMatch(names, configured);

            if (selectedName != null)
            {
                CmbMLeaderStyle.SelectedItem = selectedName;
            }
            else if (names.Count > 0)
            {
                CmbMLeaderStyle.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressMLeaderStyleCombo = false;
        }
    }

    private void CmbMLeaderStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMLeaderStyleCombo)
            return;
        if (CmbMLeaderStyle.SelectedItem is not string name)
            return;
        var nameCopy = name.Trim();
        if (nameCopy.Length == 0)
            return;

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var ok = MLeaderStyleHelper.TrySetCurrentStyleName(nameCopy);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!ok)
                            SetStatus(DddPanelUiStrings.StatusCannotSwitchStyle);
                        else
                            SetStatus(string.Format(DddPanelUiStrings.StatusStyleSetSuccessFormat, nameCopy));
                    });
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（CMLEADERSTYLE DDD）", ex);
            SetStatus(string.Format(DddPanelUiStrings.StatusCannotSwitchStyleWithReasonFormat, ex.Message));
        }
    }

    private static string? FindMLeaderStyleMatch(IReadOnlyList<string> names, string? targetName)
    {
        var target = targetName?.Trim() ?? "";
        if (target.Length == 0)
            return null;

        return names.FirstOrDefault(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase));
    }

    private string? GetSelectedMLeaderStyleName()
    {
        if (CmbMLeaderStyle?.SelectedItem is not string name)
            return null;

        var trimmed = name.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private void OpenMLeaderStyleDialog_Click(object sender, RoutedEventArgs e)
    {
        if (AcAp.DocumentManager.MdiActiveDocument == null)
            return;

        var selectedStyleName = GetSelectedMLeaderStyleName();
        var styleNameToApply = string.IsNullOrWhiteSpace(selectedStyleName) ? null : selectedStyleName;
        var queued = false;
        var styleApplied = styleNameToApply == null;
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var d = AcAp.DocumentManager.MdiActiveDocument;
                    if (d == null)
                        return;

                    if (styleNameToApply != null)
                        styleApplied = MLeaderStyleHelper.TrySetCurrentStyleName(styleNameToApply);

                    if (!styleApplied)
                        return;

                    d.SendStringToExecute("_.MLEADERSTYLE\n", true, false, false);
                    queued = true;
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（MLEADERSTYLE DDD）", ex);
            SetStatus(string.Format(DddPanelUiStrings.StatusCannotOpenLeaderSettingsFormat, ex.Message));
            return;
        }

        if (!styleApplied)
        {
            SetStatus(DddPanelUiStrings.StatusCannotSwitchStyle);
            return;
        }

        if (queued)
            Close();
    }

    private bool IsInsertWithLeaderEnabled() => ChkInsertWithLeader.IsChecked != false;

    private void RefreshInteractionStatus()
    {
        if (_isSelectionMode)
        {
            SetStatus(DddPanelUiStrings.StatusSelectionModeEnabled);
            return;
        }

        SetStatus(IsInsertWithLeaderEnabled()
            ? DddPanelUiStrings.StatusInsertModeLeader
            : DddPanelUiStrings.StatusInsertModePlainText);
    }

    private void InsertWithLeaderMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_persistSuspended)
            return;

        SchedulePersist();
        RefreshInteractionStatus();
    }

    private void SelectionMode_Changed(object sender, RoutedEventArgs e)
    {
        _isSelectionMode = BtnSelectionMode.IsChecked == true;
        RefreshInteractionStatus();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs))
            return;
        UpdateInputLabel();
    }

    private void UpdateInputLabel()
    {
        TxtAddLabel.Text = MainTabs.SelectedIndex switch
        {
            0 => "文字",
            1 => "道具名称",
            2 => "材料名称",
            _ => "输入"
        };
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ImportTableFromXlsx_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Title = DddPanelUiStrings.DialogTitleImportExcel
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var (remarks, props, materials, msgs) = DddPanelXlsxImport.ImportFromPath(dlg.FileName, MainTabs.SelectedIndex);
            if (remarks == null && props == null && materials == null)
            {
                MessageBox.Show(this,
                    msgs.Count > 0 ? string.Join(Environment.NewLine, msgs) : DddPanelUiStrings.MsgImportFailedFallback,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                SetStatus(DddPanelUiStrings.StatusExcelImportNotRun);
                return;
            }

            var fileRowCount = remarks?.Count ?? props?.Count ?? materials?.Count ?? 0;
            if (fileRowCount == 0)
            {
                MessageBox.Show(this,
                    msgs.Count > 0 ? string.Join(Environment.NewLine, msgs) : DddPanelUiStrings.MsgImportNoRowsFallback,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                SetStatus(DddPanelUiStrings.StatusExcelImportNoRowsAdded);
                return;
            }

            int added = 0;
            int skippedDup = 0;
            if (remarks != null)
            {
                var existing = new HashSet<string>(
                    _remarks.Select(static r => (r.RemarkText ?? "").Trim()).Where(static s => s.Length > 0),
                    StringComparer.Ordinal);
                foreach (var r in remarks)
                {
                    var t = (r.RemarkText ?? "").Trim();
                    if (t.Length == 0)
                        continue;
                    if (existing.Contains(t))
                    {
                        skippedDup++;
                        continue;
                    }

                    _remarks.Add(new DddRemarkRow { RemarkText = t });
                    existing.Add(t);
                    added++;
                }
            }
            else if (props != null)
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in _props)
                    existing.Add(PropMergeKey(x.ItemName, x.Price, x.Note));

                foreach (var r in props)
                {
                    var k = PropMergeKey(r.ItemName, r.Price, r.Note);
                    if (k.Length == 0 || k == "||")
                        continue;
                    if (existing.Contains(k))
                    {
                        skippedDup++;
                        continue;
                    }

                    _props.Add(new DddPropRow { ItemName = r.ItemName, Price = r.Price, Note = r.Note });
                    existing.Add(k);
                    added++;
                }
            }
            else if (materials != null)
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in _materials)
                    existing.Add(MaterialMergeKey(x.MaterialName, x.Specification, x.Note));

                foreach (var r in materials)
                {
                    var k = MaterialMergeKey(r.MaterialName, r.Specification, r.Note);
                    if (k.Length == 0 || k == "||")
                        continue;
                    if (existing.Contains(k))
                    {
                        skippedDup++;
                        continue;
                    }

                    _materials.Add(new DddMaterialRow
                    {
                        MaterialName = r.MaterialName,
                        Specification = r.Specification,
                        Note = r.Note
                    });
                    existing.Add(k);
                    added++;
                }
            }

            var note = string.Format(DddPanelUiStrings.StatusExcelImportResultFormat, added, fileRowCount, skippedDup);
            if (msgs.Count > 0)
                note += Environment.NewLine + string.Join(Environment.NewLine, msgs);
            SetStatus(note);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("DDD 浮窗 Excel 导入", ex);
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(string.Format(DddPanelUiStrings.StatusExcelImportFailedFormat, ex.Message));
        }
    }

    private DataGrid? GetCurrentGrid() =>
        GetCurrentListKind() switch
        {
            DddPanelListKind.Remarks => GridRemarks,
            DddPanelListKind.Props => GridProps,
            DddPanelListKind.Materials => GridMaterials,
            _ => null
        };

    private IReadOnlyList<T> GetCheckedRows<T>() where T : DddNotifyRowBase =>
        GetCurrentRowSource<T>()
            .Where(static row => row.IsSelected)
            .ToList();

    private IEnumerable<T> GetCurrentRowSource<T>() where T : DddNotifyRowBase =>
        typeof(T) == typeof(DddRemarkRow)
            ? _remarks.Cast<T>()
            : typeof(T) == typeof(DddPropRow)
                ? _props.Cast<T>()
                : _materials.Cast<T>();

    private static int RemoveSelectedRows<T>(ObservableCollection<T> rows, IReadOnlyCollection<T> selectedRows) where T : class
    {
        if (selectedRows.Count == 0)
            return 0;

        var selected = new HashSet<T>(selectedRows);
        var removed = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!selected.Contains(rows[i]))
                continue;

            rows.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private void DeleteSelectedRows_Click(object sender, RoutedEventArgs e)
    {
        var tabName = GetCurrentTabName();
        var currentGrid = GetCurrentGrid();
        if (currentGrid == null)
        {
            SetStatus(DddPanelUiStrings.StatusSelectTabFirst);
            return;
        }

        CommitPendingDataGridEdits();
        PrepareDataGridsBeforeRowCollectionMutation();
        var deletedCount = 0;
        RunBulkCollectionMutation(() =>
        {
            switch (GetCurrentListKind())
            {
                case DddPanelListKind.Remarks:
                    deletedCount = RemoveSelectedRows(_remarks, GetCheckedRows<DddRemarkRow>());
                    break;
                case DddPanelListKind.Props:
                    deletedCount = RemoveSelectedRows(_props, GetCheckedRows<DddPropRow>());
                    break;
                case DddPanelListKind.Materials:
                    deletedCount = RemoveSelectedRows(_materials, GetCheckedRows<DddMaterialRow>());
                    break;
                default:
                    throw new InvalidOperationException(DddPanelUiStrings.StatusSelectTabFirst);
            }
        });

        if (deletedCount == 0)
        {
            SetStatus(string.Format(DddPanelUiStrings.StatusDeleteNeedSelectionFormat, tabName));
            return;
        }

        currentGrid.SelectedItems.Clear();
        SetStatus(string.Format(DddPanelUiStrings.StatusDeletedSelectedRowsFormat, tabName, deletedCount));
    }

    private void ListGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (sender is not DataGrid)
            return;

        var editingCell = FindAncestorDataGridCell(e.OriginalSource as DependencyObject);
        if (editingCell?.IsEditing == true)
            return;

        DeleteSelectedRows_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private static string PropMergeKey(string? name, string? price, string? note) =>
        $"{name?.Trim() ?? ""}|{price?.Trim() ?? ""}|{note?.Trim() ?? ""}";

    private static string MaterialMergeKey(string? name, string? spec, string? note) =>
        $"{name?.Trim() ?? ""}|{spec?.Trim() ?? ""}|{note?.Trim() ?? ""}";

    private void InsertFromInput_Click(object sender, RoutedEventArgs e)
    {
        var t = TxtAddText.Text ?? "";
        if (string.IsNullOrWhiteSpace(t))
        {
            SetStatus(DddPanelUiStrings.StatusInsertNeedText);
            return;
        }

        if (!TryAddInputToCurrentList(t, out var statusText))
        {
            SetStatus(statusText);
            return;
        }

        SetStatus(statusText);
        TxtAddText.Clear();
        InsertAnnotationText(t);
    }

    private bool TryAddInputToCurrentList(string text, out string statusText)
    {
        switch (MainTabs.SelectedIndex)
        {
            case 0:
                _remarks.Add(new DddRemarkRow { RemarkText = text });
                statusText = string.Format(DddPanelUiStrings.StatusAddedRemarkFormat, _remarks.Count);
                return true;
            case 1:
                _props.Add(new DddPropRow { ItemName = text, Price = "", Note = "" });
                statusText = string.Format(DddPanelUiStrings.StatusAddedPropFormat, _props.Count);
                return true;
            case 2:
                _materials.Add(new DddMaterialRow { MaterialName = text, Specification = "", Note = "" });
                statusText = string.Format(DddPanelUiStrings.StatusAddedMaterialFormat, _materials.Count);
                return true;
            default:
                statusText = DddPanelUiStrings.StatusSelectTabFirst;
                return false;
        }
    }

    private void PickTextFromCad_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        SetStatus(DddPanelUiStrings.StatusPickingTextFromCad);

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    IReadOnlyList<string> pickedTexts = Array.Empty<string>();
                    string statusText;

                    try
                    {
                        if (doc == null)
                        {
                            statusText = DddPanelUiStrings.StatusPickTextNoDocument;
                        }
                        else if (DddDrawingSelectionSync.TryPromptAndCaptureSelectedTextContents(doc, out var texts, out var selectedCount)
                                 && selectedCount > 0
                                 && texts.Count > 0)
                        {
                            pickedTexts = texts;
                            statusText = string.Format(DddPanelUiStrings.StatusPickedTextToInputFormat, texts.Count);
                        }
                        else
                        {
                            statusText = DddPanelUiStrings.StatusPickTextCancelled;
                        }
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("DDD 选择 CAD 文字回填输入框", ex);
                        statusText = string.Format(DddPanelUiStrings.StatusPickTextFailedFormat, ex.Message);
                    }

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() =>
                        {
                            EnsureShown();
                            if (pickedTexts.Count > 0)
                            {
                                TxtAddText.Text = string.Join(Environment.NewLine, pickedTexts);
                                TxtAddText.Focus();
                                TxtAddText.CaretIndex = TxtAddText.Text.Length;
                            }

                            SetStatus(statusText);
                            DddInputLanguageHelper.SwitchToChineseForWindow(this, "切换 V_DDD 中文输入法");
                        }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("DDD 发起 CAD 文字选择失败", ex);
            EnsureShown();
            SetStatus(string.Format(DddPanelUiStrings.StatusPickTextFailedFormat, ex.Message));
        }
    }

    /// <summary>直接使用指定文字按当前模式插入图中。</summary>
    private void InsertAnnotationText(string? textToInsert)
    {
        var row = textToInsert ?? "";
        if (string.IsNullOrWhiteSpace(row))
        {
            SetStatus(DddPanelUiStrings.StatusLeaderMainColumnEmpty);
            return;
        }

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            SetStatus(DddPanelUiStrings.StatusPickTextNoDocument);
            return;
        }

        var useLeader = IsInsertWithLeaderEnabled();
        var selectedStyleName = GetSelectedMLeaderStyleName();

        if (!DddPanelInsertWorkflow.TryQueueInsertFromPanel(doc, row, useLeader, selectedStyleName, out var errorMessage))
        {
            SetStatus(string.Format(
                useLeader
                    ? DddPanelUiStrings.StatusInsertLeaderErrorFormat
                    : DddPanelUiStrings.StatusInsertTextErrorFormat,
                errorMessage));
            return;
        }

        Close();
    }

    /// <summary>文字列表：单击主列；若图中已预选 MText/DBText 则写回该行文字，否则按当前模式插入。</summary>
    private void GridRemarks_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectionMode)
            return;
        if (e.ClickCount != 1)
            return;
        if (sender is not DataGrid dg)
            return;
        var cell = FindAncestorDataGridCell(e.OriginalSource as DependencyObject);
        // 仅在主列上处理点击，其他列保留默认行为
        if (cell?.Column is null || !IsLeaderNameColumn(cell.Column))
            return;

        e.Handled = true;

        if (!TryGetRowForLeaderClick(dg, cell, e, out DddRemarkRow? r) || r is null)
            return;
        var text = r.RemarkText ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(DddPanelUiStrings.StatusRemarkTextEmpty);
            return;
        }

        // 先检查当前是否选中了文字，有则修改文字内容
        if (TryApplyListTextToDrawingSelection(text))
        {
            SetStatus(DddPanelUiStrings.StatusAppliedTextToDrawing);
            return;
        }

        // 未选中文字时，按当前模式插入
        InsertAnnotationText(text);
    }

    private void GridProps_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectionMode)
            return;
        if (e.ClickCount != 1)
            return;
        if (sender is not DataGrid dg)
            return;
        var cell = FindAncestorDataGridCell(e.OriginalSource as DependencyObject);
        // 仅在主列上处理点击，其他列保留默认行为
        if (cell?.Column is null || !IsLeaderNameColumn(cell.Column))
            return;

        e.Handled = true;

        if (!TryGetRowForLeaderClick(dg, cell, e, out DddPropRow? r) || r is null)
            return;
        var text = r.ItemName ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(DddPanelUiStrings.StatusPropNameEmpty);
            return;
        }

        // 先检查当前是否选中了文字，有则修改文字内容
        if (TryApplyListTextToDrawingSelection(text))
        {
            SetStatus(DddPanelUiStrings.StatusAppliedTextToDrawing);
            return;
        }

        // 未选中文字时，按当前模式插入
        InsertAnnotationText(text);
    }

    private void GridMaterials_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectionMode)
            return;
        if (e.ClickCount != 1)
            return;
        if (sender is not DataGrid dg)
            return;
        var cell = FindAncestorDataGridCell(e.OriginalSource as DependencyObject);
        // 仅在主列上处理点击，其他列保留默认行为
        if (cell?.Column is null || !IsLeaderNameColumn(cell.Column))
            return;

        e.Handled = true;

        if (!TryGetRowForLeaderClick(dg, cell, e, out DddMaterialRow? r) || r is null)
            return;
        var text = r.MaterialName ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(DddPanelUiStrings.StatusMaterialNameEmpty);
            return;
        }

        // 先检查当前是否选中了文字，有则修改文字内容
        if (TryApplyListTextToDrawingSelection(text))
        {
            SetStatus(DddPanelUiStrings.StatusAppliedTextToDrawing);
            return;
        }

        // 未选中文字时，按当前模式插入
        InsertAnnotationText(text);
    }

    /// <summary>在 CAD 主线程上刷新预选并写回 MText/DBText；有选中文字时只改字、不插引线。</summary>
    private static bool TryApplyListTextToDrawingSelection(string textToApply)
    {
        return DddPanelInsertWorkflow.TryApplyListTextToDrawingSelection(textToApply);
    }

    /// <summary>名称列：<see cref="DddRemarkRow.RemarkText"/> / <see cref="DddPropRow.ItemName"/> / <see cref="DddMaterialRow.MaterialName"/>。</summary>
    private static bool IsLeaderNameColumn(DataGridColumn column)
    {
        if (column is not DataGridTextColumn tc)
            return false;
        if (tc.Binding is not Binding b)
            return false;
        var path = b.Path?.Path;
        return path is "RemarkText" or "ItemName" or "MaterialName";
    }

    private static DataGridCell? FindAncestorDataGridCell(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is DataGridCell cell)
                return cell;
            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }

    /// <summary>优先用单元格所在行；否则用鼠标下 DataGridRow / 当前选中行。</summary>
    private static bool TryGetRowForLeaderClick<T>(DataGrid dg, DataGridCell cell, MouseButtonEventArgs e, out T? row) where T : class
    {
        row = null;
        var rowHost = FindAncestorDataGridRow(cell);
        if (rowHost?.Item is T t)
        {
            row = t;
            return true;
        }

        var hit = e.OriginalSource as DependencyObject;
        var dr = FindAncestorDataGridRow(hit);
        if (dr?.Item is T t2)
        {
            row = t2;
            return true;
        }

        if (dg.SelectedItem is T t3)
        {
            row = t3;
            return true;
        }

        return false;
    }

    private static DataGridRow? FindAncestorDataGridRow(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is DataGridRow dr)
                return dr;
            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }

    internal void EnsureShown()
    {
        ApplyFixedWindowSize();
        Show();
        ShowActivated = false;
    }

    public void OnHostShowing()
    {
        ApplyFixedWindowSize();
    }

    public void OnHostHiding()
    {
    }
}

/// <summary>文字列表行（文字标注浮窗）。「文字内容」列。</summary>
public sealed class DddRemarkRow : DddNotifyRowBase
{
    private string _remarkText = "";

    public string RemarkText
    {
        get => _remarkText;
        set => SetString(ref _remarkText, value);
    }
}

/// <summary>道具列表行（文字标注浮窗）。道具名称、价格、备注。</summary>
public sealed class DddPropRow : DddNotifyRowBase
{
    private string _itemName = "";
    private string _price = "";
    private string _note = "";

    public string ItemName
    {
        get => _itemName;
        set => SetString(ref _itemName, value);
    }

    public string Price
    {
        get => _price;
        set => SetString(ref _price, value);
    }

    public string Note
    {
        get => _note;
        set => SetString(ref _note, value);
    }
}

/// <summary>材料列表行（文字标注浮窗）。</summary>
public sealed class DddMaterialRow : DddNotifyRowBase
{
    private string _materialName = "";
    private string _specification = "";
    private string _note = "";

    public string MaterialName
    {
        get => _materialName;
        set => SetString(ref _materialName, value);
    }

    public string Specification
    {
        get => _specification;
        set => SetString(ref _specification, value);
    }

    public string Note
    {
        get => _note;
        set => SetString(ref _note, value);
    }
}

public abstract class DddNotifyRowBase : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    protected void SetString(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        var v = value ?? "";
        if (field == v)
            return;
        field = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
