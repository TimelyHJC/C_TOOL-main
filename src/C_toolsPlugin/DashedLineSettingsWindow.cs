using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using C_toolsShared;

namespace C_toolsPlugin;

internal sealed class DashedLineSettingsWindow : Window
{
    private const string KeepOriginalColorDisplay = "保持原颜色";
    private const string ByLayerColorDisplay = "ByLayer";
    private const string ByBlockColorDisplay = "ByBlock";
    private const string AciColorDisplay = "指定 ACI 颜色";
    private const string KeepOriginalLayerDisplay = "（保持原图层）";

    private static readonly Style s_cadToolbarComboBoxStyle = CadThemeStyleProvider.CadToolbarComboBoxStyle;
    private readonly ComboBox _colorModeBox;
    private readonly TextBox _colorValueBox;
    private readonly ComboBox _layerBox;
    private readonly ComboBox _linetypeBox;
    private readonly TextBox _linetypeScaleBox;
    private readonly Func<Window, DashedLineStylePickResult>? _pickLineStyle;
    private readonly TextBlock _statusText;

    internal DashedLineSettingsWindow(
        DashedLineSettingsDto currentSettings,
        IReadOnlyList<string> linetypeOptions,
        IReadOnlyList<string> layerOptions,
        Func<Window, DashedLineStylePickResult>? pickLineStyle = null)
    {
        Title = "F_XG 线型设置";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 380;
        MinHeight = 320;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = CadDialogControlFactory.Brush("#232830");
        Foreground = System.Windows.Media.Brushes.White;
        _pickLineStyle = pickLineStyle;

        _linetypeBox = CreateEditableComboBox(linetypeOptions);
        _linetypeBox.Text = (currentSettings.LinetypeName ?? "").Trim();

        _linetypeScaleBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.LinetypeScale.ToString("0.###", CultureInfo.InvariantCulture));

