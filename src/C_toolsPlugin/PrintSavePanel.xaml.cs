using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

public partial class PrintSavePanel : UserControl
{
    private readonly ObservableCollection<string> _printers = new();
    private readonly ObservableCollection<string> _media = new();
    private readonly ObservableCollection<string> _styles = new();
    private readonly ObservableCollection<string> _scaleOptions = new();
    private readonly ObservableCollection<string> _printOrderRules = new();
    private bool _printerComboBusy;

    public Action<string>? SetStatusCallback { get; set; }

    public PrintSavePanel()
    {
        InitializeComponent();
        PrinterCombo.ItemsSource = _printers;
        MediaCombo.ItemsSource = _media;
        StyleCombo.ItemsSource = _styles;
        ScaleCombo.ItemsSource = _scaleOptions;
        PrintOrderCombo.ItemsSource = _printOrderRules;
        InitializeScaleOptions();
        InitializePrintOrderOptions();
    }

    private void SetStatus(string s) => SetStatusCallback?.Invoke(s);

    private void PrintSavePanel_OnLoaded(object sender, RoutedEventArgs e)
    {
        var options = PrintSaveAutoStore.LoadOrDefault();
        RefreshFromDocument(options);
        PrintSaveAutoSaveService.Restart(options);
    }

    internal void RefreshFromDocument()
    {
        RefreshFromDocument(PrintSaveAutoStore.LoadOrDefault());
    }

    private void RefreshFromDocument(PrintSaveAutoOptionsDto options)
    {
        IntervalMinutesBox.Text = options.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        AutoSaveRootBox.Text = options.SaveBasePath;

        RefreshPrinterList();
        RefreshStyleList();

        _printerComboBusy = true;
        try
        {
            SelectOrAddCombo(PrinterCombo, _printers, options.PrinterName);
            RefreshMediaList(options.PrinterName, options.CanonicalMediaName);
        }
        finally
        {
            _printerComboBusy = false;
        }

        SelectOrAddCombo(StyleCombo, _styles, options.StyleSheet);
        SelectOrAddCombo(ScaleCombo, _scaleOptions, options.FitToPaper ? PrintSaveService.ScaleFitText : options.ScaleText);

        CenterPlotCheckBox.IsChecked = options.CenterPlot;
        OffsetXBox.Text = FormatDouble(options.OffsetX);
        OffsetYBox.Text = FormatDouble(options.OffsetY);
        FitToPaperCheckBox.IsChecked = options.FitToPaper;
        ScaleLineweightsCheckBox.IsChecked = options.ScaleLineweights;
        AutoMatchOrientationCheckBox.IsChecked = options.AutoMatchOrientation;
        LandscapeRadio.IsChecked = options.Landscape;
        PortraitRadio.IsChecked = !options.Landscape;
        UpsideDownCheckBox.IsChecked = options.UpsideDown;
        SelectOrAddCombo(PrintOrderCombo, _printOrderRules, options.PrintOrderRule);

        UpdateOffsetInputsState();
        UpdateScaleInputsState();
        UpdateOrientationInputsState();
        SetStatus("已加载打印与保存设置。");
    }

    private void InitializeScaleOptions()
    {
        _scaleOptions.Clear();
        foreach (var option in new[]
                 {
                     PrintSaveService.ScaleFitText,
                     "1:1",
                     "1:2",
                     "1:50",
                     "1:100"
                 })
        {
            _scaleOptions.Add(option);
        }
    }

    private void InitializePrintOrderOptions()
    {
        _printOrderRules.Clear();
        foreach (var rule in PrintSaveService.GetPlotOrderRules())
            _printOrderRules.Add(rule);
    }

