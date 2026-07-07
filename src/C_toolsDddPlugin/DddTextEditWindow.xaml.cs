using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsShared;

namespace C_toolsDddPlugin;

public partial class DddTextEditWindow : Window
{
    private const string DefaultStatusMessage = "可直接输入新文字，也可从下方列表带入后再写入图中。";

    private enum StatusTone
    {
        Neutral,
        Success,
        Error
    }

    private enum SharedContentTabKind
    {
        History,
        Remarks,
        Props,
        Materials
    }

    private const string DialogPlacementKey = "Dialog_C_toolsDddPlugin.DddTextEditWindow";
    private const double FixedWindowPixelWidth = 600d;
    private const double FixedWindowPixelHeight = 1080d;
    private readonly ObservableCollection<DddTextHistoryItem> _history = new();
    private readonly ObservableCollection<DddRemarkRow> _remarks = new();
    private readonly ObservableCollection<DddPropRow> _props = new();
    private readonly ObservableCollection<DddMaterialRow> _materials = new();

    private bool _sharedListsPersistSuspended = true;
    private DispatcherTimer? _sharedListsPersistTimer;
    private bool _sharedInsertWithLeader = true;
    private int _capturedSelectedCount;

    public DddTextEditWindow()
        : this(Array.Empty<string>(), 0)
    {
    }

