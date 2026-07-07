using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal sealed class BbbDeviceBlockCreateWindow : Window
{
    private const string PlacementKey = "C_TOOL_BbbDeviceBlockCreateWindow";
    private readonly TextBox _blockNameBox;
    private readonly TextBlock _statusText;
    private readonly Dictionary<BbbDeviceBlockAnchor, Button> _anchorButtons = new();
    private BbbDeviceBlockAnchor _selectedAnchor;

    internal BbbDeviceBlockCreateWindow(BbbDeviceBlockCreateSettings settings)
    {
        Title = "F_AB 创建设备块";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        MinWidth = 400;
        MinHeight = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Brush("#232830");
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 12;

        _selectedAnchor = settings.Anchor;
        _blockNameBox = CreateTextBox(settings.BlockName);
        _statusText = new TextBlock
        {
            Text = "",
            Foreground = Brush("#F0C674"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Content = BuildContent();
        SelectAnchor(_selectedAnchor);
    }

    internal BbbDeviceBlockCreateSettings? SavedSettings { get; private set; }
    internal bool RequestPickText { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTitleBarHelper.TryApplyDarkTitleBar(this);
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
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var cancelButton = CreateActionButton("取消", isPrimary: false);
        cancelButton.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        var okButton = CreateActionButton("创建", isPrimary: true);
        okButton.Click += (_, _) => ConfirmAndClose();

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        var panel = new StackPanel();
        panel.Children.Add(CreateLabel("设备块名称"));
        panel.Children.Add(CreateBlockNameRow());
        panel.Children.Add(CreateSpacer(14));
        panel.Children.Add(CreateLabel("基点位置"));
        panel.Children.Add(CreateAnchorGrid());
        panel.Children.Add(_statusText);

        root.Children.Add(buttons);
        root.Children.Add(panel);
        return root;
    }

    private UIElement CreateBlockNameRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_blockNameBox, 0);
        grid.Children.Add(_blockNameBox);

        var pickButton = CreateSecondaryButton("拾取文字");
        pickButton.Margin = new Thickness(8, 0, 0, 0);
        pickButton.Click += (_, _) =>
        {
            SaveCurrentSettings(allowEmptyName: true);
            RequestPickText = true;
            DialogResult = false;
            Close();
        };

        Grid.SetColumn(pickButton, 1);
        grid.Children.Add(pickButton);
        return grid;
    }

    private UIElement CreateAnchorGrid()
    {
        var outer = new Border
        {
            BorderBrush = Brush("#56616C"),
            BorderThickness = new Thickness(1),
            Background = Brush("#1E232B"),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 6, 0, 0)
        };

        var grid = new Grid();
        for (var i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        AddAnchorButton(grid, BbbDeviceBlockAnchor.TopLeft, "左上", 0, 0);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.TopCenter, "中上", 0, 1);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.TopRight, "右上", 0, 2);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.MiddleLeft, "左中", 1, 0);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.Center, "中心", 1, 1);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.MiddleRight, "右中", 1, 2);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.BottomLeft, "左下", 2, 0);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.BottomCenter, "中下", 2, 1);
        AddAnchorButton(grid, BbbDeviceBlockAnchor.BottomRight, "右下", 2, 2);

        outer.Child = grid;
        return outer;
    }

    private void AddAnchorButton(Grid grid, BbbDeviceBlockAnchor anchor, string text, int row, int column)
    {
        var button = CreateSecondaryButton(text);
        button.Margin = new Thickness(3);
        button.MinWidth = 0;
        button.Height = 38;
        button.Click += (_, _) => SelectAnchor(anchor);

        _anchorButtons[anchor] = button;
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private void SelectAnchor(BbbDeviceBlockAnchor anchor)
    {
        _selectedAnchor = anchor;
        foreach (var pair in _anchorButtons)
        {
            var selected = pair.Key == anchor;
            pair.Value.Background = selected ? Brush("#2D6CDF") : Brush("#2E3640");
            pair.Value.BorderBrush = selected ? Brush("#76A9FF") : Brush("#56616C");
            pair.Value.Foreground = selected ? Brushes.White : Brush("#E6E8EA");
        }
    }

    private void ConfirmAndClose()
    {
        if (!SaveCurrentSettings(allowEmptyName: false))
            return;

        DialogResult = true;
        Close();
    }

    private bool SaveCurrentSettings(bool allowEmptyName)
    {
        var nameText = _blockNameBox.Text ?? "";
        if (!allowEmptyName &&
            !BbbDeviceBlockCreateService.TryNormalizeBlockNameInput(nameText, out nameText, out var error))
        {
            _statusText.Text = error;
            _statusText.Foreground = Brush("#E06C75");
            return false;
        }

        SavedSettings = new BbbDeviceBlockCreateSettings
        {
            BlockName = allowEmptyName ? nameText.Trim() : nameText,
            Anchor = _selectedAnchor
        };
        return true;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this, PlacementKey);
        _blockNameBox.Focus();
        _blockNameBox.SelectAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DialogWindowPlacementHelper.TrySavePlacement(this, PlacementKey);
    }

    private static TextBlock CreateLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Brush("#AAB2BF"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    private static TextBox CreateTextBox(string text)
    {
        return new TextBox
        {
            Text = text,
            Height = 32,
            MinHeight = 32,
            Background = Brush("#1E232B"),
            Foreground = Brush("#E6E8EA"),
            BorderBrush = Brush("#56616C"),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static Button CreateActionButton(string text, bool isPrimary)
    {
        var button = CreateSecondaryButton(text);
        button.MinWidth = 78;
        button.Margin = new Thickness(8, 0, 0, 0);
        if (isPrimary)
        {
            button.Background = Brush("#2D6CDF");
            button.BorderBrush = Brush("#76A9FF");
        }

        return button;
    }

    private static Button CreateSecondaryButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 82,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brush("#2E3640"),
            Foreground = Brush("#E6E8EA"),
            BorderBrush = Brush("#56616C"),
            BorderThickness = new Thickness(1)
        };
    }

    private static UIElement CreateSpacer(double height)
    {
        return new Border { Height = height };
    }

    private static Brush Brush(string color)
    {
        return (Brush)new BrushConverter().ConvertFromString(color)!;
    }
}