    private void RefreshPrinterList()
    {
        _printers.Clear();
        try
        {
            foreach (var printer in PrintSaveService
                         .GetPlotDeviceList()
                         .Cast<string>()
                         .Where(static x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            {
                _printers.Add(printer);
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：读取打印机列表", ex);
        }
    }

    private void RefreshStyleList()
    {
        _styles.Clear();
        try
        {
            foreach (var style in PrintSaveService
                         .GetPlotStyleSheetList()
                         .Cast<string>()
                         .Where(static x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            {
                _styles.Add(style);
            }

            PrintSaveService.MergePlotStyleFileNamesFromUserFolders(_styles);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：读取打印样式表", ex);
        }
    }

    private static void SelectOrAddCombo(ComboBox combo, ObservableCollection<string> source, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0)
        {
            combo.SelectedIndex = -1;
            combo.Text = "";
            return;
        }

        var idx = -1;
        for (var i = 0; i < source.Count; i++)
        {
            if (string.Equals(source[i], v, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            source.Add(v);
            combo.SelectedItem = v;
        }
        else
        {
            combo.SelectedIndex = idx;
        }

        combo.Text = v;
    }

    private void RefreshMediaList(string? printerName, string? selectMedia)
    {
        _media.Clear();

        var selection = NormalizeMediaSelection(selectMedia);
        var printer = (printerName ?? "").Trim();
        if (printer.Length == 0 || PrintSaveService.IsNoPlotDeviceName(printer))
        {
            SelectOrAddCombo(MediaCombo, _media, selection);
            return;
        }

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            SelectOrAddCombo(MediaCombo, _media, selection);
            return;
        }

        try
        {
            if (!PrintSaveService.TryGetCanonicalMediaNamesForCurrentLayout(doc, printer, out var mediaNames, out var errorMessage))
            {
                SelectOrAddCombo(MediaCombo, _media, selection);
                SetStatus("更新图纸尺寸列表失败：" + (errorMessage ?? "未知错误。"));
                return;
            }

            foreach (var media in mediaNames)
                _media.Add(media);

            SelectOrAddCombo(MediaCombo, _media, selection);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：介质列表", ex);
            SelectOrAddCombo(MediaCombo, _media, selection);
            SetStatus("更新图纸尺寸列表失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：介质列表", ex);
            SelectOrAddCombo(MediaCombo, _media, selection);
            SetStatus("更新图纸尺寸列表失败：" + ex.Message);
        }
    }

    private static string NormalizeMediaSelection(string? media)
    {
        var value = PrintSaveService.NormalizeMediaName(media);
        return value.Length == 0 ? PrintSaveService.MediaAutoMatchText : value;
    }

    private void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_printerComboBusy)
            return;

        RefreshMediaList(GetComboText(PrinterCombo), null);
    }

    private void CenterPlotCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOffsetInputsState();
    }

    private void FitToPaperCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateScaleInputsState();
    }

    private void AutoMatchOrientationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOrientationInputsState();
    }

    private void UpdateOffsetInputsState()
    {
        if (CenterPlotCheckBox == null || OffsetXBox == null || OffsetYBox == null)
            return;

        var isCentered = CenterPlotCheckBox.IsChecked == true;
        OffsetXBox.IsEnabled = !isCentered;
        OffsetYBox.IsEnabled = !isCentered;
    }

    private void UpdateScaleInputsState()
    {
        if (FitToPaperCheckBox == null || ScaleCombo == null)
            return;

        var fitToPaper = FitToPaperCheckBox.IsChecked == true;
        ScaleCombo.IsEnabled = !fitToPaper;
        if (fitToPaper)
            SelectOrAddCombo(ScaleCombo, _scaleOptions, PrintSaveService.ScaleFitText);
    }

    private void UpdateOrientationInputsState()
    {
        if (AutoMatchOrientationCheckBox == null || PortraitRadio == null || LandscapeRadio == null)
            return;

        var autoMatch = AutoMatchOrientationCheckBox.IsChecked == true;
        PortraitRadio.IsEnabled = !autoMatch;
        LandscapeRadio.IsEnabled = !autoMatch;
    }