    internal DddTextEditWindow(IReadOnlyList<string> selectedTexts, int selectedCount)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            ApplyFixedWindowSize();
            WindowTitleBarHelper.TryApplyDarkTitleBar(this, applyCaptionColorToBorder: true);
        };

        ListHistory.ItemsSource = _history;
        GridRemarks.ItemsSource = _remarks;
        GridProps.ItemsSource = _props;
        GridMaterials.ItemsSource = _materials;

        LoadHistory();
        WireSharedListPersistence();
        DddPanelListsStore.TryLoadInto(_remarks, _props, _materials, out _sharedInsertWithLeader);
        _sharedListsPersistSuspended = false;

        UpdateSharedContentUi();
        ApplyCapturedSelection(selectedTexts, selectedCount, recordHistory: true);
        Closed += OnClosed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureOwner();
        ApplyFixedWindowSize();
        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this, DialogPlacementKey);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            TxtEditText.Focus();
            TxtEditText.SelectAll();
            DddInputLanguageHelper.SwitchToChineseForWindow(this, "切换 F_ED 中文输入法");
        }), DispatcherPriority.Input);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DddInputLanguageHelper.RestoreEnglishToAcadMainWindow("恢复 F_ED 英文输入法");
        _sharedListsPersistTimer?.Stop();
        FlushSharedListPersist();
        DialogWindowPlacementHelper.TrySavePlacement(this, DialogPlacementKey);
    }

    private void ApplyInput_Click(object sender, RoutedEventArgs e)
    {
        var text = TxtEditText.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("请输入要写入的文字，或先从历史记录中选择一条。", StatusTone.Error);
            return;
        }

        if (!TryApplyTextToSelection(text))
        {
            SetStatus("使用命令前，请先选择文字。", StatusTone.Error);
            return;
        }

        PromoteHistoryText(text, saveNow: true);
        Close();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            SetStatus("历史记录已经是空的。");
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "确定清空全部历史文字记录吗？",
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        _history.Clear();
        SaveHistory();
        SetStatus("已清空历史文字记录。", StatusTone.Success);
    }

    private void ListHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListHistory.SelectedItem is not DddTextHistoryItem item)
            return;

        TxtEditText.Text = item.RawText;
        FocusEditInput(selectAll: true);
    }

    private void ListHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListHistory.SelectedItem is not DddTextHistoryItem item)
            return;

        if (!TryApplyTextToSelection(item.RawText))
        {
            SetStatus("使用命令前，请先选择文字。", StatusTone.Error);
            return;
        }

        PromoteHistoryText(item.RawText, saveNow: true);
        TxtEditText.Text = item.RawText;
        SetStatus("已将历史文字写入当前选中文字。", StatusTone.Success);
    }

    private void TxtListAddText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        AddToSharedList_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void ContentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ContentTabs))
            return;

        UpdateSharedContentUi();
    }

    private void AddToSharedList_Click(object sender, RoutedEventArgs e)
    {
        var text = (TxtListAddText.Text ?? "").Trim();
        if (text.Length == 0)
        {
            SetStatus(DddPanelUiStrings.StatusAddNeedText, StatusTone.Error);
            return;
        }

        switch (GetCurrentSharedContentTabKind())
        {
            case SharedContentTabKind.Remarks:
                _remarks.Add(new DddRemarkRow { RemarkText = text });
                SetStatus(string.Format(DddPanelUiStrings.StatusAddedRemarkFormat, _remarks.Count), StatusTone.Success);
                break;
            case SharedContentTabKind.Props:
                _props.Add(new DddPropRow { ItemName = text, Price = "", Note = "" });
                SetStatus(string.Format(DddPanelUiStrings.StatusAddedPropFormat, _props.Count), StatusTone.Success);
                break;
            case SharedContentTabKind.Materials:
                _materials.Add(new DddMaterialRow { MaterialName = text, Specification = "", Note = "" });
                SetStatus(string.Format(DddPanelUiStrings.StatusAddedMaterialFormat, _materials.Count), StatusTone.Success);
                break;
            default:
                SetStatus(DddPanelUiStrings.StatusSelectTabFirst, StatusTone.Error);
                return;
        }

        TxtListAddText.Clear();
        TxtListAddText.Focus();
    }

    private void ImportSharedList_Click(object sender, RoutedEventArgs e)
    {
        if (GetCurrentSharedContentTabKind() == SharedContentTabKind.History)
        {
            SetStatus("历史文字不支持 Excel 导入。", StatusTone.Error);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Title = DddPanelUiStrings.DialogTitleImportExcel
        };

        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var tabIndex = GetCurrentSharedContentTabKind() switch
            {
                SharedContentTabKind.Remarks => 0,
                SharedContentTabKind.Props => 1,
                SharedContentTabKind.Materials => 2,
                _ => -1
            };

            var (remarks, props, materials, msgs) = DddPanelXlsxImport.ImportFromPath(dlg.FileName, tabIndex);
            if (remarks == null && props == null && materials == null)
            {
                MessageBox.Show(this,
                    msgs.Count > 0 ? string.Join(Environment.NewLine, msgs) : DddPanelUiStrings.MsgImportFailedFallback,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                SetStatus(DddPanelUiStrings.StatusExcelImportNotRun, StatusTone.Error);
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
                SetStatus(DddPanelUiStrings.StatusExcelImportNoRowsAdded, StatusTone.Error);
                return;
            }

            int added = 0;
            int skippedDup = 0;
            if (remarks != null)
            {
                var existing = new HashSet<string>(
                    _remarks.Select(static r => (r.RemarkText ?? "").Trim()).Where(static s => s.Length > 0),
                    StringComparer.Ordinal);

                foreach (var row in remarks)
                {
                    var value = (row.RemarkText ?? "").Trim();
                    if (value.Length == 0)
                        continue;
                    if (existing.Contains(value))
                    {
                        skippedDup++;
                        continue;
                    }

                    _remarks.Add(new DddRemarkRow { RemarkText = value });
                    existing.Add(value);
                    added++;
                }
            }
            else if (props != null)
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in _props)
                    existing.Add(PropMergeKey(row.ItemName, row.Price, row.Note));

                foreach (var row in props)
                {
                    var key = PropMergeKey(row.ItemName, row.Price, row.Note);
                    if (key.Length == 0 || key == "||")
                        continue;
                    if (existing.Contains(key))
                    {
                        skippedDup++;
                        continue;
                    }

                    _props.Add(new DddPropRow { ItemName = row.ItemName, Price = row.Price, Note = row.Note });
                    existing.Add(key);
                    added++;
                }
            }
            else if (materials != null)
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in _materials)
                    existing.Add(MaterialMergeKey(row.MaterialName, row.Specification, row.Note));

                foreach (var row in materials)
                {
                    var key = MaterialMergeKey(row.MaterialName, row.Specification, row.Note);
                    if (key.Length == 0 || key == "||")
                        continue;
                    if (existing.Contains(key))
                    {
                        skippedDup++;
                        continue;
                    }

                    _materials.Add(new DddMaterialRow
                    {
                        MaterialName = row.MaterialName,
                        Specification = row.Specification,
                        Note = row.Note
                    });
                    existing.Add(key);
                    added++;
                }
            }

            var note = string.Format(DddPanelUiStrings.StatusExcelImportResultFormat, added, fileRowCount, skippedDup);
            if (msgs.Count > 0)
                note += Environment.NewLine + string.Join(Environment.NewLine, msgs);

            SetStatus(note, StatusTone.Success);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_ED 共享列表 Excel 导入", ex);
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(string.Format(DddPanelUiStrings.StatusExcelImportFailedFormat, ex.Message), StatusTone.Error);
        }
    }

    private void ClearSharedList_Click(object sender, RoutedEventArgs e)
    {
        var kind = GetCurrentSharedContentTabKind();
        if (kind == SharedContentTabKind.History)
        {
            ClearHistory_Click(sender, e);
            return;
        }

        var tabName = GetCurrentSharedTabName();
        var count = GetCurrentSharedListCount();
        if (count == 0)
        {
            SetStatus(string.Format(DddPanelUiStrings.StatusTabAlreadyEmptyFormat, tabName));
            return;
        }

        var confirm = MessageBox.Show(this,
            string.Format(DddPanelUiStrings.ClearListConfirmFormat, tabName, count),
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        PrepareSharedDataGridsBeforeCollectionMutation();
        RunSharedCollectionMutation(() =>
        {
            switch (kind)
            {
                case SharedContentTabKind.Remarks:
                    ClearCollection(_remarks);
                    break;
                case SharedContentTabKind.Props:
                    ClearCollection(_props);
                    break;
                case SharedContentTabKind.Materials:
                    ClearCollection(_materials);
                    break;
            }
        });

        SetStatus(string.Format(DddPanelUiStrings.StatusListClearedFormat, tabName), StatusTone.Success);
    }

    private void GridSharedList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
            return;
        if (sender is not DataGrid dg)
            return;
        if (!TryResolveSharedListClickText(dg, e, out var text, out var emptyMessage))
            return;

        if (text.Length == 0)
        {
            SetStatus(emptyMessage, StatusTone.Error);
            return;
        }

        TxtEditText.Text = text;
        FocusEditInput(selectAll: true);
        SetStatus("已将列表文字带入输入框。", StatusTone.Success);
    }

    private void GridSharedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dg)
            return;
        if (!TryResolveSharedListClickText(dg, e, out var text, out var emptyMessage))
            return;

        if (text.Length == 0)
        {
            SetStatus(emptyMessage, StatusTone.Error);
            return;
        }

        TxtEditText.Text = text;
        FocusEditInput(selectAll: true);
        if (!TryApplyTextToSelection(text))
        {
            SetStatus("使用命令前，请先选择文字。", StatusTone.Error);
            return;
        }

        PromoteHistoryText(text, saveNow: true);
        SetStatus("已将列表文字写入当前选中文字。", StatusTone.Success);
    }

    private void GridSharedList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (sender is not DataGrid dg)
            return;

        var editingCell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (editingCell?.IsEditing == true)
            return;

        var selectedItems = GetCheckedRows(dg).Cast<object>().ToList();
        if (selectedItems.Count == 0)
            return;

        var tabName = GetCurrentSharedTabName();
        PrepareSharedDataGridsBeforeCollectionMutation();
        RunSharedCollectionMutation(() =>
        {
            switch (GetCurrentSharedContentTabKind())
            {
                case SharedContentTabKind.Remarks:
                    RemoveSelectedRows(_remarks, selectedItems);
                    break;
                case SharedContentTabKind.Props:
                    RemoveSelectedRows(_props, selectedItems);
                    break;
                case SharedContentTabKind.Materials:
                    RemoveSelectedRows(_materials, selectedItems);
                    break;
            }
        });

        SetStatus($"已从「{tabName}」删除 {selectedItems.Count} 条。", StatusTone.Success);
        e.Handled = true;
    }

    private void ApplyCapturedSelection(IReadOnlyList<string> texts, int selectedCount, bool recordHistory)
    {
        _capturedSelectedCount = Math.Max(0, selectedCount);

        if (recordHistory)
        {
            foreach (var text in texts)
                PromoteHistoryText(text, saveNow: false);
            SaveHistory();
        }

        if (texts.Count == 1 && string.IsNullOrWhiteSpace(TxtEditText.Text))
            TxtEditText.Text = texts[0];

        if (_capturedSelectedCount > 0)
        {
            SetStatus(string.Empty);
            return;
        }

        SetStatus(string.Empty);
    }

    private bool TryApplyTextToSelection(string text)
    {
        var applied = false;

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                        return;

                    applied = DddDrawingSelectionSync.TryCaptureAndApplyTextToImpliedSelection(doc, text);
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（F_ED 写回选中文字）", ex);
        }

        return applied;
    }

    private void LoadHistory()
    {
        _history.Clear();
        foreach (var record in DddTextEditHistoryStore.Load())
            _history.Add(new DddTextHistoryItem(record.Text, record.UpdatedAtUtc));
    }

    private void SaveHistory()
    {
        var records = _history
            .Select(static item => new DddTextEditHistoryRecord
            {
                Text = item.RawText,
                UpdatedAtUtc = item.UpdatedAtUtc
            })
            .ToArray();

        DddTextEditHistoryStore.Save(records);
    }

    private void PromoteHistoryText(string rawText, bool saveNow)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var existing = _history.FirstOrDefault(item => string.Equals(item.RawText, rawText, StringComparison.Ordinal));
        if (existing != null)
            _history.Remove(existing);

        _history.Insert(0, new DddTextHistoryItem(rawText, DateTime.UtcNow));
        while (_history.Count > DddTextEditHistoryStore.MaxEntryCount)
            _history.RemoveAt(_history.Count - 1);

        if (saveNow)
            SaveHistory();
    }

    private void WireSharedListPersistence()
    {
        _remarks.CollectionChanged += OnRemarksCollectionChanged;
        _props.CollectionChanged += OnPropsCollectionChanged;
        _materials.CollectionChanged += OnMaterialsCollectionChanged;
    }

    private void OnRemarksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DddRemarkRow row in e.NewItems)
                row.PropertyChanged += OnSharedRowPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DddRemarkRow row in e.OldItems)
                row.PropertyChanged -= OnSharedRowPropertyChanged;
        }

        ScheduleSharedListPersist();
    }

    private void OnPropsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DddPropRow row in e.NewItems)
                row.PropertyChanged += OnSharedRowPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DddPropRow row in e.OldItems)
                row.PropertyChanged -= OnSharedRowPropertyChanged;
        }

        ScheduleSharedListPersist();
    }

    private void OnMaterialsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DddMaterialRow row in e.NewItems)
                row.PropertyChanged += OnSharedRowPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DddMaterialRow row in e.OldItems)
                row.PropertyChanged -= OnSharedRowPropertyChanged;
        }

        ScheduleSharedListPersist();
    }

    private void OnSharedRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DddNotifyRowBase.IsSelected))
            return;
        ScheduleSharedListPersist();
    }

    private void ScheduleSharedListPersist()
    {
        if (_sharedListsPersistSuspended)
            return;

        if (_sharedListsPersistTimer == null)
        {
            _sharedListsPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _sharedListsPersistTimer.Tick += (_, _) =>
            {
                _sharedListsPersistTimer.Stop();
                FlushSharedListPersist();
            };
        }

        _sharedListsPersistTimer.Stop();
        _sharedListsPersistTimer.Start();
    }

    private void FlushSharedListPersist()
    {
        if (_sharedListsPersistSuspended)
            return;

        DddPanelListsStore.Save(_remarks, _props, _materials, _sharedInsertWithLeader);
    }

    private void PrepareSharedDataGridsBeforeCollectionMutation()
    {
        foreach (var grid in new[] { GridRemarks, GridProps, GridMaterials })
        {
            try
            {
                grid.CancelEdit(DataGridEditingUnit.Cell);
                grid.CancelEdit(DataGridEditingUnit.Row);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_ED PrepareSharedDataGridsBeforeCollectionMutation", ex);
            }
        }
    }

    private void RunSharedCollectionMutation(Action action)
    {
        var oldSuspended = _sharedListsPersistSuspended;
        _sharedListsPersistSuspended = true;
        try
        {
            action();
        }
        finally
        {
            _sharedListsPersistSuspended = oldSuspended;
            if (!oldSuspended)
                ScheduleSharedListPersist();
        }
    }

    private void UpdateSharedContentUi()
    {
        var isHistory = GetCurrentSharedContentTabKind() == SharedContentTabKind.History;
        BtnClearHistory.Visibility = isHistory ? Visibility.Visible : Visibility.Collapsed;
        SharedListActionsPanel.Visibility = Visibility.Collapsed;
    }

    private SharedContentTabKind GetCurrentSharedContentTabKind() =>
        ContentTabs.SelectedIndex switch
        {
            1 => SharedContentTabKind.Remarks,
            2 => SharedContentTabKind.Props,
            3 => SharedContentTabKind.Materials,
            _ => SharedContentTabKind.History
        };

    private string GetCurrentSharedAddLabel() =>
        GetCurrentSharedContentTabKind() switch
        {
            SharedContentTabKind.Remarks => "文字",
            SharedContentTabKind.Props => "道具名称",
            SharedContentTabKind.Materials => "材料名称",
            _ => "输入"
        };

    private string GetCurrentSharedTabName() =>
        GetCurrentSharedContentTabKind() switch
        {
            SharedContentTabKind.Remarks => DddPanelUiStrings.TabNameTextList,
            SharedContentTabKind.Props => DddPanelUiStrings.TabNamePropList,
            SharedContentTabKind.Materials => DddPanelUiStrings.TabNameMaterialList,
            _ => "历史文字"
        };

    private int GetCurrentSharedListCount() =>
        GetCurrentSharedContentTabKind() switch
        {
            SharedContentTabKind.Remarks => _remarks.Count,
            SharedContentTabKind.Props => _props.Count,
            SharedContentTabKind.Materials => _materials.Count,
            _ => _history.Count
        };

    private static void ClearCollection<T>(ObservableCollection<T> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            rows.RemoveAt(i);
    }

    private static void RemoveSelectedRows<T>(ObservableCollection<T> rows, IReadOnlyList<object> selectedItems) where T : class
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (selectedItems.Contains(rows[i]!))
                rows.RemoveAt(i);
        }
    }

    private static IEnumerable<DddNotifyRowBase> GetCheckedRows(DataGrid dg) =>
        dg.Items
            .OfType<DddNotifyRowBase>()
            .Where(static row => row.IsSelected);

    private static string PropMergeKey(string? name, string? price, string? note) =>
        $"{name?.Trim() ?? ""}|{price?.Trim() ?? ""}|{note?.Trim() ?? ""}";

    private static string MaterialMergeKey(string? name, string? specification, string? note) =>
        $"{name?.Trim() ?? ""}|{specification?.Trim() ?? ""}|{note?.Trim() ?? ""}";

    private static bool TryResolveSharedListClickText(DataGrid dg, MouseButtonEventArgs e, out string text, out string emptyMessage)
    {
        text = "";
        emptyMessage = DddPanelUiStrings.StatusLeaderMainColumnEmpty;

        var cell = FindAncestorDataGridCell(e.OriginalSource as DependencyObject);
        if (cell?.Column is not DataGridTextColumn tc)
            return false;
        if (tc.Binding is not Binding binding)
            return false;

        switch (binding.Path?.Path)
        {
            case "RemarkText":
                if (!TryGetRowForGridClick(dg, cell, e, out DddRemarkRow? remark) || remark == null)
                    return false;
                text = (remark.RemarkText ?? "").Trim();
                emptyMessage = DddPanelUiStrings.StatusRemarkTextEmpty;
                return true;

            case "ItemName":
                if (!TryGetRowForGridClick(dg, cell, e, out DddPropRow? prop) || prop == null)
                    return false;
                text = (prop.ItemName ?? "").Trim();
                emptyMessage = DddPanelUiStrings.StatusPropNameEmpty;
                return true;

            case "MaterialName":
                if (!TryGetRowForGridClick(dg, cell, e, out DddMaterialRow? material) || material == null)
                    return false;
                text = (material.MaterialName ?? "").Trim();
                emptyMessage = DddPanelUiStrings.StatusMaterialNameEmpty;
                return true;

            default:
                return false;
        }
    }

    private static DataGridCell? FindAncestorDataGridCell(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is DataGridCell cell)
                return cell;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T typed)
                return typed;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static bool TryGetRowForGridClick<T>(DataGrid dg, DataGridCell cell, MouseButtonEventArgs e, out T? row) where T : class
    {
        row = null;

        var rowHost = FindAncestorDataGridRow(cell);
        if (rowHost?.Item is T item)
        {
            row = item;
            return true;
        }

        var hit = e.OriginalSource as DependencyObject;
        var rowFromHit = FindAncestorDataGridRow(hit);
        if (rowFromHit?.Item is T hitItem)
        {
            row = hitItem;
            return true;
        }

        if (dg.SelectedItem is T selectedItem)
        {
            row = selectedItem;
            return true;
        }

        return false;
    }

    private static DataGridRow? FindAncestorDataGridRow(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is DataGridRow row)
                return row;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void FocusEditInput(bool selectAll)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TxtEditText.Focus();
            if (selectAll)
                TxtEditText.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void SetStatus(string text, StatusTone tone = StatusTone.Neutral)
    {
        var hasCustomStatus = !string.IsNullOrWhiteSpace(text);
        var statusText = hasCustomStatus ? text.Trim() : DefaultStatusMessage;
        StatusText.Text = statusText;
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Foreground = (hasCustomStatus ? tone : StatusTone.Neutral) switch
        {
            StatusTone.Success => ResolveBrush("Cad.StatusSuccess", "#9BCBFF"),
            StatusTone.Error => ResolveBrush("Cad.StatusError", "#FF7B5C"),
            _ => ResolveBrush("Cad.Muted", "#8B949E")
        };
    }

    private Brush ResolveBrush(string resourceKey, string fallbackHex)
    {
        if (TryFindResource(resourceKey) is Brush brush)
            return brush;

        return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
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

    private void EnsureOwner()
    {
        try
        {
            if (Owner != null)
                return;

            var owner = AcAp.MainWindow?.Handle ?? IntPtr.Zero;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(this) { Owner = owner };
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("设置 F_ED 文字快改弹窗 Owner", ex);
        }
    }

}

internal sealed class DddTextHistoryItem
{
    internal DddTextHistoryItem(string rawText, DateTime updatedAtUtc)
    {
        RawText = rawText ?? "";
        UpdatedAtUtc = updatedAtUtc == default ? DateTime.UtcNow : updatedAtUtc;
    }

    internal string RawText { get; }

    internal DateTime UpdatedAtUtc { get; }

    public string DisplayText => DddTextHistoryTextFormatter.ToDisplayText(RawText);

    public string UpdatedAtText => "记录于 " + UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

internal static class DddTextHistoryTextFormatter
{
    internal static string ToDisplayText(string? rawText)
    {
        var text = rawText ?? "";
        if (text.Length == 0)
            return "(空文字)";

        var displayText = text.Replace("\\P", Environment.NewLine);
        return DddTextContentHelper.NormalizeLineEndings(displayText);
    }
}
