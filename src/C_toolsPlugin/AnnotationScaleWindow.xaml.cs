using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsShared;

namespace C_toolsPlugin;

public partial class AnnotationScaleWindow : Window, IModelessWindowPlacement, IModelessWindowHostAware
{
    private const double FixedWindowPixelWidth = 600d;
    private const double FixedWindowPixelHeight = 1080d;
    private readonly ObservableCollection<AnnotationScaleGroupInfo> _groups = new();
    private readonly ObservableCollection<AnnotationScaleListItem> _normalScales = new();
    private readonly ObservableCollection<AnnotationScaleListItem> _innerScales = new();
    private List<AnnotationScaleListItem> _allScales = new();
    private bool _isApplyingScale;
    private bool _isRefreshing;
    private bool _refreshPending;
    private bool _initialRefreshQueued;
    private DateTime _lastRefreshRequestUtc = DateTime.MinValue;
    private bool _suppressGroupSelectionChanged;
    private bool _suppressScaleSelectionChanged;

    public string PlacementKey => "C_TOOL_AnnotationScale";

    public AnnotationScaleWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            ApplyFixedWindowSize();
            WindowTitleBarHelper.TryApplyDarkTitleBar(this, applyCaptionColorToBorder: true);
        };
        ApplyFixedWindowSize();
        GroupCombo.ItemsSource = _groups;
        NormalScaleList.ItemsSource = _normalScales;
        InnerScaleList.ItemsSource = _innerScales;
        Loaded += OnLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    internal void BringToFront()
    {
        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            if (Visibility == Visibility.Visible)
            {
                Activate();
                Dispatcher.BeginInvoke(new Action(FocusPrimaryInput), DispatcherPriority.Background);
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("激活 F_DE 标注比例浮窗失败（无效操作）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("激活 F_DE 标注比例浮窗失败", ex);
        }
    }

    internal void RefreshFromActiveDocument()
    {
        if (_isApplyingScale)
        {
            _refreshPending = true;
            return;
        }

        if (_isRefreshing)
        {
            _refreshPending = true;
            return;
        }

        _refreshPending = false;
        _isRefreshing = true;
        UpdateControlState();
        SetStatus("正在读取当前图纸标注比例...");

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var snapshot = ViewportCommandService.ReadAnnotationScaleSnapshot(out var message);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            ApplySnapshot(snapshot, message);
                        }
                        catch (Exception ex)
                        {
                            C_toolsDiagnostics.LogNonFatal("应用 F_DE 标注比例快照失败", ex);
                            SetStatus("刷新失败：" + ex.Message);
                        }
                        finally
                        {
                            FinishRefresh();
                        }
                    }));
                },
                null);
        }
        catch (InvalidOperationException ex)
        {
            _isRefreshing = false;
            UpdateControlState();
            C_toolsDiagnostics.LogNonFatal("刷新 F_DE 标注比例浮窗失败（无效操作）", ex);
            SetStatus("刷新失败：" + ex.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            _isRefreshing = false;
            UpdateControlState();
            C_toolsDiagnostics.LogNonFatal("刷新 F_DE 标注比例浮窗失败（CAD）", ex);
            SetStatus("刷新失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            _isRefreshing = false;
            UpdateControlState();
            C_toolsDiagnostics.LogNonFatal("刷新 F_DE 标注比例浮窗失败", ex);
            SetStatus("刷新失败：" + ex.Message);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyFixedWindowSize();

        try
        {
            var owner = AcAp.MainWindow?.Handle ?? IntPtr.Zero;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(this) { Owner = owner };
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("设置 F_DE 标注比例浮窗 Owner", ex);
        }

        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this);
        Dispatcher.BeginInvoke(new Action(FocusPrimaryInput), DispatcherPriority.Background);
        QueueInitialRefresh();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DialogWindowPlacementHelper.TrySavePlacement(this);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (!IsVisible || !IsLoaded)
            return;

        if (!_initialRefreshQueued)
        {
            QueueInitialRefresh();
            return;
        }

        if (_lastRefreshRequestUtc == DateTime.MinValue)
            return;

        RefreshFromActiveDocumentThrottled();
    }

    private void QueueInitialRefresh()
    {
        if (_initialRefreshQueued)
            return;

        _initialRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(RefreshFromActiveDocumentThrottled), DispatcherPriority.ContextIdle);
    }

    private void RefreshFromActiveDocumentThrottled()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRefreshRequestUtc).TotalMilliseconds < 750)
            return;

        _lastRefreshRequestUtc = now;
        RefreshFromActiveDocument();
    }

    private void ApplySnapshot(AnnotationScaleSnapshot snapshot, string message)
    {
        _allScales = new List<AnnotationScaleListItem>(snapshot.AllScales);
        CurrentScaleText.Text = string.IsNullOrWhiteSpace(snapshot.CurrentScaleName)
            ? "（未设置）"
            : GetScaleDisplayText(snapshot.CurrentScaleName);

        RefreshGroupList(snapshot.Groups, snapshot.CurrentScaleName);
        UpdateControlState();
        SetStatus(message);
    }

    private void FinishRefresh()
    {
        _isRefreshing = false;
        UpdateControlState();
        RequestPendingRefreshIfNeeded();
    }

    private void RefreshGroupList(
        IReadOnlyList<AnnotationScaleGroupInfo> sourceGroups,
        string currentScaleName)
    {
        var previousPrefix = AnnotationScaleGrouping.NormalizeGroupPrefix(
            (GroupCombo.SelectedItem as AnnotationScaleGroupInfo)?.Prefix);
        var configuredPrefix = ResolveConfiguredGroupPrefix();
        var savedPrefix = AnnotationScaleGrouping.NormalizeGroupPrefix(AnnotationScaleLastGroupStore.TryGetPrefix());

        _suppressGroupSelectionChanged = true;
        try
        {
            _groups.Clear();
            foreach (var group in sourceGroups)
                _groups.Add(group);

            if (_groups.Count == 0)
            {
                GroupCombo.SelectedItem = null;
                RefreshScaleLists(null, currentScaleName);
                return;
            }

            var selectedGroup = FindGroup(previousPrefix)
                                ?? FindGroup(configuredPrefix)
                                ?? FindGroup(savedPrefix)
                                ?? FindGroupContainingScale(currentScaleName)
                                ?? _groups[0];
            GroupCombo.SelectedItem = selectedGroup;
            RefreshScaleLists(selectedGroup, currentScaleName);
        }
        finally
        {
            _suppressGroupSelectionChanged = false;
        }
    }

    private void UpdateControlState()
    {
        var isBusy = _isApplyingScale || _isRefreshing;
        GroupCombo.IsEnabled = !isBusy && _groups.Count > 0;
        NormalScaleList.IsEnabled = !isBusy && _normalScales.Count > 0;
        InnerScaleList.IsEnabled = !isBusy && _innerScales.Count > 0;
    }

    private static string? ResolveConfiguredGroupPrefix()
    {
        var configuredStyleName = UserConfigurationStore.TryGetDimStyleName();
        var stylePrefix = AnnotationScaleGrouping.NormalizeGroupPrefix(
            AnnotationScaleGrouping.GetBasePrefix(configuredStyleName));
        if (!string.IsNullOrWhiteSpace(stylePrefix))
            return stylePrefix;

        return AnnotationScaleGrouping.NormalizeGroupPrefix(UserConfigurationStore.TryGetDimStyleGroupPrefix());
    }

    private AnnotationScaleGroupInfo? FindGroup(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        foreach (var group in _groups)
        {
            if (string.Equals(group.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                return group;
        }

        return null;
    }

    private AnnotationScaleGroupInfo? FindGroupContainingScale(string? scaleName)
    {
        if (string.IsNullOrWhiteSpace(scaleName))
            return null;

        foreach (var group in _groups)
        {
            foreach (var item in group.NormalScales)
            {
                if (item.MatchesScale(scaleName))
                    return group;
            }

            foreach (var item in group.InnerScales)
            {
                if (item.MatchesScale(scaleName))
                    return group;
            }
        }

        return null;
    }

    private void RefreshScaleLists(AnnotationScaleGroupInfo? group, string? selectedScaleName)
    {
        _suppressScaleSelectionChanged = true;
        try
        {
            _normalScales.Clear();
            _innerScales.Clear();

            if (group != null)
            {
                foreach (var item in group.NormalScales)
                    _normalScales.Add(item);
                foreach (var item in group.InnerScales)
                    _innerScales.Add(item);
            }

            SelectScale(selectedScaleName);
            UpdateColumnHeaders();
            UpdateControlState();
        }
        finally
        {
            _suppressScaleSelectionChanged = false;
        }
    }

    private void UpdateColumnHeaders()
    {
        NormalScaleHeaderText.Text = $"外尺寸标注（{_normalScales.Count}）";
        InnerScaleHeaderText.Text = $"内尺寸标注（{_innerScales.Count}）";
    }

    private void SelectScale(string? scaleName)
    {
        _suppressScaleSelectionChanged = true;
        try
        {
            if (string.IsNullOrWhiteSpace(scaleName))
            {
                NormalScaleList.SelectedItem = null;
                InnerScaleList.SelectedItem = null;
                return;
            }

            foreach (var normalScale in _normalScales)
            {
                if (normalScale.MatchesScale(scaleName))
                {
                    NormalScaleList.SelectedItem = normalScale;
                    InnerScaleList.SelectedItem = null;
                    NormalScaleList.ScrollIntoView(normalScale);
                    return;
                }
            }

            foreach (var innerScale in _innerScales)
            {
                if (innerScale.MatchesScale(scaleName))
                {
                    InnerScaleList.SelectedItem = innerScale;
                    NormalScaleList.SelectedItem = null;
                    InnerScaleList.ScrollIntoView(innerScale);
                    return;
                }
            }

            NormalScaleList.SelectedItem = null;
            InnerScaleList.SelectedItem = null;
        }
        finally
        {
            _suppressScaleSelectionChanged = false;
        }
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGroupSelectionChanged)
            return;

        if (GroupCombo.SelectedItem is AnnotationScaleGroupInfo group)
            AnnotationScaleLastGroupStore.Save(group.Prefix);

        RefreshScaleLists(GroupCombo.SelectedItem as AnnotationScaleGroupInfo, CurrentScaleText.Text);
    }

    private void NormalScaleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HandleScaleSelectionChanged(sender as ListBox, preferInnerDimStyle: false);
    }

    private void InnerScaleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HandleScaleSelectionChanged(sender as ListBox, preferInnerDimStyle: true);
    }

    private void HandleScaleSelectionChanged(ListBox? sourceList, bool preferInnerDimStyle)
    {
        if (_suppressScaleSelectionChanged || _isApplyingScale || _isRefreshing)
            return;
        if (sourceList?.SelectedItem is not AnnotationScaleListItem selectedScale)
            return;

        var selectedGroupPrefix = (GroupCombo.SelectedItem as AnnotationScaleGroupInfo)?.Prefix;
        _suppressScaleSelectionChanged = true;
        try
        {
            if (preferInnerDimStyle)
                NormalScaleList.SelectedItem = null;
            else
                InnerScaleList.SelectedItem = null;
        }
        finally
        {
            _suppressScaleSelectionChanged = false;
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                ApplySelectedScale(
                    selectedScale,
                    selectedGroupPrefix,
                    preferInnerDimStyle,
                    closeOnSuccess: true);
            }),
            DispatcherPriority.Background);
    }

    private void ScaleList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender is not ListBox sourceList || sourceList.SelectedItem is not AnnotationScaleListItem selectedScale)
            return;

        var preferInnerDimStyle = ReferenceEquals(sourceList, InnerScaleList);
        var selectedGroupPrefix = (GroupCombo.SelectedItem as AnnotationScaleGroupInfo)?.Prefix;
        ApplySelectedScale(selectedScale, selectedGroupPrefix, preferInnerDimStyle, closeOnSuccess: true);
        e.Handled = true;
    }

    private void ApplySelectedScale(
        AnnotationScaleListItem? selected,
        string? selectedGroupPrefix,
        bool preferInnerDimStyle,
        bool closeOnSuccess)
    {
        if (selected == null)
        {
            SetStatus("请先选择一个有效的标注比例。");
            FocusPrimaryInput();
            return;
        }

        if (_isApplyingScale)
            return;

        _isApplyingScale = true;
        UpdateControlState();
        SetStatus("正在切换比例并应用当前标注样式...");

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var ok = ViewportCommandService.TryApplyAnnotationScale(
                        selected.Name,
                        selectedGroupPrefix,
                        preferInnerDimStyle,
                        out var appliedScaleName,
                        out var message);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _isApplyingScale = false;
                            var shouldClose = ok && closeOnSuccess;
                            if (ok)
                            {
                                CurrentScaleText.Text = GetScaleDisplayText(appliedScaleName);
                                SelectScale(appliedScaleName);
                            }

                            UpdateControlState();
                            SetStatus(message);
                            RequestPendingRefreshIfNeeded();
                            if (shouldClose)
                                Close();
                        }
                        catch (Exception ex)
                        {
                            _isApplyingScale = false;
                            UpdateControlState();
                            C_toolsDiagnostics.LogNonFatal("回写 F_DE 应用结果失败", ex);
                            SetStatus("应用失败：" + ex.Message);
                            RequestPendingRefreshIfNeeded();
                        }
                    }));
                },
                null);
        }
        catch (InvalidOperationException ex)
        {
            _isApplyingScale = false;
            UpdateControlState();
            C_toolsDiagnostics.LogNonFatal("F_DE 应用标注比例失败（无效操作）", ex);
            SetStatus("应用失败：" + ex.Message);
            RequestPendingRefreshIfNeeded();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            _isApplyingScale = false;
            UpdateControlState();
            C_toolsDiagnostics.LogNonFatal("F_DE 应用标注比例失败（CAD）", ex);
            SetStatus("应用失败：" + ex.Message);
            RequestPendingRefreshIfNeeded();
        }
        catch (Exception ex)
        {
            _isApplyingScale = false;
            UpdateControlState();
            C_toolsDiagnostics.LogNonFatal("F_DE 应用标注比例失败", ex);
            SetStatus("应用失败：" + ex.Message);
            RequestPendingRefreshIfNeeded();
        }
    }

    private void FocusPrimaryInput()
    {
        if (NormalScaleList.IsEnabled)
        {
            NormalScaleList.Focus();
            if (NormalScaleList.SelectedItem != null)
                NormalScaleList.ScrollIntoView(NormalScaleList.SelectedItem);
            return;
        }

        if (InnerScaleList.IsEnabled)
        {
            InnerScaleList.Focus();
            if (InnerScaleList.SelectedItem != null)
                InnerScaleList.ScrollIntoView(InnerScaleList.SelectedItem);
            return;
        }

        if (GroupCombo.IsEnabled)
            GroupCombo.Focus();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(message)
            ? "选择比例后会直接切换当前标注样式（不改写样式参数）。"
            : message;
    }

    public void OnHostShowing()
    {
        ApplyFixedWindowSize();
        if (_initialRefreshQueued)
            RefreshFromActiveDocumentThrottled();
        else
            QueueInitialRefresh();
    }

    public void OnHostHiding()
    {
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

    private string GetScaleDisplayText(string? scaleName)
    {
        var value = scaleName?.Trim() ?? "";
        if (value.Length == 0)
            return "（未设置）";

        foreach (var scale in _allScales)
        {
            if (scale.MatchesScale(value))
                return scale.ListDisplay;
        }

        return value;
    }

    private void RequestPendingRefreshIfNeeded()
    {
        if (!_refreshPending || _isApplyingScale || _isRefreshing || !IsVisible)
            return;

        _refreshPending = false;
        Dispatcher.BeginInvoke(new Action(RefreshFromActiveDocument), DispatcherPriority.Background);
    }
}