    /// <summary>
    /// 将当前面板上的打印机/图纸尺寸/打印样式/偏移/比例/方向写入当前布局。
    /// <paramref name="savedOptionsMessage"/> 非空时表示刚保存过共享 JSON，状态栏会合并提示。
    /// </summary>
    private void ApplyCurrentPrintSettingsToCad(
        PrintSavePageFileDto dto,
        string? savedOptionsMessage,
        Action<bool, string>? completed = null)
    {
        if (PrintSaveService.IsNoPlotDeviceName(dto.PrinterName))
        {
            var failMessage = savedOptionsMessage != null
                ? $"{savedOptionsMessage} 未同步到当前布局：请先选择有效打印机。"
                : "请先选择有效打印机。";
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
            return;
        }

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            var successMessage = savedOptionsMessage != null
                                ? $"{savedOptionsMessage} 当前无活动图纸，未同步到布局。"
                                : "当前无活动图纸。";
                            SetStatus(successMessage);
                            completed?.Invoke(true, successMessage);
                        });
                        return;
                    }

                    try
                    {
                        if (!PrintSaveService.TryApplyToCurrentLayout(doc, dto, out var err))
                        {
                            Dispatcher.BeginInvoke(() =>
                            {
                                var failMessage = savedOptionsMessage != null
                                    ? $"{savedOptionsMessage} 同步到当前布局失败：{err ?? "应用失败。"}"
                                    : err ?? "应用失败。";
                                SetStatus(failMessage);
                                completed?.Invoke(false, failMessage);
                            });
                            return;
                        }

                        Dispatcher.BeginInvoke(() =>
                        {
                            var successMessage = savedOptionsMessage != null
                                ? $"{savedOptionsMessage} 已同步到当前布局。"
                                : "已同步到当前布局。";
                            SetStatus(successMessage);
                            completed?.Invoke(true, successMessage);
                        });
                    }
                    catch (InvalidOperationException ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("打印与保存：应用布局参数", ex);
                        Dispatcher.BeginInvoke(() =>
                        {
                            var failMessage = savedOptionsMessage != null
                                ? $"{savedOptionsMessage} 同步到当前布局失败：{ex.Message}"
                                : "同步到当前布局失败：" + ex.Message;
                            SetStatus(failMessage);
                            completed?.Invoke(false, failMessage);
                        });
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("打印与保存：应用布局参数", ex);
                        Dispatcher.BeginInvoke(() =>
                        {
                            var failMessage = savedOptionsMessage != null
                                ? $"{savedOptionsMessage} 同步到当前布局失败：{ex.Message}"
                                : "同步到当前布局失败：" + ex.Message;
                            SetStatus(failMessage);
                            completed?.Invoke(false, failMessage);
                        });
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（打印与保存）", ex);
            var failMessage = savedOptionsMessage != null
                ? $"{savedOptionsMessage} 未同步到当前布局：无法切换到 CAD 上下文：{ex.Message}"
                : "无法切换到 CAD 上下文：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
    }

    private bool TryBuildOptionsFromUi(
        out PrintSaveAutoOptionsDto options,
        out PrintSavePageFileDto dto,
        out string message)
    {
        options = new PrintSaveAutoOptionsDto();
        dto = new PrintSavePageFileDto();
        message = "";

        var intervalText = (IntervalMinutesBox.Text ?? "").Trim();
        if (!int.TryParse(intervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMinutes) ||
            intervalMinutes < 0)
        {
            message = "自动 QSAVE 间隔须为不小于 0 的整数（分钟）。";
            return false;
        }

        if (!TryParseDouble(OffsetXBox.Text, out var offsetX))
        {
            message = "打印偏移 X 请输入有效数字。";
            return false;
        }

        if (!TryParseDouble(OffsetYBox.Text, out var offsetY))
        {
            message = "打印偏移 Y 请输入有效数字。";
            return false;
        }

        var printerName = GetComboText(PrinterCombo);
        if (PrintSaveService.IsNoPlotDeviceName(printerName))
        {
            message = "请先选择有效打印机。";
            return false;
        }

        var fitToPaper = FitToPaperCheckBox.IsChecked == true;
        var scaleText = fitToPaper ? PrintSaveService.ScaleFitText : GetComboText(ScaleCombo);
        if (!fitToPaper && scaleText.Length == 0)
        {
            message = "请输入打印比例，例如 1:1、1:50。";
            return false;
        }

        var mediaName = NormalizeMediaSelection(GetComboText(MediaCombo));
        var styleSheet = GetComboText(StyleCombo);
        var centerPlot = CenterPlotCheckBox.IsChecked == true;
        var autoMatchOrientation = AutoMatchOrientationCheckBox.IsChecked == true;
        var landscape = LandscapeRadio.IsChecked != false;
        var upsideDown = UpsideDownCheckBox.IsChecked == true;
        var scaleLineweights = ScaleLineweightsCheckBox.IsChecked != false;
        var printOrderRule = GetComboText(PrintOrderCombo);
        if (printOrderRule.Length == 0)
            printOrderRule = PrintSaveService.PlotOrderAddedOrder;
        var saveBasePath = (AutoSaveRootBox.Text ?? "").Trim();
        if (saveBasePath.Length == 0)
            saveBasePath = PrintSaveAutoStore.DefaultSaveBasePath;

        options = new PrintSaveAutoOptionsDto
        {
            IntervalMinutes = intervalMinutes,
            SaveBasePath = saveBasePath,
            PrinterName = printerName,
            CanonicalMediaName = mediaName,
            StyleSheet = styleSheet,
            CenterPlot = centerPlot,
            OffsetX = offsetX,
            OffsetY = offsetY,
            FitToPaper = fitToPaper,
            ScaleText = scaleText,
            ScaleLineweights = scaleLineweights,
            AutoMatchOrientation = autoMatchOrientation,
            Landscape = landscape,
            UpsideDown = upsideDown,
            PrintOrderRule = printOrderRule
        };

        dto = new PrintSavePageFileDto
        {
            PageSetupName = "",
            PrinterName = printerName,
            CanonicalMediaName = mediaName,
            StyleSheet = styleSheet,
            CenterPlot = centerPlot,
            OffsetX = offsetX,
            OffsetY = offsetY,
            FitToPaper = fitToPaper,
            ScaleText = scaleText,
            ScaleLineweights = scaleLineweights,
            AutoMatchOrientation = autoMatchOrientation,
            Landscape = landscape,
            UpsideDown = upsideDown
        };

        return true;
    }

    private static string GetComboText(ComboBox combo)
    {
        return (combo.SelectedItem as string ?? combo.Text ?? "").Trim();
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        var candidate = (text ?? "").Trim();
        if (candidate.Length == 0)
        {
            value = 0;
            return true;
        }

        if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return double.TryParse(candidate, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>主窗口「保存」在「打印与保存」标签时调用：写入共享 JSON，并在有活动图纸时同步当前布局。</summary>
    internal void ApplyFromMainSave(Action<bool, string>? completed = null)
    {
        if (!TryBuildOptionsFromUi(out var options, out var dto, out var message))
        {
            SetStatus(message);
            completed?.Invoke(false, message);
            return;
        }

        try
        {
            PrintSaveAutoStore.Save(options);
            PrintSaveAutoSaveService.Restart(options);

            var savedMessage = "已保存打印与保存设置：" + PrintSaveAutoStore.FilePath;
            ApplyCurrentPrintSettingsToCad(dto, savedMessage, completed);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：保存设置（路径参数）", ex);
            var failMessage = "保存失败：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：保存设置（路径过长）", ex);
            var failMessage = "保存失败：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：保存设置（路径格式）", ex);
            var failMessage = "保存失败：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：保存设置（权限）", ex);
            var failMessage = "保存失败：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：保存设置", ex);
            var failMessage = "保存失败：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
    }
}
