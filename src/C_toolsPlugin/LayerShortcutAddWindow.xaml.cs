using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

public partial class LayerShortcutAddWindow : Window
{
    private readonly Brush _statusNeutralBrush;
    private readonly Brush _statusErrorBrush;

    public LayerShortcutAddWindow()
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);
        SourceInitialized += (_, _) => WindowTitleBarHelper.TryApplyDarkTitleBar(this, applyCaptionColorToBorder: true);

        _statusNeutralBrush = TryFindResource("Cad.Muted") as Brush ?? Brushes.Gray;
        _statusErrorBrush = TryFindResource("Dialog.StatusError") as Brush ?? Brushes.OrangeRed;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    internal LayerShortcutDialogResult? Result { get; private set; }
    internal LayerShortcutDialogResult? InitialResult { get; set; }
    internal string ConfirmButtonText { get; set; } = "添加";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Owner == null)
            {
                var owner = AcAp.MainWindow?.Handle ?? IntPtr.Zero;
                if (owner != IntPtr.Zero)
                    new WindowInteropHelper(this) { Owner = owner };
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("设置图层信息弹窗 Owner", ex);
        }

        DialogWindowPlacementHelper.TryRestoreOrCenterOnOwnerMonitor(this);

        if (InitialResult != null)
        {
            AliasTextBox.Text = InitialResult.Alias ?? "";
            LayerNameTextBox.Text = InitialResult.LayerName ?? "";
            ColorTextBox.Text = InitialResult.LayerColor ?? "";
            DescriptionTextBox.Text = InitialResult.Description ?? "";
        }

        if (ConfirmButton != null)
            ConfirmButton.Content = ConfirmButtonText;

        SetStatus("图层名称必填；快捷键留空只建配置行；颜色可留空或填写 ACI 1-255。");
        AliasTextBox.Focus();
        AliasTextBox.SelectAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DialogWindowPlacementHelper.TrySavePlacement(this);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result, out var error))
        {
            SetStatus(error, isError: true);
            return;
        }

        Result = result;
        DialogResult = true;
        Close();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? _statusErrorBrush : _statusNeutralBrush;
    }

    private bool TryBuildResult(out LayerShortcutDialogResult result, out string error)
    {
        result = new LayerShortcutDialogResult();
        error = "";

        var alias = (AliasTextBox.Text ?? "").Trim();
        var layerName = (LayerNameTextBox.Text ?? "").Trim();
        if (layerName.Length == 0)
        {
            error = "请先填写图层名称。";
            LayerNameTextBox.Focus();
            return false;
        }

        var colorText = (ColorTextBox.Text ?? "").Trim();
        var normalizedColor = "";
        if (colorText.Length > 0)
        {
            var parsedColor = LayerStyleHelper.TryParseAciColor(colorText);
            if (!parsedColor.HasValue)
            {
                error = "颜色请留空，或填写 1-255 的 ACI 颜色索引。";
                ColorTextBox.Focus();
                ColorTextBox.SelectAll();
                return false;
            }

            normalizedColor = parsedColor.Value.ToString(CultureInfo.InvariantCulture);
        }

        var description = (DescriptionTextBox.Text ?? "").Trim();

        result = new LayerShortcutDialogResult
        {
            Alias = alias,
            LayerName = layerName,
            LayerColor = normalizedColor,
            Description = description,
            RunDimensionWhenNoSelection = InitialResult?.RunDimensionWhenNoSelection == true
        };

        return true;
    }
}

internal sealed class LayerShortcutDialogResult
{
    internal string Alias { get; set; } = "";

    internal string LayerName { get; set; } = "";

    internal string LayerColor { get; set; } = "";

    internal string Description { get; set; } = "";

    internal bool RunDimensionWhenNoSelection { get; set; }
}
