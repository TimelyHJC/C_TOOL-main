using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using C_toolsShared;

namespace C_toolsBbbPlugin;

public partial class BbbBlockHiddenDeviceNameWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<BbbBlockHiddenDeviceNameTarget> _targets;
    private readonly ObservableCollection<BbbBlockHiddenDeviceNameOption> _deviceOptions;
    private readonly Dictionary<BbbBlockHiddenDeviceNameOption, int> _deviceOptionOrder = new();
    private readonly ICollectionView _deviceOptionsView;
    private string _searchText = "";
    private string _footerStatusText = "";

    internal BbbBlockHiddenDeviceNameWindow(
        IEnumerable<BbbBlockHiddenDeviceNameTarget> targets,
        IEnumerable<string> deviceNames,
        IEnumerable<string> preselectedNames,
        string workbookPath,
        string worksheetName,
        string columnHeader,
        int ordinaryBlockCount)
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);
        SourceInitialized += (_, _) => WindowTitleBarHelper.TryApplyDarkTitleBar(this);

        WorkbookPath = workbookPath;
        WorksheetName = worksheetName;
        ColumnHeader = columnHeader;

        _targets = new ObservableCollection<BbbBlockHiddenDeviceNameTarget>(targets);
        Targets = _targets;

        var selectedSet = new HashSet<string>(preselectedNames, StringComparer.OrdinalIgnoreCase);
        _deviceOptions = new ObservableCollection<BbbBlockHiddenDeviceNameOption>(
            deviceNames.Select(x => new BbbBlockHiddenDeviceNameOption
            {
                Name = x,
                IsSelected = selectedSet.Contains(x)
            }));
        for (var i = 0; i < _deviceOptions.Count; i++)
            _deviceOptionOrder[_deviceOptions[i]] = i;
        foreach (var option in _deviceOptions)
            option.PropertyChanged += OnOptionPropertyChanged;

        _deviceOptionsView = CollectionViewSource.GetDefaultView(_deviceOptions);
        _deviceOptionsView.Filter = FilterDeviceOption;
        if (_deviceOptionsView is ListCollectionView deviceOptionsListView)
            deviceOptionsListView.CustomSort = new DeviceOptionSearchComparer(this);

        DeviceOptionsView = _deviceOptionsView;
        BlockSummaryText = ordinaryBlockCount > 0
            ? $"{_targets.Count} 个将处理，其中普通块 {ordinaryBlockCount} 个"
            : $"{_targets.Count} 个将处理";
        DeviceSummaryText = $"{_deviceOptions.Count} 条可选";

        DataContext = this;
        UpdateFooterStatus();
        Closed += OnClosed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BbbBlockHiddenDeviceNameTarget> Targets { get; }

    public ICollectionView DeviceOptionsView { get; }

    public string WorkbookPath { get; }

    public string WorksheetName { get; }

    public string ColumnHeader { get; }

    public string BlockSummaryText { get; }

    public string DeviceSummaryText { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;

            _searchText = value;
            OnPropertyChanged(nameof(SearchText));
            _deviceOptionsView.Refresh();
            UpdateFooterStatus();
        }
    }

    public string FooterStatusText
    {
        get => _footerStatusText;
        private set
        {
            if (_footerStatusText == value)
                return;

            _footerStatusText = value;
            OnPropertyChanged(nameof(FooterStatusText));
        }
    }

    internal IReadOnlyList<string> SelectedDeviceNames =>
        _deviceOptions
            .Where(x => x.IsSelected)
            .Select(x => x.Name)
            .ToList();

    private bool FilterDeviceOption(object item)
    {
        if (item is not BbbBlockHiddenDeviceNameOption option)
            return false;

        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        return SearchTextCompat.ContainsFuzzy(option.Name, SearchText);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this);
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DialogWindowPlacementHelper.TrySavePlacement(this);
    }

    private void SearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _deviceOptionsView.Refresh();
        UpdateFooterStatus();
    }

    private void SelectFilteredButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _deviceOptionsView.Cast<object>().OfType<BbbBlockHiddenDeviceNameOption>())
            item.IsSelected = true;

        UpdateFooterStatus();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _deviceOptions)
            item.IsSelected = false;

        UpdateFooterStatus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BbbBlockHiddenDeviceNameOption.IsSelected))
            UpdateFooterStatus();
    }

    private void UpdateFooterStatus()
    {
        var selectedCount = _deviceOptions.Count(x => x.IsSelected);
        var visibleCount = _deviceOptionsView.Cast<object>().Count();
        FooterStatusText = $"已勾选 {selectedCount} 条设备名称；当前筛选显示 {visibleCount} / {_deviceOptions.Count} 条。";
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class DeviceOptionSearchComparer : IComparer
    {
        private readonly BbbBlockHiddenDeviceNameWindow _owner;

        public DeviceOptionSearchComparer(BbbBlockHiddenDeviceNameWindow owner) => _owner = owner;

        public int Compare(object? x, object? y)
        {
            if (x is not BbbBlockHiddenDeviceNameOption a || y is not BbbBlockHiddenDeviceNameOption b)
                return 0;

            var searchText = _owner._searchText;
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var ra = SearchTextCompat.GetFuzzyMatchRank(a.Name, searchText);
                var rb = SearchTextCompat.GetFuzzyMatchRank(b.Name, searchText);
                if (ra != rb)
                    return ra.CompareTo(rb);
            }

            return _owner.GetOriginalDeviceOptionIndex(a).CompareTo(_owner.GetOriginalDeviceOptionIndex(b));
        }
    }

    private int GetOriginalDeviceOptionIndex(BbbBlockHiddenDeviceNameOption option) =>
        _deviceOptionOrder.TryGetValue(option, out var index) ? index : int.MaxValue;
}
