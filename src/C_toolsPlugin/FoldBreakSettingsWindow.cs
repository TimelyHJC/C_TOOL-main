using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using C_toolsShared;

namespace C_toolsPlugin;

internal sealed class FoldBreakSettingsWindow : Window
{
    private readonly TextBox _horizontalLeftPartBox;
    private readonly TextBox _horizontalRightPartBox;
    private readonly TextBox _verticalTopPartBox;
    private readonly TextBox _verticalBottomPartBox;
    private readonly TextBox _colorIndexBox;
    private readonly TextBlock _statusText;

    internal FoldBreakSettingsWindow(FoldBreakSettingsDto currentSettings)
    {
        Title = "F_DK 折空符号设置";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(340, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 340;
        MinHeight = 280;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = CadDialogControlFactory.Brush("#232830");
        Foreground = System.Windows.Media.Brushes.White;

        _horizontalLeftPartBox = CreateNumberBox(currentSettings.HorizontalLeftPart);
        _horizontalRightPartBox = CreateNumberBox(currentSettings.HorizontalRightPart);
        _verticalTopPartBox = CreateNumberBox(currentSettings.VerticalTopPart);
        _verticalBottomPartBox = CreateNumberBox(currentSettings.VerticalBottomPart);
        _colorIndexBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.ColorIndex.ToString(CultureInfo.InvariantCulture));

        _statusText = new TextBlock
        {
            Text = "默认比例为 1:7，颜色为 ACI 8。",
            Foreground = CadDialogControlFactory.Brush("#F0C674"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Content = BuildContent();
    }

    internal FoldBreakSettingsDto? SavedSettings { get; private set; }

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
        panel.Children.Add(CadDialogControlFactory.CreateField("左右比例：左份", _horizontalLeftPartBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("左右比例：右份", _horizontalRightPartBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("上下比例：上份", _verticalTopPartBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("上下比例：下份", _verticalBottomPartBox));
        panel.Children.Add(CadDialogControlFactory.CreateField("颜色(ACI)", _colorIndexBox));
        panel.Children.Add(_statusText);

        root.Children.Add(buttons);
        root.Children.Add(panel);
        return root;
    }

    private static TextBox CreateNumberBox(double value) =>
        CadDialogControlFactory.CreateTextBox(value.ToString("0.###", CultureInfo.InvariantCulture));

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

    private bool TryBuildSettings(out FoldBreakSettingsDto dto, out string error)
    {
        dto = new FoldBreakSettingsDto();
        error = "";

        if (!TryReadPositivePart(_horizontalLeftPartBox.Text, "左右比例左份", out var horizontalLeftPart, out error) ||
            !TryReadPositivePart(_horizontalRightPartBox.Text, "左右比例右份", out var horizontalRightPart, out error) ||
            !TryReadPositivePart(_verticalTopPartBox.Text, "上下比例上份", out var verticalTopPart, out error) ||
            !TryReadPositivePart(_verticalBottomPartBox.Text, "上下比例下份", out var verticalBottomPart, out error))
        {
            return false;
        }

        var colorIndex = LayerStyleHelper.TryParseAciColor(_colorIndexBox.Text);
        if (!colorIndex.HasValue)
        {
            error = "颜色请填写 1-255 的 ACI 颜色编号。";
            return false;
        }

        dto = FoldBreakSettingsStore.Normalize(new FoldBreakSettingsDto
        {
            HorizontalLeftPart = horizontalLeftPart,
            HorizontalRightPart = horizontalRightPart,
            VerticalTopPart = verticalTopPart,
            VerticalBottomPart = verticalBottomPart,
            ColorIndex = colorIndex.Value
        });
        return true;
    }

    private static bool TryReadPositivePart(string? text, string label, out double value, out string error)
    {
        if (CadDialogValueHelper.TryParsePositiveDouble(text, out value))
        {
            error = "";
            return true;
        }

        error = label + "必须是大于 0 的数字。";
        return false;
    }
}
