using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using C_toolsPlugin;
using C_toolsShared;

namespace C_toolsDddPlugin;

internal sealed class DddOuterDimensionSettingsWindow : Window
{
    private readonly TextBox _offsetDistanceBox;
    private readonly TextBlock _statusText;

    internal DddOuterDimensionSettingsWindow(DddOuterDimensionSettingsDto currentSettings)
    {
        Title = "F_DQQ 外包尺寸设置";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 360;
        MinHeight = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = CadDialogControlFactory.Brush("#232830");
        Foreground = Brushes.White;

        _offsetDistanceBox = CadDialogControlFactory.CreateTextBox(
            currentSettings.OffsetDistance.ToString("0.###", CultureInfo.InvariantCulture));

        _statusText = new TextBlock
        {
            Text = "生成外包总尺寸时，会在当前标注所在排距的基础上继续向外偏移这个数值。",
            Foreground = CadDialogControlFactory.Brush("#F0C674"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Content = BuildContent();
    }

    internal DddOuterDimensionSettingsDto? SavedSettings { get; private set; }

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
        panel.Children.Add(CadDialogControlFactory.CreateField("向外偏移量", _offsetDistanceBox));
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

    private bool TryBuildSettings(out DddOuterDimensionSettingsDto dto, out string error)
    {
        dto = new DddOuterDimensionSettingsDto();
        error = "";

        if (!CadDialogValueHelper.TryParsePositiveDouble(_offsetDistanceBox.Text, out var offsetDistance))
        {
            error = "向外偏移量必须是大于 0 的数字。";
            return false;
        }

        dto = new DddOuterDimensionSettingsDto
        {
            OffsetDistance = offsetDistance
        };
        return true;
    }
}