        _colorValueBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.ColorIndex?.ToString(CultureInfo.InvariantCulture) ?? "7");

        _colorModeBox = CreateFixedComboBox(
            KeepOriginalColorDisplay,
            ByLayerColorDisplay,
            ByBlockColorDisplay,
            AciColorDisplay);
        _colorModeBox.SelectionChanged += (_, _) => UpdateColorValueState();
        SelectColorMode(currentSettings);

        var layerItems = new List<string> { KeepOriginalLayerDisplay };
        layerItems.AddRange(layerOptions);
        _layerBox = CreateEditableComboBox(layerItems);
        _layerBox.Text = string.IsNullOrWhiteSpace(currentSettings.TargetLayerName)
            ? KeepOriginalLayerDisplay
            : currentSettings.TargetLayerName.Trim();

        _statusText = new TextBlock
        {
            Text = "线型和线型比例会应用到所选线对象。颜色可选保持原颜色、ByLayer、ByBlock 或指定 ACI 1-255；图层可保持原图层，也可以输入新图层名自动创建。",
            Foreground = CadDialogControlFactory.Brush("#F0C674"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };

        UpdateColorValueState();
        Loaded += OnLoaded;
        Closed += OnClosed;
        Content = BuildContent();
    }

    internal DashedLineSettingsDto? SavedSettings { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTitleBarHelper.TryApplyDarkTitleBar(this);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DialogWindowPlacementHelper.TrySavePlacement(this);
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel
        {
            Margin = new Thickness(16)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var cancelButton = CadDialogControlFactory.CreateActionButton("取消", isPrimary: false);
        cancelButton.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        var okButton = CadDialogControlFactory.CreateActionButton("确定", isPrimary: true);
        okButton.Click += (_, _) => ConfirmAndClose();

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        var panel = new StackPanel();
        panel.Children.Add(CadDialogControlFactory.CreateField("线型", CreateLinetypePickerRow()));
        panel.Children.Add(CadDialogControlFactory.CreateField("线型比例", _linetypeScaleBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("线段颜色", _colorModeBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("ACI 颜色值", _colorValueBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("线段图层", _layerBox));
        panel.Children.Add(_statusText);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };

        root.Children.Add(buttons);
        root.Children.Add(scrollViewer);
        return root;
    }

    private UIElement CreateLinetypePickerRow()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_linetypeBox, 0);
        row.Children.Add(_linetypeBox);

        var pickButton = new Button
        {
            Content = "选择",
            Width = 68,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Background = CadDialogControlFactory.Brush("#2E3640"),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = CadDialogControlFactory.Brush("#56616C"),
            BorderThickness = new Thickness(1),
            ToolTip = "从 CAD 中选择线/多段线，并读取线型和线型比例",
            IsEnabled = _pickLineStyle != null
        };
        pickButton.Click += PickLineStyle_Click;

        Grid.SetColumn(pickButton, 1);
        row.Children.Add(pickButton);
        return row;
    }

    private static ComboBox CreateEditableComboBox(IReadOnlyList<string> items)
    {
        return new ComboBox
        {
            Height = 32,
            IsEditable = true,
            IsTextSearchEnabled = true,
            StaysOpenOnEdit = true,
            ItemsSource = items,
            Style = s_cadToolbarComboBoxStyle
        };
    }

    private static ComboBox CreateFixedComboBox(params string[] items)
    {
        return new ComboBox
        {
            Height = 32,
            IsEditable = false,
            ItemsSource = items,
            SelectedIndex = 0,
            Style = s_cadToolbarComboBoxStyle
        };
    }

    private void SelectColorMode(DashedLineSettingsDto settings)
    {
        var display = ResolveColorModeDisplay(settings.ColorMode);
        CadDialogValueHelper.SelectComboValue(_colorModeBox, display, KeepOriginalColorDisplay);
    }

    private static string ResolveColorModeDisplay(string? colorMode)
    {
        var normalized = (colorMode ?? "").Trim();
        if (string.Equals(normalized, DashedLineColorModes.ByLayer, StringComparison.OrdinalIgnoreCase))
            return ByLayerColorDisplay;
        if (string.Equals(normalized, DashedLineColorModes.ByBlock, StringComparison.OrdinalIgnoreCase))
            return ByBlockColorDisplay;
        if (string.Equals(normalized, DashedLineColorModes.Aci, StringComparison.OrdinalIgnoreCase))
            return AciColorDisplay;
        return KeepOriginalColorDisplay;
    }

    private void UpdateColorValueState()
    {
        var selectedDisplay = (_colorModeBox.SelectedItem as string) ?? KeepOriginalColorDisplay;
        var isAci = string.Equals(selectedDisplay, AciColorDisplay, StringComparison.Ordinal);
        _colorValueBox.IsEnabled = isAci;
        _colorValueBox.Opacity = isAci ? 1.0 : 0.6;
    }

    private void PickLineStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_pickLineStyle == null)
            return;

        var result = _pickLineStyle(this);
        if (result.Succeeded)
        {
            _linetypeBox.Text = result.LinetypeName;
            _linetypeScaleBox.Text = result.LinetypeScale.ToString("0.####", CultureInfo.InvariantCulture);
            _statusText.Text = $"已读取线型 [{result.LinetypeName}]，比例 [{result.LinetypeScale.ToString("0.####", CultureInfo.InvariantCulture)}]。";
            _statusText.Foreground = CadDialogControlFactory.Brush("#98C379");
            return;
        }

        _statusText.Text = result.ErrorMessage;
        _statusText.Foreground = result.Cancelled
            ? CadDialogControlFactory.Brush("#F0C674")
            : CadDialogControlFactory.Brush("#E06C75");
    }

    private void ConfirmAndClose()
    {
        if (!TryBuildSettings(out var dto, out var error))
        {
            _statusText.Text = error;
            _statusText.Foreground = CadDialogControlFactory.Brush("#E06C75");
            return;
        }

        SavedSettings = dto;
        DialogResult = true;
        Close();
    }

    private bool TryBuildSettings(out DashedLineSettingsDto dto, out string error)
    {
        dto = new DashedLineSettingsDto();
        error = "";

        var linetypeName = (_linetypeBox.Text ?? "").Trim();
        if (linetypeName.Length == 0)
        {
            error = "线型不能为空。";
            return false;
        }

        if (!CadDialogValueHelper.TryParsePositiveDouble(_linetypeScaleBox.Text, out var linetypeScale))
        {
            error = "线型比例必须是大于 0 的数字。";
            return false;
        }

        var colorDisplay = (_colorModeBox.SelectedItem as string) ?? KeepOriginalColorDisplay;
        var colorMode = DashedLineColorModes.Keep;
        int? colorIndex = null;

        if (string.Equals(colorDisplay, ByLayerColorDisplay, StringComparison.Ordinal))
        {
            colorMode = DashedLineColorModes.ByLayer;
        }
        else if (string.Equals(colorDisplay, ByBlockColorDisplay, StringComparison.Ordinal))
        {
            colorMode = DashedLineColorModes.ByBlock;
        }
        else if (string.Equals(colorDisplay, AciColorDisplay, StringComparison.Ordinal))
        {
            colorMode = DashedLineColorModes.Aci;
            colorIndex = LayerStyleHelper.TryParseAciColor(_colorValueBox.Text);
            if (!colorIndex.HasValue)
            {
                error = "ACI 颜色必须填写 1-255。";
                return false;
            }
        }

        var targetLayerName = (_layerBox.Text ?? "").Trim();
        if (string.Equals(targetLayerName, KeepOriginalLayerDisplay, StringComparison.Ordinal))
            targetLayerName = "";

        dto = new DashedLineSettingsDto
        {
            LinetypeName = linetypeName,
            LinetypeScale = linetypeScale,
            ColorMode = colorMode,
            ColorIndex = colorIndex,
            TargetLayerName = targetLayerName
        };
        return true;
    }
}

internal readonly struct DashedLineStylePickResult
{
    private DashedLineStylePickResult(
        bool succeeded,
        bool cancelled,
        string linetypeName,
        double linetypeScale,
        string errorMessage)
    {
        Succeeded = succeeded;
        Cancelled = cancelled;
        LinetypeName = linetypeName;
        LinetypeScale = linetypeScale;
        ErrorMessage = errorMessage;
    }

    internal bool Succeeded { get; }

    internal bool Cancelled { get; }

    internal string LinetypeName { get; }

    internal double LinetypeScale { get; }

    internal string ErrorMessage { get; }

    internal static DashedLineStylePickResult Success(string linetypeName, double linetypeScale) =>
        new(true, false, linetypeName, linetypeScale, "");

    internal static DashedLineStylePickResult CancelledResult(string message = "未选择线/多段线对象。") =>
        new(false, true, "", 1.0, message);

    internal static DashedLineStylePickResult Failure(string message) =>
        new(false, false, "", 1.0, message);
}
