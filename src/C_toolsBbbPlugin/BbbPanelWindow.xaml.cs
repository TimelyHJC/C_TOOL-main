using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using C_toolsShared;

namespace C_toolsBbbPlugin;

public partial class BbbPanelWindow : Window, IModelessWindowPlacement
{
    public string PlacementKey => "C_TOOL_BbbPanel";

    private const string EmptyDeviceNamePlaceholder = "（未填写）";
    private readonly ObservableCollection<BbbResultRow> _rows = new();
    private readonly ObservableCollection<BbbDeviceSummaryRow> _summaryRows = new();
    private string? _excelPath;
    private bool _hasSelectionState;

    internal BbbPanelWindow()
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);
        ConfigureForMode();
        RefreshSummary();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTitleBarHelper.TryApplyDarkTitleBar(this);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var configuredExcelPath = BbbHiddenDeviceNameConfigStore.LoadWorkbookPath();
        if (!string.IsNullOrWhiteSpace(configuredExcelPath))
        {
            _excelPath = configuredExcelPath;
            ExcelPathTextBox.Text = configuredExcelPath;
        }

        if (_hasSelectionState)
            return;

        SetStatus(File.Exists(configuredExcelPath)
            ? GetWorkbookLoadedStatusText()
            : GetInitialStatusText());
    }

    internal void EnsureShown()
    {
        ModelessWindowDisplayHelper.RestoreAndShow(this);
        ShowActivated = false;
    }

    private void ConfigureForMode()
    {
        ConfigureResultsGrid();

        Title = "C_TOOL 设备清单";
        WriteExcelButton.Content = "写入";
        ResultsSectionTitleText.Text = "设备汇总";
    }

    private string GetInitialStatusText()
    {
        return "就绪。";
    }

    private string GetWorkbookLoadedStatusText()
    {
        return "已加载 Excel 模板路径。";
    }

    private string GetWorkbookSelectedStatusText()
    {
        return "已选择 Excel 模板。";
    }

    private void BrowseExcel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Title = "选择输出 Excel 模板"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _excelPath = dialog.FileName;
        ExcelPathTextBox.Text = _excelPath;
        PersistWorkbookPath(_excelPath);
        SetStatus(GetWorkbookSelectedStatusText());
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryRows.Count == 0)
        {
            SetStatus("请先重新执行 V_BBB，读取当前预选或重新选块。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_excelPath) || !File.Exists(_excelPath))
        {
            SetStatus("请先选择有效的 Excel 模板。");
            return;
        }

        try
        {
            var outputRows = BuildWorkbookOutputRows(out var skippedGroupCount, out var skippedQuantity, out var totalQuantity);
            if (outputRows.Count == 0)
            {
                SetStatus("未找到可写入 Excel 的有效设备名称。");
                return;
            }

            var result = BbbWorkbookTemplateWriter.WriteSummaryRows(_excelPath!, outputRows);
            if (!result.Success)
            {
                MessageBox.Show(this, $"写入 Excel 失败：{result.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus($"写入 Excel 失败：{result.Message}");
                return;
            }

            var summary = $"{result.Message} 总数量 {totalQuantity}。";
            if (skippedGroupCount > 0)
                summary += $" 已跳过 {skippedGroupCount} 种空设备名称（数量 {skippedQuantity}）。";

            MessageBox.Show(this, summary, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus(summary);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 Excel 失败", ex);
            MessageBox.Show(this, $"写入 Excel 失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("写入 Excel 失败。");
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 Excel 被拒绝", ex);
            MessageBox.Show(this, $"写入 Excel 失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("写入 Excel 失败。");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("设备清单输出失败", ex);
            MessageBox.Show(this, $"写入 Excel 失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("写入 Excel 失败。");
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            SetStatus("当前没有可导出的结果。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            Title = "导出设备清单对比结果",
            FileName = $"V_BBB_设备清单_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SetStatus($"已导出 CSV：{dialog.FileName}");
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("导出 CSV 失败", ex);
            MessageBox.Show(this, $"导出 CSV 失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("导出 CSV 失败。");
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("导出 CSV 被拒绝", ex);
            MessageBox.Show(this, $"导出 CSV 失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("导出 CSV 失败。");
        }
    }

    private void ClearRows_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        RefreshSummary();
        SetStatus("已清空当前结果列表。");
    }

    private void LoadSelection(BbbSelectionCaptureResult result)
    {
        _hasSelectionState = true;
        _rows.Clear();
        foreach (var item in result.Devices)
            _rows.Add(BbbResultRow.From(item));

        RefreshSummary();

        if (_summaryRows.Count == 0)
        {
            SetStatus(result.Message);
            return;
        }

        SetStatus($"{result.Message} 当前汇总 {_summaryRows.Count} 种设备，共 {_rows.Count} 个数量。");
    }

    internal void ApplySelectionResult(BbbSelectionCaptureResult result)
    {
        if (result.Devices.Count == 0 && result.Message.Length == 0)
            return;

        LoadSelection(result);
    }

    internal void ClearResultsOnHide()
    {
        _hasSelectionState = false;
        _rows.Clear();
        RefreshSummary();
        SetStatus(GetInitialStatusText());
    }

    private void RefreshSummary()
    {
        var summaryRows = BuildSummaryRows();

        _summaryRows.Clear();
        foreach (var row in summaryRows)
            _summaryRows.Add(row);

        DeviceTypeCountText.Text = summaryRows.Count.ToString();
        TotalQuantityText.Text = _rows.Count.ToString();
    }

    private string BuildCsv()
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, new[]
        {
            "设备名称",
            "数量"
        });

        foreach (var row in _summaryRows)
        {
            AppendCsvRow(builder, new[]
            {
                row.DeviceName,
                row.QuantityText
            });
        }

        return builder.ToString();
    }

    private void ConfigureResultsGrid()
    {
        ResultsGrid.Columns.Clear();

        ResultsGrid.ItemsSource = _summaryRows;
        ResultsGrid.FrozenColumnCount = 1;
        ResultsGrid.Columns.Add(CreateTextColumn("设备名称", nameof(BbbDeviceSummaryRow.DeviceName), 1));
        ResultsGrid.Columns.Add(CreateTextColumn("数量", nameof(BbbDeviceSummaryRow.QuantityText), 140, DataGridLengthUnitType.Pixel));
    }

    private static DataGridTextColumn CreateTextColumn(string header, string bindingPath, double width, DataGridLengthUnitType unitType = DataGridLengthUnitType.Star)
    {
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(bindingPath),
            Width = new DataGridLength(width, unitType)
        };
    }

    private List<BbbDeviceSummaryRow> BuildSummaryRows()
    {
        return _rows
            .GroupBy(
                x => string.IsNullOrWhiteSpace(x.DeviceName) ? "" : x.DeviceName.Trim(),
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var displayName = group
                    .Select(x => (x.DeviceName ?? "").Trim())
                    .FirstOrDefault(x => x.Length > 0);

                return new BbbDeviceSummaryRow
                {
                    DeviceName = string.IsNullOrWhiteSpace(displayName) ? EmptyDeviceNamePlaceholder : displayName,
                    QuantityText = group.Count().ToString()
                };
            })
            .OrderBy(x => x.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private List<BbbWorkbookOutputRow> BuildWorkbookOutputRows(
        out int skippedGroupCount,
        out int skippedQuantity,
        out int totalQuantity)
    {
        skippedGroupCount = 0;
        skippedQuantity = 0;
        totalQuantity = 0;

        var rows = new List<BbbWorkbookOutputRow>();
        foreach (var summaryRow in _summaryRows)
        {
            var quantity = int.TryParse(summaryRow.QuantityText, out var parsedQuantity) && parsedQuantity > 0
                ? parsedQuantity
                : 0;

            if (string.IsNullOrWhiteSpace(summaryRow.DeviceName) ||
                string.Equals(summaryRow.DeviceName, EmptyDeviceNamePlaceholder, StringComparison.Ordinal))
            {
                skippedGroupCount++;
                skippedQuantity += quantity;
                continue;
            }

            rows.Add(new BbbWorkbookOutputRow
            {
                DeviceName = summaryRow.DeviceName.Trim(),
                QuantityText = quantity > 0 ? quantity.ToString() : summaryRow.QuantityText
            });

            totalQuantity += quantity;
        }

        return rows;
    }

    private static void AppendCsvRow(StringBuilder builder, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                builder.Append(',');

            var value = values[i] ?? "";
            var escaped = value.Replace("\"", "\"\"");
            builder.Append('"').Append(escaped).Append('"');
        }

        builder.AppendLine();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static void PersistWorkbookPath(string path)
    {
        try
        {
            BbbHiddenDeviceNameConfigStore.SaveWorkbookPath(path);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("记录设备清单 Excel 路径失败", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("记录设备清单 Excel 路径失败（权限）", ex);
        }
    }
}

public sealed class BbbResultRow : BbbNotifyRowBase
{
    internal const string StatusPending = "待比对";
    internal const string StatusMatched = "已匹配";
    internal const string StatusUnmatched = "未匹配";
    internal const string StatusReview = "需确认";
    internal const string StatusMissingName = "缺少名称";

    private string _blockHandle = "";
    private string _blockName = "";
    private string _stateDisplayText = "";
    private string _slotText = "";
    private string _customDisplayText = "";
    private string _deviceName = "";
    private string _matchStatus = StatusPending;
    private string _excelDeviceName = "";
    private string _matchReason = "已读取设备名称，等待导出 Excel。";
    private string _attributePreview = "";

    public string BlockHandle
    {
        get => _blockHandle;
        set => SetString(ref _blockHandle, value);
    }

    public string BlockName
    {
        get => _blockName;
        set => SetString(ref _blockName, value);
    }

    public string StateDisplayText
    {
        get => _stateDisplayText;
        set => SetString(ref _stateDisplayText, value);
    }

    public string SlotText
    {
        get => _slotText;
        set => SetString(ref _slotText, value);
    }

    public string CustomDisplayText
    {
        get => _customDisplayText;
        set => SetString(ref _customDisplayText, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetString(ref _deviceName, value);
    }

    public string MatchStatus
    {
        get => _matchStatus;
        set => SetString(ref _matchStatus, value);
    }

    public string ExcelDeviceName
    {
        get => _excelDeviceName;
        set => SetString(ref _excelDeviceName, value);
    }

    public string MatchReason
    {
        get => _matchReason;
        set => SetString(ref _matchReason, value);
    }

    public string AttributePreview
    {
        get => _attributePreview;
        set => SetString(ref _attributePreview, value);
    }

    internal static BbbResultRow From(BbbSelectedBlockInfo info) =>
        new()
        {
            BlockHandle = info.BlockHandle,
            BlockName = info.BlockName,
            StateDisplayText = info.StateDisplayText,
            SlotText = $"#{info.SlotIndex}",
            CustomDisplayText = BuildCustomDisplayText(info),
            DeviceName = info.DeviceName,
            MatchStatus = StatusPending,
            MatchReason = "已读取设备名称，等待导出 Excel。",
            AttributePreview = info.AttributePreview
        };

    internal BbbSelectedBlockInfo ToSelectedBlockInfo() =>
        new()
        {
            BlockHandle = BlockHandle,
            BlockName = BlockName,
            StateDisplayText = StateDisplayText,
            SlotIndex = int.TryParse(SlotText.TrimStart('#'), out var slotIndex) ? slotIndex : 0,
            DeviceName = DeviceName,
            AttributePreview = AttributePreview
        };

    internal void ResetMatch()
    {
        MatchStatus = StatusPending;
        ExcelDeviceName = "";
        MatchReason = "已读取设备名称，等待导出 Excel。";
    }

    private static string BuildCustomDisplayText(BbbSelectedBlockInfo info)
    {
        var stateDisplayText = (info.StateDisplayText ?? "").Trim();
        var attributePreview = (info.AttributePreview ?? "").Trim();

        if (stateDisplayText.Length == 0)
            return attributePreview.Length == 0 ? "SBJD映射" : attributePreview;

        if (attributePreview.Length == 0 ||
            string.Equals(stateDisplayText, attributePreview, StringComparison.CurrentCultureIgnoreCase))
        {
            return stateDisplayText;
        }

        return $"{stateDisplayText} | {attributePreview}";
    }

    internal void ApplyMatch(BbbMatchResult match)
    {
        MatchStatus = match.Status;
        MatchReason = match.Reason;

        if (match.ExcelRow == null)
            return;

        ExcelDeviceName = match.ExcelRow.DeviceName;
    }
}

public sealed class BbbDeviceSummaryRow : BbbNotifyRowBase
{
    private string _deviceName = "";
    private string _quantityText = "";

    public string DeviceName
    {
        get => _deviceName;
        set => SetString(ref _deviceName, value);
    }

    public string QuantityText
    {
        get => _quantityText;
        set => SetString(ref _quantityText, value);
    }
}

public abstract class BbbNotifyRowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetString(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        var normalized = value ?? "";
        if (field == normalized)
            return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
