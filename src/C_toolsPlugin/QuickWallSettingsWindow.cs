using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using C_toolsShared;

namespace C_toolsPlugin;

internal sealed class QuickWallSettingsWindow : Window
{
    private static readonly Style s_cadToolbarComboBoxStyle = CadThemeStyleProvider.CadToolbarComboBoxStyle;
    private readonly ComboBox _wallLayerBox;
    private readonly TextBox _colorIndexBox;
    private readonly bool _hadInitialSecondaryWidth;
    private readonly bool _initialUseSecondaryWidth;
    private readonly TextBox _primaryWidthBox;
    private readonly TextBox _secondaryWidthBox;
    private readonly TextBlock _statusText;

    internal QuickWallSettingsWindow(
        QuickWallSettingsDto currentSettings,
        IReadOnlyList<string> wallLayerOptions,
        string fallbackWallLayer)
    {
        Title = "F_SQT 墙体设置";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 360;
        MinHeight = 320;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = CadDialogControlFactory.Brush("#232830");
        Foreground = System.Windows.Media.Brushes.White;

        _wallLayerBox = CreateComboBox(wallLayerOptions);
        CadDialogValueHelper.SelectComboValue(
            _wallLayerBox,
            string.IsNullOrWhiteSpace(currentSettings.WallLayerName)
                ? fallbackWallLayer
                : currentSettings.WallLayerName,
            fallbackWallLayer);

        _hadInitialSecondaryWidth = currentSettings.SecondaryWidth.HasValue && currentSettings.SecondaryWidth.Value > 0;
        _initialUseSecondaryWidth = currentSettings.UseSecondaryWidth;

        _colorIndexBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.ColorIndex?.ToString(CultureInfo.InvariantCulture) ?? "");

        _primaryWidthBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.PrimaryWidth.ToString("0.###", CultureInfo.InvariantCulture));

        _secondaryWidthBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.SecondaryWidth.HasValue
                ? currentSettings.SecondaryWidth.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "");

        _statusText = new TextBlock
        {
            Text = "填充样式选用图层说明中标注【新隔墙填充】的拾取样式。颜色留空时墙线按 C_TOOL 图层颜色；填写 ACI 1-255 时只覆盖墙体线颜色，填充颜色仍按 C_TOOL 拾取结果。第二宽度留空时按单层墙体处理；填写后双层会沿第一层外侧继续偏移生成第二个封闭图形，单层则按两项中的较大宽度生成。运行中可按 X 切换单层/双层，按 F 互换宽度和第二宽度。",
            Foreground = CadDialogControlFactory.Brush("#F0C674"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Content = BuildContent();
    }

    internal QuickWallSettingsDto? SavedSettings { get; private set; }

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
        panel.Children.Add(CadDialogControlFactory.CreateField("墙体线图层", _wallLayerBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("颜色(ACI)", _colorIndexBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("宽度", _primaryWidthBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("第二宽度", _secondaryWidthBox));
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

    private bool TryBuildSettings(out QuickWallSettingsDto dto, out string error)
    {
        dto = new QuickWallSettingsDto();
        error = "";

        var wallLayer = ((_wallLayerBox.SelectedItem as string) ?? "").Trim();
        if (wallLayer.Length == 0)
        {
            error = "墙体线图层不能为空。";
            return false;
        }

        int? colorIndex = null;
        var colorText = (_colorIndexBox.Text ?? "").Trim();
        if (colorText.Length > 0)
        {
            colorIndex = LayerStyleHelper.TryParseAciColor(colorText);
            if (!colorIndex.HasValue)
            {
                error = "颜色请留空，或填写 1-255 的 ACI 颜色索引。";
                return false;
            }
        }

        if (!CadDialogValueHelper.TryParsePositiveDouble(_primaryWidthBox.Text, out var primaryWidth))
        {
            error = "宽度必须是大于 0 的数字。";
            return false;
        }

        double? secondaryWidth = null;
        var secondaryText = (_secondaryWidthBox.Text ?? "").Trim();
        if (secondaryText.Length > 0)
        {
            if (!CadDialogValueHelper.TryParsePositiveDouble(secondaryText, out var parsedSecondary))
            {
                error = "第二宽度必须是大于 0 的数字。";
                return false;
            }

            secondaryWidth = parsedSecondary;
        }

        dto = new QuickWallSettingsDto
        {
            WallLayerName = wallLayer,
            ColorIndex = colorIndex,
            UseSecondaryWidth = secondaryWidth.HasValue && (_initialUseSecondaryWidth || !_hadInitialSecondaryWidth),
            PrimaryWidth = primaryWidth,
            SecondaryWidth = secondaryWidth
        };

        return true;
    }
}
