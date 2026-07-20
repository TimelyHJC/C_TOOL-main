using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CadColor = Autodesk.AutoCAD.Colors.Color;

namespace C_toolsPlugin;

public partial class CurrentLayerPaletteControl : UserControl
{
    private const string EmptyValue = "-";

    private readonly Brush _statusNeutralBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 197, 214));
    private readonly Brush _statusErrorBrush = Brushes.OrangeRed;

    public CurrentLayerPaletteControl()
    {
        InitializeComponent();

        if (_statusNeutralBrush.CanFreeze)
            _statusNeutralBrush.Freeze();

        SetLayerDropDown(null, EmptyValue, "正在读取当前图层状态...");
        UpdateLayerActionButtons(null);
        UpdateTabVisibility();
    }

    internal void ApplySnapshot(CurrentLayerSnapshot? snapshot, string? error)
    {
        if (snapshot == null)
        {
            var message = string.IsNullOrWhiteSpace(error) ? "读取当前图层状态失败。" : error!.Trim();
            SetField(ShortcutText, EmptyValue, message);
            SetLayerDropDown(null, EmptyValue, message);
            SetField(DocumentText, EmptyValue, message);
            SetField(ColorText, EmptyValue, message);
            SetField(DimStyleText, EmptyValue, message);
            SetField(TextStyleText, EmptyValue, message);
            SetField(MLeaderStyleText, EmptyValue, message);
            SetField(LinetypeText, EmptyValue, message);
            SetField(LinetypeScaleText, EmptyValue, message);
            SetField(LineweightText, EmptyValue, message);
            SetStatus(message, isError: true);
            UpdateLayerActionButtons(null);
            return;
        }

        SetField(ShortcutText, snapshot.ShortcutAliasesText, snapshot.ShortcutStatusText);
        SetLayerDropDown(snapshot.Layers, snapshot.LayerName, snapshot.LayerName);
        SetField(DocumentText, snapshot.DocumentName);
        SetField(ColorText, snapshot.Color);
        SetField(DimStyleText, snapshot.DimStyle);
        SetField(TextStyleText, snapshot.TextStyle);
        SetField(MLeaderStyleText, snapshot.MLeaderStyle);
        SetField(LinetypeText, snapshot.Linetype);
        SetField(LinetypeScaleText, snapshot.LinetypeScale);
        SetField(LineweightText, snapshot.Lineweight);
        SetStatus(snapshot.ShortcutStatusText);
        UpdateLayerActionButtons(snapshot);
    }

    private static void SetField(TextBlock textBlock, string? value, string? toolTip = null)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? EmptyValue : value!.Trim();
        textBlock.Text = normalized;
        textBlock.ToolTip = string.IsNullOrWhiteSpace(toolTip) ? normalized : toolTip!.Trim();
    }

    private void SetLayerDropDown(IReadOnlyList<CurrentLayerListItem>? layers, string? selectedLayerName, string? toolTip)
    {
        var normalized = string.IsNullOrWhiteSpace(selectedLayerName) ? EmptyValue : selectedLayerName!.Trim();

        var items = layers?
            .Where(x => !string.IsNullOrWhiteSpace(x.LayerName))
            .GroupBy(x => x.LayerName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.LayerName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<CurrentLayerListItem>();

        if (normalized != EmptyValue &&
            items.All(x => !string.Equals(x.LayerName, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new CurrentLayerListItem
            {
                LayerName = normalized,
                IsCurrent = true
            });
        }

        LayerRows.ItemsSource = items;
        LayerDropDownText.Text = normalized;

        var normalizedToolTip = string.IsNullOrWhiteSpace(toolTip) ? normalized : toolTip!.Trim();
        LayerDropDownText.ToolTip = normalizedToolTip;
        LayerDropDownToggle.ToolTip = normalizedToolTip;
        LayerDropDownToggle.IsEnabled = items.Count > 0;
        if (items.Count == 0)
            LayerDropDownToggle.IsChecked = false;
    }

    private void SetStatus(string? message, bool isError = false)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? "当前图层状态已刷新。" : message!.Trim();
        StatusText.Foreground = isError ? _statusErrorBrush : _statusNeutralBrush;
    }

    private void UpdateLayerActionButtons(CurrentLayerSnapshot? snapshot)
    {
        var enabled = snapshot != null;

        UpdateStateButton(
            BtnLayerOn,
            IconLayerOn,
            enabled,
            snapshot?.IsLayerOn == true,
            snapshot?.IsLayerOn == true ? CurrentLayerStatusKind.Power : CurrentLayerStatusKind.PowerOff,
            enabled ? "切换当前图层开/关" : "当前没有可用图层");

        UpdateStateButton(
            BtnLayerFreeze,
            IconLayerFreeze,
            enabled,
            snapshot?.IsFrozen == true,
            snapshot?.IsFrozen == true ? CurrentLayerStatusKind.Frozen : CurrentLayerStatusKind.Freeze,
            enabled ? "切换当前图层冻结/解冻" : "当前没有可用图层");

        UpdateStateButton(
            BtnViewportFreeze,
            IconViewportFreeze,
            enabled,
            snapshot?.IsViewportFrozen == true,
            snapshot?.IsViewportFrozen == true ? CurrentLayerStatusKind.ViewportFrozen : CurrentLayerStatusKind.ViewportFreeze,
            enabled ? "切换当前图层在当前视口冻结/解冻" : "当前没有可用视口");

        UpdateStateButton(
            BtnLayerLock,
            IconLayerLock,
            enabled,
            snapshot?.IsLocked == true,
            snapshot?.IsLocked == true ? CurrentLayerStatusKind.Lock : CurrentLayerStatusKind.Unlock,
            enabled ? "切换当前图层锁定/解锁" : "当前没有可用图层");

        UpdateColorButton(BtnLayerColor, IconLayerColor, enabled, snapshot?.LayerColor, "设置当前图层颜色");
        UpdateColorButton(BtnViewportColor, IconViewportColor, enabled, snapshot?.ViewportColor, "设置当前图层在当前视口的颜色");
    }

    private static void UpdateStateButton(
        Button button,
        Image icon,
        bool isEnabled,
        bool isActive,
        CurrentLayerStatusKind iconKind,
        string toolTip)
    {
        button.IsEnabled = isEnabled;
        button.Tag = isActive;
        button.ToolTip = toolTip;
        icon.Source = CurrentLayerStatusIconCatalog.TryGet(iconKind);
    }

    private static void UpdateColorButton(Button button, Image icon, bool isEnabled, CadColor? color, string toolTip)
    {
        button.IsEnabled = isEnabled;
        button.Tag = false;
        button.ToolTip = isEnabled ? toolTip : "当前没有可用图层";
        icon.Source = color == null
            ? CurrentLayerStatusIconCatalog.TryGet(CurrentLayerStatusKind.Color)
            : CreateColorSwatchImage(color);
    }

    private static ImageSource CreateColorSwatchImage(CadColor color)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var brush = new SolidColorBrush(ToMediaColor(color));
            brush.Freeze();
            var borderPen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238)), 1);
            borderPen.Freeze();
            context.DrawRectangle(brush, borderPen, new Rect(2, 2, 14, 14));
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static System.Windows.Media.Color ToMediaColor(CadColor color)
    {
        try
        {
            if (color.IsByColor || color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor)
                return System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);

            var index = color.ColorIndex;
            if (index <= 0 || index > 255)
                index = 7;

            var drawingColor = CadColor.GetColorValue(index, System.Drawing.Color.Black);
            return System.Windows.Media.Color.FromRgb(drawingColor.R, drawingColor.G, drawingColor.B);
        }
        catch
        {
            return System.Windows.Media.Color.FromRgb(255, 255, 255);
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e) => UpdateTabVisibility();

    private void UpdateTabVisibility()
    {
        var showStyles = TabStyles?.IsChecked == true;
        if (LayerPage != null)
            LayerPage.Visibility = showStyles ? Visibility.Collapsed : Visibility.Visible;
        if (StylePage != null)
            StylePage.Visibility = showStyles ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LayerOn_Click(object sender, RoutedEventArgs e) =>
        CurrentLayerFloatingTabManager.ToggleCurrentLayer(LayerToggleAction.On);

    private void LayerDropDownRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CurrentLayerListItem item } ||
            string.IsNullOrWhiteSpace(item.LayerName))
        {
            return;
        }

        LayerDropDownToggle.IsChecked = false;
        CurrentLayerFloatingTabManager.ChangeLayer(item.LayerName);
    }

    private void LayerOnDropDownButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ToggleLayerStateFromDropDown(sender, e, LayerTableToggleAction.On);

    private void LayerFreezeDropDownButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ToggleLayerStateFromDropDown(sender, e, LayerTableToggleAction.Freeze);

    private void LayerNewViewportFreezeDropDownButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ToggleLayerStateFromDropDown(sender, e, LayerTableToggleAction.NewViewportFreeze);

    private void LayerLockDropDownButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ToggleLayerStateFromDropDown(sender, e, LayerTableToggleAction.Lock);

    private static void ToggleLayerStateFromDropDown(object sender, MouseButtonEventArgs e, LayerTableToggleAction action)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: CurrentLayerListItem item } ||
            string.IsNullOrWhiteSpace(item.LayerName))
        {
            return;
        }

        CurrentLayerFloatingTabManager.ToggleLayerState(item.LayerName, action);
    }

    private void LayerDropDownIconButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void LayerFreeze_Click(object sender, RoutedEventArgs e) =>
        CurrentLayerFloatingTabManager.ToggleCurrentLayer(LayerToggleAction.Freeze);

    private void ViewportFreeze_Click(object sender, RoutedEventArgs e) =>
        CurrentLayerFloatingTabManager.ToggleCurrentLayer(LayerToggleAction.ViewportFreeze);

    private void LayerLock_Click(object sender, RoutedEventArgs e) =>
        CurrentLayerFloatingTabManager.ToggleCurrentLayer(LayerToggleAction.Lock);

    private void LayerColor_Click(object sender, RoutedEventArgs e) =>
        CurrentLayerFloatingTabManager.SetCurrentLayerColor(useViewportOverride: false);

    private void ViewportColor_Click(object sender, RoutedEventArgs e) =>
        CurrentLayerFloatingTabManager.SetCurrentLayerColor(useViewportOverride: true);
}
