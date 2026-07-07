using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace C_toolsPlugin;

internal sealed class WallFinishSettingsWindow : Window
{
    private const int DwAttributeUseImmersiveDarkMode = 20;
    private const int DwAttributeUseImmersiveDarkModeLegacy = 19;
    private const int DwAttributeCaptionColor = 35;
    private const int DwAttributeTextColor = 36;
    private static readonly Style s_cadToolbarComboBoxStyle = CadThemeStyleProvider.CadToolbarComboBoxStyle;
    private readonly string _autoLayerDisplayName;
    private readonly string _currentModeName;
    private readonly TextBox _offsetDistanceBox;
    private readonly TextBox _colorIndexBox;
    private readonly ComboBox _targetLayerBox;
    private readonly TextBlock _statusText;

    internal WallFinishSettingsWindow(
        WallFinishSettingsDto currentSettings,
        IReadOnlyList<string> targetLayerOptions,
        string autoLayerDisplayName)
    {
        _autoLayerDisplayName = autoLayerDisplayName;
        _currentModeName = currentSettings.Mode ?? "";

        Title = "完成面设置";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 500;
        MinHeight = 280;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = CadDialogControlFactory.Brush("#232830");
        Foreground = System.Windows.Media.Brushes.White;

        _targetLayerBox = CreateComboBox(BuildLayerItems(targetLayerOptions));
        if (!string.IsNullOrWhiteSpace(currentSettings.TargetLayerName))
            CadDialogValueHelper.SelectComboValue(_targetLayerBox, currentSettings.TargetLayerName, "");
        else
            _targetLayerBox.SelectedIndex = 0;

        _offsetDistanceBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.OffsetDistance.ToString("0.###", CultureInfo.InvariantCulture));

        _colorIndexBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.ColorIndex?.ToString(CultureInfo.InvariantCulture) ?? "");

        _statusText = new TextBlock
        {
            Text = "填充样式选用图层说明中标注【完成面填充】的拾取样式",
            Foreground = CadDialogControlFactory.Brush("#F0C674"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Content = BuildContent();
    }

    internal WallFinishSettingsDto? SavedSettings { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryApplyDarkTitleBar();
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
        panel.Children.Add(CadDialogControlFactory.CreateField("偏移量", _offsetDistanceBox));
        panel.Children.Add(CreateLayerAndColorRow());
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

    private IReadOnlyList<string> BuildLayerItems(IReadOnlyList<string> targetLayerOptions)
    {
        var items = new List<string>(targetLayerOptions.Count + 1)
        {
            _autoLayerDisplayName
        };

        for (var i = 0; i < targetLayerOptions.Count; i++)
        {
            var name = (targetLayerOptions[i] ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, _autoLayerDisplayName, StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(name);
        }

        return items;
    }

    private Border CreateLayerAndColorRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var layerPanel = CreateInlineField("完成面图层", _targetLayerBox);
        Grid.SetColumn(layerPanel, 0);
        grid.Children.Add(layerPanel);

        var colorPanel = CreateInlineField("图层颜色(ACI)", _colorIndexBox);
        Grid.SetColumn(colorPanel, 2);
        grid.Children.Add(colorPanel);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
            Child = grid
        };
    }

    private static StackPanel CreateInlineField(string label, UIElement input)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = CadDialogControlFactory.Brush("#A7B1BC"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(input);
        return panel;
    }

    private static ComboBox CreateComboBox(IReadOnlyList<string> items)
    {
        return new ComboBox
        {
            Height = 32,
            IsEditable = false,
            ItemsSource = items,
            Style = s_cadToolbarComboBoxStyle
        };
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

    private bool TryBuildSettings(out WallFinishSettingsDto dto, out string error)
    {
        dto = new WallFinishSettingsDto();
        error = "";

        if (!CadDialogValueHelper.TryParsePositiveDouble(_offsetDistanceBox.Text, out var offsetDistance))
        {
            error = "偏移量必须是大于 0 的数字。";
            return false;
        }

        int? colorIndex = null;
        var colorText = (_colorIndexBox.Text ?? "").Trim();
        if (colorText.Length > 0)
        {
            colorIndex = LayerStyleHelper.TryParseAciColor(colorText);
            if (!colorIndex.HasValue)
            {
                error = "图层颜色请留空，或填写 1-255 的 ACI 颜色索引。";
                return false;
            }
        }

        var selectedLayer = ((_targetLayerBox.SelectedItem as string) ?? "").Trim();

        dto = new WallFinishSettingsDto
        {
            OffsetDistance = offsetDistance,
            ColorIndex = colorIndex,
            TargetLayerName = string.Equals(selectedLayer, _autoLayerDisplayName, StringComparison.OrdinalIgnoreCase)
                ? ""
                : selectedLayer,
            Mode = _currentModeName
        };
        return true;
    }

    private void TryApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            SetDwmIntAttribute(handle, DwAttributeUseImmersiveDarkMode, 1);
            SetDwmIntAttribute(handle, DwAttributeUseImmersiveDarkModeLegacy, 1);
            SetDwmIntAttribute(handle, DwAttributeCaptionColor, ToColorRef(0x23, 0x28, 0x30));
            SetDwmIntAttribute(handle, DwAttributeTextColor, ToColorRef(0xE6, 0xE8, 0xEA));
        }
        catch
        {
            // 标题栏颜色为增强项；当前系统不支持时忽略即可。
        }
    }

    private static void SetDwmIntAttribute(IntPtr hwnd, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, Marshal.SizeOf<int>());
    }

    private static int ToColorRef(byte r, byte g, byte b) =>
        r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
