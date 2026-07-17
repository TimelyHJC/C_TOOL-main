using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using C_toolsShared;
using Forms = System.Windows.Forms;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsQqqPlugin
{

public partial class QqqPlotWindow : Window, IModelessWindowPlacement, IModelessWindowHostAware
{
    public string PlacementKey => "C_TOOL_QqqPlot";

    private const string DefaultPdfPrinter = "DWG To PDF.pc3";
    private const string DefaultFileNameTemplate = QqqPlotService.FileNameTemplateDrawing;
    private const string MultiSizeText = "多尺寸";
    private const int FrameSelectionPersistDelayMilliseconds = 450;
    private static readonly System.Windows.Media.Brush FallbackStatusSuccessBrush = CreateFrozenBrush("#9BCBFF");
    private static readonly System.Windows.Media.Brush FallbackStatusErrorBrush = CreateFrozenBrush("#FF7B5C");
    private static readonly System.Windows.Media.Brush FallbackHintFixedBrush = CreateFrozenBrush("#F0C674");

    private readonly ResettableObservableCollection<FrameInfo> _frames = new();
    private readonly ResettableObservableCollection<FrameTemplateInfo> _templates = new();
    private readonly ObservableCollection<string> _outputFolders = new();
    private readonly ObservableCollection<string> _fileNameTemplates = new();
    private readonly ResettableObservableCollection<SavedSheetSetTabInfo> _savedSheetSetTabs = new();
    private readonly System.Windows.Threading.DispatcherTimer _frameSelectionPersistTimer;

    private string _suggestedOutputFolder = "";
    private string _outputFolderTextBeforeEdit = "";
    private bool _isPrinting;
    private bool _recognitionModeReady;
    private bool _recognitionTemplatesLoaded;
    private bool _isLoadingRecognitionTemplates;
    private bool _isApplyingRecognitionMode;
    private int _sheetRefreshVersion;
    private CancellationTokenSource? _printCancellation;
    private Document? _pendingNativeCadCommandDocument;
    private string _pendingNativeCadCommandName = "";
    private string _pendingNativeCadCommandCloseMessage = "";
    private string _pendingNativeCadCommandCloseHint = "";
    private bool _isUpdatingFrameSelection;
    private bool _isUpdatingSavedSheetSetTabs;
    private bool _isAwaitingSpaceToRestoreFromRangeDisplay;
    private FrameListPersistRequest? _pendingFrameListPersist;
    private QqqRecognitionTemplateStoreState _recognitionTemplateState = new();

    public QqqPlotWindow()
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);

        TemplateGrid.ItemsSource = _templates;
        FramesGrid.ItemsSource = _frames;
        OutputFolderCombo.ItemsSource = _outputFolders;
        FileNameTemplateCombo.ItemsSource = _fileNameTemplates;
        SavedSheetSetTabs.ItemsSource = _savedSheetSetTabs;

        _frameSelectionPersistTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(FrameSelectionPersistDelayMilliseconds)
        };
        _frameSelectionPersistTimer.Tick += FrameSelectionPersistTimer_Tick;

        _frames.CollectionChanged += (_, _) =>
        {
            UpdateFrameSelectionHeader();
        };

        UpdateSavedSheetSetTabsVisibility();

        InitializeStaticOptions();
        UpdateRecognitionTemplateHeaders();
        Closed += OnWindowClosed;
        UpdateStatus(
            "请先选择识别方式，再点击加号记录识别项；也可点击 ○ 从 CAD 选择列表中的图块添加图纸。",
            "提示：输出固定为合并 PDF；打印参数读取 V_YYY“打印与保存”设置；可在上方切换输出目录和命名模板。");
    }

    internal void RefreshFromCurrentDocument()
    {
        FlushPendingFrameListPersist();
        PersistRecognitionTemplatesForCurrentMode();

        var document = AcAp.DocumentManager.MdiActiveDocument;
        _suggestedOutputFolder = QqqPlotService.GetSuggestedOutputFolder(document);
        if (string.IsNullOrWhiteSpace(_suggestedOutputFolder))
            _suggestedOutputFolder = ResolveDefaultOutputFolder();

        RefreshOutputFolderSuggestions(_suggestedOutputFolder);
        if (_outputFolders.Count > 0)
            OutputFolderCombo.SelectedItem = _outputFolders[0];
        RefreshCadPlotResourcePaths();

        LoadRecognitionTemplates();
        RefreshSavedSheetSetTabs();
        RestoreWorkingFramesForCurrentDocument();
    }

    public void OnHostShowing()
    {
        UnregisterRangeDisplaySpaceRestore();
        RefreshFromCurrentDocument();
    }

    public void OnHostHiding()
    {
        UnregisterRangeDisplaySpaceRestore();
        FlushPendingFrameListPersist();
        PersistRecognitionTemplatesForCurrentMode();
        QqqPlotService.ClearFrameRangeDisplay();
    }

    // ── Window events ────────────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTitleBarHelper.TryApplyDarkTitleBar(this);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TryAttachOwner();
        RefreshFromCurrentDocument();
        _recognitionModeReady = true;
    }

    private void TitleSettings_Click(object sender, RoutedEventArgs e)
    {
        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        try
        {
            document.SendStringToExecute(C_toolsCommandIds.Sys.OpenPrintSaveTab + "\n", true, false, false);
            UpdateStatus(
                "已打开 V_YYY 的“打印与保存”页。",
                "提示：V_QQQ 会按 V_YYY 中保存的打印与保存设置生成 PDF。");
        }
        catch (Exception ex)
        {
            UpdateStatus("打开 V_YYY“打印与保存”页失败：" + ex.Message);
            C_toolsDiagnostics.LogNonFatal("V_QQQ 打开 V_YYY 打印与保存页失败", ex);
        }
    }

    // ── Recognition mode (block / layer) ────────────────────────────────────

    private void RecognitionMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateRecognitionTemplateHeaders();
        if (!_recognitionModeReady || !IsLoaded || _isApplyingRecognitionMode)
            return;

        LoadRecognitionTemplatesForCurrentMode(isInitialLoad: false);
        PersistRecognitionTemplatesForCurrentMode();
    }

    // ── Template list (识别方式上方的图块/图层列表) ──────────────────────────

    private void RefreshTemplates_Click(object sender, RoutedEventArgs e)
    {
        BeginCaptureRecognitionTemplates(replaceExisting: false);
    }

    private void ClearTemplates_Click(object sender, RoutedEventArgs e)
    {
        ClearTemplatesAndSheets();
        PersistRecognitionTemplatesForCurrentMode();
        UpdateStatus(
            "已清空识别列表。",
            "提示：重新点击加号，可继续从 CAD 记录识别项。");
    }

    private void TemplateSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingRecognitionTemplates)
            return;

        PersistRecognitionTemplatesForCurrentMode();
        UpdateStatus(
            "已更新识别项。",
            "提示：点击刷新读取勾选项，或点击 ○ 选择图块、点击 □ 框选窗口手动追加图纸。");
    }

    // ── Sheet list (图纸列表) ────────────────────────────────────────────────

    private void RefreshFrames_Click(object sender, RoutedEventArgs e)
    {
        RefreshFrames(false, controlDisplayedSheetSet: true);
    }

    private void DeleteSelectedFrames_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count == 0)
        {
            UpdateStatus(
                "当前图纸列表为空。",
                "提示：保留了上方识别项；重新勾选或点击刷新后，可再次读取图纸。");
            return;
        }

        var selectedFrames = _frames
            .Where(static x => x.IsSelected)
            .ToList();
        if (selectedFrames.Count == 0)
        {
            UpdateStatus(
                "请先勾选要删除的图纸。",
                $"提示：当前图纸列表共 {_frames.Count} 张图纸。");
            return;
        }

        QqqPlotService.ClearFrameRangeDisplay();

        foreach (var frame in selectedFrames)
            _frames.Remove(frame);

        RenumberFrameAddedOrders();
        UpdateFrameSelectionHeader();
        var persistErrorMessage = PersistDisplayedFrames();

        UpdateStatus(
            $"已删除 {selectedFrames.Count} 张勾选图纸。",
            BuildSheetListHint(
                _frames.Count == 0
                ? "提示：保留了上方识别项；重新勾选或点击刷新后，可再次读取图纸。"
                : $"提示：当前图纸列表还剩 {_frames.Count} 张图纸，可继续预览或开始打印。",
                persistErrorMessage));
    }

    private void SaveSheetSetButton_Click(object sender, RoutedEventArgs e)
    {
        FlushPendingFrameListPersist(showStatusOnError: true);

        var documentPath = GetCurrentDocumentName();
        var definitions = BuildSavedSheetSetDefinitionsFromTabs();
        var nextName = CreateNextSavedSheetSetName(definitions);
        var nextDefinition = new QqqSavedSheetSetDefinition
        {
            Name = nextName,
            Frames = new List<FrameInfo>()
        };
        QqqSavedSheetSetStore.CaptureTextSnapshots(nextDefinition);
        definitions.Add(nextDefinition);

        if (!QqqSavedSheetSetStore.Save(documentPath, definitions))
        {
            UpdateStatus("保存 TAD 标签失败。");
            return;
        }

        RefreshSavedSheetSetTabs(selectTabName: nextName);
        if (SavedSheetSetTabs.SelectedItem is SavedSheetSetTabInfo { Definition: not null } selectedTab &&
            string.Equals(selectedTab.DisplayName, nextName, StringComparison.OrdinalIgnoreCase))
        {
            _frames.ReplaceAll(selectedTab.Definition.Frames.Select(CloneFrameInfo));
            UpdateFrameSelectionHeader();
        }

        UpdateStatus(
            $"已新建 TAD 标签“{nextName}”。",
            "提示：该标签暂未记录图纸；添加图纸后会自动保存到该 TAD 标签。");
    }

    private void SavedSheetSetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSavedSheetSetTabs)
            return;

        FlushPendingFrameListPersist(showStatusOnError: true);

        if (SavedSheetSetTabs.SelectedItem is not SavedSheetSetTabInfo tab ||
            tab.IsWorkingSet ||
            tab.Definition == null)
        {
            return;
        }

        ApplySavedSheetSet(tab);
    }

    private void SavedSheetSetTabHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
            return;

        if (sender is not FrameworkElement { DataContext: SavedSheetSetTabInfo tab } ||
            tab.IsWorkingSet ||
            tab.Definition == null)
        {
            return;
        }

        if (tab.IsRenaming)
            return;

        SavedSheetSetTabs.SelectedItem = tab;
        BeginSavedSheetSetRename(tab);
        e.Handled = true;
    }

    private void SavedSheetSetRenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
            return;

        if (textBox.IsVisible)
            FocusSavedSheetSetRenameTextBox(textBox);
    }

    private void SavedSheetSetRenameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox && textBox.IsVisible)
            FocusSavedSheetSetRenameTextBox(textBox);
    }

    private void SavedSheetSetRenameTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SavedSheetSetTabInfo tab })
            return;

        if (e.Key == Key.Enter)
        {
            CommitSavedSheetSetRename(tab);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelSavedSheetSetRename(tab);
            e.Handled = true;
        }
    }

    private void SavedSheetSetRenameTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedSheetSetTabInfo tab })
            CommitSavedSheetSetRename(tab);
    }

    private void FocusSavedSheetSetRenameTextBox(System.Windows.Controls.TextBox textBox)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!textBox.IsVisible)
                return;

            textBox.Focus();
            textBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Output folder ─────────────────────────────────────────────────────────

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择 PDF 输出目录",
            SelectedPath = OutputFolderCombo.Text.Trim()
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        OutputFolderCombo.Text = dialog.SelectedPath;
        if (!_outputFolders.Contains(dialog.SelectedPath))
            _outputFolders.Insert(0, dialog.SelectedPath);
    }

    private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (OutputFolderCombo.IsEditable)
            EndOutputFolderEdit(commitChanges: true);

        var outputFolder = (OutputFolderCombo.SelectedItem as string ?? OutputFolderCombo.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            UpdateStatus("输出目录为空，无法打开。");
            return;
        }

        if (!Directory.Exists(outputFolder))
        {
            UpdateStatus($"输出目录不存在：{outputFolder}");
            return;
        }

        OpenOutputFolder(outputFolder);
        UpdateStatus("已打开输出目录。", $"路径：{outputFolder}");
    }

    private void OpenPrinterConfigFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguredFolder(PrinterConfigPathTextBox.Text, "打印机配置目录");
    }

    private void OpenPlotStyleFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguredFolder(PlotStylePathTextBox.Text, "打印样式表目录");
    }

    private void OpenConfiguredFolder(string? folderPath, string label)
    {
        var path = (folderPath ?? "").Trim();
        if (path.Length == 0)
        {
            UpdateStatus($"{label}为空，无法打开。");
            return;
        }

        if (!Directory.Exists(path))
        {
            UpdateStatus($"{label}不存在：{path}");
            return;
        }

        OpenOutputFolder(path);
        UpdateStatus($"已打开{label}。", $"路径：{path}");
    }

    private void OutputFolderCombo_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        BeginOutputFolderEdit();
        e.Handled = true;
    }

    private void OutputFolderCombo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!OutputFolderCombo.IsEditable)
            return;

        if (e.Key == Key.Enter)
        {
            EndOutputFolderEdit(commitChanges: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            EndOutputFolderEdit(commitChanges: false);
            e.Handled = true;
        }
    }

    private void OutputFolderCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (OutputFolderCombo.IsEditable && !OutputFolderCombo.IsKeyboardFocusWithin)
            EndOutputFolderEdit(commitChanges: true);
    }

    private void DataGrid_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid dataGrid)
            return;

        var scrollViewer = FindVisualChild<ScrollViewer>(dataGrid);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
            return;

        var isScrollingUp = e.Delta > 0;
        if ((isScrollingUp && scrollViewer.VerticalOffset <= 0) ||
            (!isScrollingUp && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight))
        {
            return;
        }

        if (isScrollingUp)
            scrollViewer.LineUp();
        else
            scrollViewer.LineDown();

        e.Handled = true;
    }

    // ── Print buttons ─────────────────────────────────────────────────────────

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var previewOptions = BuildPlotOptions();
        var selectedFrames = GetSelectedFramesInPlotOrder(previewOptions);
        if (selectedFrames.Count == 0)
        {
            UpdateStatus("请先勾选并读取图纸。");
            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        PreviewFrame(
            document,
            selectedFrames[0],
            selectedFrames.Count > 1
                ? "提示：当前预览定位到打印顺序中的首个勾选图纸。"
                : "提示：关闭预览后，会自动返回当前批量打印面板。",
            previewOptions);
    }

    private void FramePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_isPrinting)
            return;

        if (sender is not FrameworkElement { DataContext: FrameInfo previewFrame })
        {
            UpdateStatus("未找到可预览的图纸。");
            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        FramesGrid.SelectedItem = previewFrame;

        PreviewFrame(
            document,
            previewFrame,
            "提示：关闭预览后，会自动返回当前批量打印面板。");
    }

    private void ShowRangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrinting)
            return;

        var selectedFrames = GetSelectedFramesInPlotOrder(BuildPlotOptions());
        if (selectedFrames.Count == 0)
        {
            UpdateStatus("请先勾选并读取图纸。");
            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        UpdateStatus("正在显示打印范围…", "提示：面板会先隐藏，重新执行 V_QQQ 可返回批量打印面板。");
        Hide();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    Exception? displayError = null;
                    var displayResult = new QqqFrameRangeDisplayResult();
                    try
                    {
                        if (!QqqPlotService.TryShowFrameRanges(document, selectedFrames, out displayResult, out var errorMessage))
                            displayError = new InvalidOperationException(errorMessage);
                    }
                    catch (Exception ex)
                    {
                        displayError = ex;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (displayError != null)
                        {
                            UnregisterRangeDisplaySpaceRestore();
                            EnsureShown();
                            UpdateStatus($"显示打印范围失败：{displayError.Message}");
                            return;
                        }

                        var shownFrame = selectedFrames.FirstOrDefault(
                            x => string.Equals(x.LayoutName, displayResult.LayoutName, StringComparison.OrdinalIgnoreCase));
                        if (shownFrame != null)
                            FramesGrid.SelectedItem = shownFrame;

                        RegisterRangeDisplaySpaceRestore();
                        UpdateStatus(
                            BuildFrameRangeDisplayMessage(displayResult),
                            BuildFrameRangeDisplayHint(displayResult));
                    }));
                },
                null);
        }
        catch (Exception ex)
        {
            UnregisterRangeDisplaySpaceRestore();
            EnsureShown();
            UpdateStatus($"显示打印范围失败：{ex.Message}");
            C_toolsDiagnostics.LogNonFatal("V_QQQ 显示打印范围失败", ex);
        }
    }

    private void PreviewFrame(Document document, FrameInfo previewFrame, string closingHint, QqqPlotOptions? previewOptions = null)
    {
        QqqPlotService.ClearFrameRangeDisplay();
        previewOptions ??= BuildPlotOptions();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    Exception? previewError = null;
                    IReadOnlyList<string> previewInfoMessages = Array.Empty<string>();
                    var previewHint = closingHint;
                    try
                    {
                        if (QqqPlotService.TryPrepareFrameForNativePreview(
                                document,
                                previewFrame,
                                previewOptions,
                                out previewInfoMessages,
                                out var previewSetupError))
                        {
                            previewHint = BuildPlotResultHint(closingHint, previewInfoMessages);
                        }
                        else
                        {
                            previewError = new InvalidOperationException(previewSetupError);
                        }
                    }
                    catch (Exception ex)
                    {
                        previewError = ex;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (previewError != null)
                        {
                            if (ShouldUsePdfPreviewFallback(previewError.Message))
                            {
                                OpenPdfPreviewFallback(
                                    document,
                                    previewFrame,
                                    previewOptions,
                                    previewInfoMessages,
                                    closingHint,
                                    previewError.Message);
                                return;
                            }

                            UpdateStatus(
                                $"打开预览失败：{previewError.Message}",
                                BuildPlotResultHint("提示：请先在 V_YYY 的“打印与保存”页确认有效 PDF 输出设备。", previewInfoMessages));
                            return;
                        }

                        OpenCadNativeCommand(
                            document,
                            "PREVIEW",
                            "_.PREVIEW\n",
                            $"已定位到“{previewFrame.FrameName}”，并按 V_YYY“打印与保存”设置同步预览布局，正在打开 CAD 原生打印预览…",
                            "已退出 CAD 原生打印预览。",
                            previewHint,
                            "打开 CAD 原生打印预览");
                    }));
                },
                null);
        }
        catch (Exception ex)
        {
            UpdateStatus($"打开 CAD 原生打印预览失败：{ex.Message}");
            C_toolsDiagnostics.LogNonFatal("V_QQQ 打开 CAD 原生打印预览失败", ex);
        }
    }

    private static bool ShouldUsePdfPreviewFallback(string? errorMessage)
    {
        return string.Equals((errorMessage ?? "").Trim(), "eInvalidInput", StringComparison.OrdinalIgnoreCase);
    }

    private async void OpenPdfPreviewFallback(
        Document document,
        FrameInfo previewFrame,
        QqqPlotOptions previewOptions,
        IReadOnlyList<string> previewInfoMessages,
        string closingHint,
        string nativePreviewErrorMessage)
    {
        var tempPreviewFolder = Path.Combine(Path.GetTempPath(), "C_tools_V_QQQ", "Preview");
        var fallbackHint = BuildPlotResultHint(
            $"提示：CAD 原生预览返回 {nativePreviewErrorMessage}，已自动改用临时 PDF 预览。",
            previewInfoMessages);

        try
        {
            SetPrintingState(true);
            UpdateStatus(
                $"正在生成“{previewFrame.FrameName}”的临时 PDF 预览…",
                fallbackHint);

            Directory.CreateDirectory(tempPreviewFolder);
            var previewPdfOptions = CreatePdfPreviewOptions(previewOptions, tempPreviewFolder);
            var previewFile = await RunSinglePreviewPlotAsync(document, previewFrame, previewPdfOptions, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(previewFile) || !File.Exists(previewFile))
            {
                UpdateStatus("打开预览失败：未生成可用的临时 PDF 预览。", fallbackHint);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = previewFile,
                UseShellExecute = true
            });

            UpdateStatus(
                $"已打开临时 PDF 预览：{Path.GetFileName(previewFile)}",
                BuildPlotResultHint("提示：当前图纸的 CAD 原生预览不可用，已改用临时 PDF 预览。", previewInfoMessages));
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 打开临时 PDF 预览失败", ex);
            UpdateStatus(
                $"打开预览失败：{nativePreviewErrorMessage}",
                BuildPlotResultHint($"提示：尝试生成临时 PDF 预览也失败：{ex.Message}", previewInfoMessages));
        }
        finally
        {
            SetPrintingState(false);
        }
    }

    private async void StartPrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrinting)
            return;

        QqqPlotService.ClearFrameRangeDisplay();

        if (_frames.Count == 0)
        {
            UpdateStatus("请先读取图纸后再打印。");
            return;
        }

        var options = BuildPlotOptions();
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            options.OutputFolder = _suggestedOutputFolder;

        var selectedFrames = GetSelectedFramesInPlotOrder(options);
        if (selectedFrames.Count == 0)
        {
            UpdateStatus("没有可打印的图纸，请先在上方识别列表勾选并读取图纸。");
            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        _printCancellation?.Dispose();
        _printCancellation = new CancellationTokenSource();
        SetPrintingState(true);

        try
        {
            foreach (var frame in selectedFrames)
            {
                frame.Status = "待打印";
                frame.OutputFile = "";
            }

            UpdateStatus($"开始生成合并 PDF… 共 {selectedFrames.Count} 张图纸。");
            var result = await RunCombinedPlotAsync(document, selectedFrames, options, _printCancellation.Token).ConfigureAwait(true);

            if (result.SuccessCount > 0 && !string.IsNullOrWhiteSpace(result.OutputFile))
            {
                foreach (var frame in selectedFrames)
                    frame.OutputFile = result.OutputFile;

                var suffix = result.FailureCount > 0
                    ? $"，失败 {result.FailureCount} 张"
                    : "";
                UpdateStatus(
                    $"合并 PDF 打印完成，成功 {result.SuccessCount} 张{suffix}。",
                    BuildPlotResultHint($"输出文件：{result.OutputFile}", result.InfoMessages));
                OpenOutputFolder(result.OutputFolder, result.OutputFile);
            }
            else if (result.SuccessCount > 0 && !string.IsNullOrWhiteSpace(result.FallbackOutputFolder))
            {
                var pageFailureSuffix = result.FailureCount > 0
                    ? $"，另有 {result.FailureCount} 张图纸打印失败"
                    : "";
                var mergeMessage = string.IsNullOrWhiteSpace(result.MergeErrorMessage)
                    ? "合并 PDF 失败"
                    : $"合并 PDF 失败：{result.MergeErrorMessage}";

                UpdateStatus(
                    $"{mergeMessage}，已保留 {result.FallbackOutputFileCount} 张单页 PDF{pageFailureSuffix}。",
                    BuildPlotResultHint($"输出目录：{result.FallbackOutputFolder}", result.InfoMessages));
                OpenOutputFolder(result.FallbackOutputFolder);
            }
            else
            {
                var error = result.ErrorMessages.FirstOrDefault() ?? "未生成输出文件。";
                UpdateStatus($"打印失败：{error}", BuildPlotResultHint("", result.InfoMessages));
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("已取消打印。");
        }
        catch (Exception ex)
        {
            UpdateStatus($"打印出错：{ex.Message}");
            C_toolsDiagnostics.LogNonFatal("StartPrintButton_Click", ex);
        }
        finally
        {
            SetPrintingState(false);
            _printCancellation?.Dispose();
            _printCancellation = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void BeginCaptureRecognitionTemplates(bool replaceExisting)
    {
        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        QqqPlotService.ClearFrameRangeDisplay();
        var isBlockMode = IsBlockRecognitionMode();
        var modeLabel = isBlockMode ? "图框图块" : "图框图层";
        UpdateStatus($"请在 CAD 中选择{modeLabel}…");
        Hide();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var activeDocument = AcAp.DocumentManager.MdiActiveDocument;
                    var result = activeDocument == null
                        ? new QqqRecognitionTemplateCaptureResult { Message = "当前没有活动图纸。" }
                        : isBlockMode
                            ? QqqFrameSelectionService.CaptureBlockTemplates(activeDocument)
                            : QqqFrameSelectionService.CaptureLayerTemplates(activeDocument);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        EnsureShown();
                        if (replaceExisting)
                            ClearTemplatesAndSheets();

                        if (result.Items.Count > 0)
                            MergeCapturedTemplates(result.Items);

                        PersistRecognitionTemplatesForCurrentMode();
                        UpdateStatus(
                            result.Message,
                            _templates.Count == 0
                                ? "提示：先从 CAD 里选择图框对象，把名称记录到上方列表。"
                                : "提示：勾选上方列表后，系统会读取当前图纸中对应的图纸信息。");
                    }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 记录识别项失败", ex);
            EnsureShown();
            UpdateStatus($"记录{modeLabel}失败：{ex.Message}");
        }
    }

    private void AddBlockSheets_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrinting)
        {
            UpdateStatus("正在打印，暂时不能添加图纸。");
            return;
        }

        if (!IsBlockRecognitionMode())
        {
            UpdateStatus(
                "请先切换到“选择图框图块”。",
                "提示：圆形按钮只会从左上图块列表中的图块里选择打印图纸。");
            return;
        }

        var blockNames = GetListedTemplateNames();
        if (blockNames.Count == 0)
        {
            UpdateStatus(
                "请先在左上图块列表中添加图块。",
                "提示：点击上方加号从 CAD 记录图块后，再点击 ○ 选择要打印的图块。");
            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        QqqPlotService.ClearFrameRangeDisplay();
        UpdateStatus(
            "请在 CAD 中选择要打印的图块…",
            $"范围：左上图块列表中的 {blockNames.Count} 个图块。");
        Hide();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var activeDocument = AcAp.DocumentManager.MdiActiveDocument;
                    var result = activeDocument == null
                        ? new QqqFrameSelectionResult { Message = "当前没有活动图纸。" }
                        : QqqFrameSelectionService.CaptureBlockFramesByNames(activeDocument, blockNames);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        EnsureShown();
                        AppendCapturedFrames(result);
                    }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 选择打印图块添加图纸失败", ex);
            EnsureShown();
            UpdateStatus($"添加图块图纸失败：{ex.Message}");
        }
    }

    private void AddWindowSheets_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrinting)
        {
            UpdateStatus("正在打印，暂时不能添加图纸。");
            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            UpdateStatus("当前没有活动图纸。");
            return;
        }

        QqqPlotService.ClearFrameRangeDisplay();
        UpdateStatus("请在 CAD 中框选要添加到图纸列表的窗口范围…");
        Hide();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var activeDocument = AcAp.DocumentManager.MdiActiveDocument;
                    var result = activeDocument == null
                        ? new QqqFrameSelectionResult { Message = "当前没有活动图纸。" }
                        : QqqFrameSelectionService.CaptureWindowFrames(activeDocument);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        EnsureShown();
                        AppendCapturedFrames(result);
                    }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 框选窗口添加图纸失败", ex);
            EnsureShown();
            UpdateStatus($"添加窗口图纸失败：{ex.Message}");
        }
    }

    private void RefreshFrames(bool isInitialLoad, bool controlDisplayedSheetSet = false)
    {
        var selectedNames = GetSelectedTemplateNames();
        if (selectedNames.Count == 0)
        {
            _frames.ReplaceAll(Array.Empty<FrameInfo>());
            var persistErrorMessage = controlDisplayedSheetSet ? PersistDisplayedFrames() : "";
            if (!isInitialLoad)
            {
                UpdateStatus(
                    _templates.Count == 0
                        ? "请先从 CAD 记录识别项。"
                        : "请先在上方识别列表勾选需要读取的项。",
                    BuildSheetListHint(
                        "提示：勾选后，系统会读取当前图纸中对应的图纸信息。",
                        persistErrorMessage));
            }

            return;
        }

        var document = AcAp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            _frames.ReplaceAll(Array.Empty<FrameInfo>());
            var persistErrorMessage = controlDisplayedSheetSet ? PersistDisplayedFrames() : "";
            if (!isInitialLoad)
                UpdateStatus("当前没有活动图纸。", persistErrorMessage);
            return;
        }

        var requestVersion = Interlocked.Increment(ref _sheetRefreshVersion);
        var isBlockMode = IsBlockRecognitionMode();
        UpdateStatus($"正在读取 {selectedNames.Count} 项对应的图纸信息…", "提示：会读取当前图纸中符合勾选项的图纸。");

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var activeDocument = AcAp.DocumentManager.MdiActiveDocument;
                    var result = activeDocument == null
                        ? new QqqFrameSelectionResult { Message = "当前没有活动图纸。" }
                        : isBlockMode
                            ? QqqFrameSelectionService.RecognizeFramesByBlockNames(activeDocument, selectedNames)
                            : QqqFrameSelectionService.RecognizeFramesByLayerNames(activeDocument, selectedNames);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (requestVersion != _sheetRefreshVersion)
                            return;

                        ApplyFrameResult(result, controlDisplayedSheetSet);
                    }));
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 读取图纸失败", ex);
            if (!isInitialLoad)
                UpdateStatus($"读取图纸失败：{ex.Message}");
        }
    }

    private void ApplyFrameResult(QqqFrameSelectionResult result, bool controlDisplayedSheetSet)
    {
        QqqPlotService.ClearFrameRangeDisplay();
        _frames.ReplaceAll(result.Frames);

        var persistErrorMessage = "";
        if (controlDisplayedSheetSet)
            persistErrorMessage = PersistDisplayedFrames();
        else
        {
            persistErrorMessage = PersistDisplayedFrames();
        }

        UpdateFrameSelectionHeader();

        if (_frames.Count == 0)
        {
            UpdateStatus(
                result.Message,
                BuildSheetListHint(
                    "提示：确认上方勾选的图框图块或图框图层在当前图纸中确实存在。",
                    persistErrorMessage));
            return;
        }

        UpdateStatus(
            result.Message,
            BuildSheetListHint(
                "提示：下方图纸来自上方勾选项；预览和开始打印会按打印顺序执行。",
                persistErrorMessage));
    }

    private void AppendCapturedFrames(QqqFrameSelectionResult result)
    {
        QqqPlotService.ClearFrameRangeDisplay();

        if (result.Frames.Count == 0)
        {
            UpdateStatus(
                result.Message,
                _frames.Count == 0
                    ? "提示：点击 ○ 可选择左上列表中的图块，点击 □ 可框选窗口，直接追加到图纸列表。"
                    : $"提示：当前图纸列表保留了 {_frames.Count} 张已有图纸。");
            return;
        }

        var existingKeys = _frames
            .Where(static x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(static x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var appendedCount = 0;
        var framesToAppend = new List<FrameInfo>();
        foreach (var frame in result.Frames.Where(static x => x != null))
        {
            if (!existingKeys.Add(frame.Key))
                continue;

            frame.AddedOrder = _frames.Count + framesToAppend.Count + 1;
            frame.IsSelected = true;
            frame.Status = "待打印";
            framesToAppend.Add(frame);
            appendedCount++;
        }

        _frames.AddRange(framesToAppend);

        if (appendedCount == 0)
        {
            UpdateStatus(
                "所选图纸已全部存在于图纸列表中。",
                $"提示：当前共保留 {_frames.Count} 张图纸，没有重复添加。");
            return;
        }

        var duplicateCount = result.Frames.Count - appendedCount;
        var mergeMessage = duplicateCount > 0
            ? $"已追加 {appendedCount} 张图纸，跳过 {duplicateCount} 张重复图纸。"
            : $"已追加 {appendedCount} 张图纸。";

        UpdateStatus(
            mergeMessage,
            BuildSheetListHint(
                $"提示：当前图纸列表共 {_frames.Count} 张图纸；预览和开始打印会按打印顺序执行。",
                PersistDisplayedFrames()));
    }

    private void FrameSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFrameSelection)
            return;

        QqqPlotService.ClearFrameRangeDisplay();
        UpdateFrameSelectionHeader();
        ScheduleDisplayedFrameListPersist();
    }

    private void ToggleAllFrames_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFrameSelection)
            return;

        SetAllFramesSelected(true);
    }

    private void ToggleAllFrames_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFrameSelection)
            return;

        SetAllFramesSelected(false);
    }

    private void SetAllFramesSelected(bool isSelected)
    {
        if (_frames.Count == 0)
        {
            UpdateFrameSelectionHeader();
            return;
        }

        _isUpdatingFrameSelection = true;
        try
        {
            foreach (var frame in _frames)
                frame.IsSelected = isSelected;
        }
        finally
        {
            _isUpdatingFrameSelection = false;
        }

        QqqPlotService.ClearFrameRangeDisplay();
        UpdateFrameSelectionHeader();
        ScheduleDisplayedFrameListPersist();
    }

    private void RenumberFrameAddedOrders()
    {
        for (var i = 0; i < _frames.Count; i++)
            _frames[i].AddedOrder = i + 1;
    }

    private void UpdateFrameSelectionHeader()
    {
        var selectAllFramesCheckBox = FindNamedVisualChild<System.Windows.Controls.CheckBox>(FramesGrid, "SelectAllFramesCheckBox");
        if (selectAllFramesCheckBox == null)
            return;

        var selectedCount = _frames.Count(static x => x.IsSelected);
        bool? nextState = _frames.Count switch
        {
            0 => false,
            _ when selectedCount == 0 => false,
            _ when selectedCount == _frames.Count => true,
            _ => null
        };

        _isUpdatingFrameSelection = true;
        try
        {
            selectAllFramesCheckBox.IsChecked = nextState;
        }
        finally
        {
            _isUpdatingFrameSelection = false;
        }
    }

    private void MergeCapturedTemplates(IEnumerable<QqqRecognitionTemplateCaptureItem> items)
    {
        var existingMap = _templates.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items
                     .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
                     .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (existingMap.TryGetValue(item.Name, out var existing))
            {
                existing.SizeText = MergeTemplateSizeText(existing.SizeText, item.SizeText);
                continue;
            }

            var template = new FrameTemplateInfo
            {
                Name = item.Name.Trim(),
                SizeText = NormalizeTemplateSizeText(item.SizeText),
                IsSelected = true
            };
            _templates.Add(template);
            existingMap[template.Name] = template;
        }

        SortTemplates();
    }

    private void SortTemplates()
    {
        var ordered = _templates
            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var currentIndex = _templates.IndexOf(ordered[i]);
            if (currentIndex >= 0 && currentIndex != i)
                _templates.Move(currentIndex, i);
        }
    }

    private void LoadRecognitionTemplates()
    {
        _recognitionTemplatesLoaded = false;
        _recognitionTemplateState = QqqRecognitionTemplateStore.Load();
        ApplyRecognitionMode(_recognitionTemplateState.LastModeKey);
        LoadRecognitionTemplatesForCurrentMode(isInitialLoad: true);
        _recognitionTemplatesLoaded = true;
    }

    private void LoadRecognitionTemplatesForCurrentMode(bool isInitialLoad)
    {
        _isLoadingRecognitionTemplates = true;
        try
        {
            var templates = _recognitionTemplateState.GetTemplates(GetRecognitionModeKey())
                .Select(entry => new FrameTemplateInfo
                {
                    Name = entry.Name,
                    SizeText = NormalizeTemplateSizeText(entry.SizeText),
                    IsSelected = entry.IsSelected
                })
                .ToList();
            _templates.ReplaceAll(templates);

            SortTemplates();
        }
        finally
        {
            _isLoadingRecognitionTemplates = false;
        }

        if (!isInitialLoad)
        {
            UpdateStatus(
                "已切换识别方式。",
                "提示：点击刷新读取勾选项，或点击 ○ 选择图块、点击 □ 框选窗口手动追加图纸。");
        }
    }

    private void PersistRecognitionTemplatesForCurrentMode()
    {
        if (!_recognitionTemplatesLoaded)
            return;

        var modeKey = GetRecognitionModeKey();
        _recognitionTemplateState.LastModeKey = modeKey;
        _recognitionTemplateState.SetTemplates(modeKey, BuildRecognitionTemplateEntriesFromUi());
        QqqRecognitionTemplateStore.Save(_recognitionTemplateState);
    }

    private IReadOnlyList<QqqRecognitionTemplateEntry> BuildRecognitionTemplateEntriesFromUi()
    {
        return _templates
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(static x => new QqqRecognitionTemplateEntry
            {
                Name = x.Name.Trim(),
                SizeText = NormalizeTemplateSizeText(x.SizeText),
                IsSelected = x.IsSelected
            })
            .ToList();
    }

    private void ApplyRecognitionMode(string? modeKey)
    {
        _isApplyingRecognitionMode = true;
        try
        {
            switch (QqqRecognitionTemplateStore.NormalizeModeKey(modeKey))
            {
                case QqqRecognitionTemplateStore.ModeLayer:
                    RecognizeByLayerButton.IsChecked = true;
                    break;
                default:
                    RecognizeByBlockButton.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _isApplyingRecognitionMode = false;
        }

        UpdateRecognitionTemplateHeaders();
    }

    private void UpdateRecognitionTemplateHeaders()
    {
        if (TemplateGrid == null)
            return;

        TemplateNameHeader.Text = IsBlockRecognitionMode() ? "图块名" : "图层名";

        if (TemplateGrid.Columns.Count > 1)
            TemplateGrid.Columns[1].Header = "尺寸";
    }

    private IReadOnlyList<string> GetSelectedTemplateNames()
    {
        return _templates
            .Where(static x => x.IsSelected && !string.IsNullOrWhiteSpace(x.Name))
            .Select(static x => x.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> GetListedTemplateNames()
    {
        return _templates
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(static x => x.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsBlockRecognitionMode() => RecognizeByBlockButton.IsChecked == true;

    private string GetRecognitionModeKey()
    {
        if (IsBlockRecognitionMode())
            return QqqRecognitionTemplateStore.ModeBlock;

        return QqqRecognitionTemplateStore.ModeLayer;
    }

    private void ClearTemplatesAndSheets()
    {
        _templates.ReplaceAll(Array.Empty<FrameTemplateInfo>());
        _frames.ReplaceAll(Array.Empty<FrameInfo>());
        PersistDisplayedFrames();
    }

    private void RefreshSavedSheetSetTabs(string? selectTabName = null)
    {
        var documentPath = GetCurrentDocumentName();
        var definitions = QqqSavedSheetSetStore.Load(documentPath);

        _isUpdatingSavedSheetSetTabs = true;
        try
        {
            var tabs = new List<SavedSheetSetTabInfo>();

            foreach (var definition in definitions)
            {
                tabs.Add(new SavedSheetSetTabInfo
                {
                    DisplayName = definition.Name,
                    Definition = new QqqSavedSheetSetDefinition
                    {
                        Name = definition.Name,
                        HasDddPanelListsSnapshot = definition.HasDddPanelListsSnapshot,
                        DddPanelListsSnapshot = definition.DddPanelListsSnapshot,
                        HasDddTextEditHistorySnapshot = definition.HasDddTextEditHistorySnapshot,
                        DddTextEditHistorySnapshot = definition.DddTextEditHistorySnapshot,
                        Frames = definition.Frames.Select(CloneFrameInfo).ToList()
                    }
                });
            }

            _savedSheetSetTabs.ReplaceAll(tabs);

            UpdateSavedSheetSetTabsVisibility();

            var selectedIndex = _savedSheetSetTabs.Count > 0 ? 0 : -1;
            if (!string.IsNullOrWhiteSpace(selectTabName))
            {
                var matched = _savedSheetSetTabs
                    .Select((tab, index) => new { tab, index })
                    .FirstOrDefault(x => string.Equals(x.tab.DisplayName, selectTabName, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                    selectedIndex = matched.index;
            }

            SavedSheetSetTabs.SelectedIndex = selectedIndex;
        }
        finally
        {
            _isUpdatingSavedSheetSetTabs = false;
        }
    }

    private void UpdateSavedSheetSetTabsVisibility()
    {
        if (SavedSheetSetTabs == null)
            return;

        SavedSheetSetTabs.Visibility = _savedSheetSetTabs.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SelectDefaultSheetSetTab()
    {
        if (SavedSheetSetTabs == null)
            return;

        _isUpdatingSavedSheetSetTabs = true;
        try
        {
            SavedSheetSetTabs.SelectedIndex = _savedSheetSetTabs.Count > 0 ? 0 : -1;
        }
        finally
        {
            _isUpdatingSavedSheetSetTabs = false;
        }
    }

    private void RestoreWorkingFramesForCurrentDocument()
    {
        QqqPlotService.ClearFrameRangeDisplay();
        SelectDefaultSheetSetTab();
        _frames.ReplaceAll(Array.Empty<FrameInfo>());

        var documentPath = GetCurrentDocumentName();
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            UpdateFrameSelectionHeader();
            UpdateStatus(
                "当前没有活动图纸。",
                "提示：打开图纸后可恢复 V_QQQ 上次图纸列表。");
            return;
        }

        var selectedDefinition = (SavedSheetSetTabs.SelectedItem as SavedSheetSetTabInfo)?.Definition;
        var restoredFrames = (selectedDefinition?.Frames ?? Enumerable.Empty<FrameInfo>())
            .Select(CloneFrameInfo)
            .ToList();
        _frames.ReplaceAll(restoredFrames);

        UpdateFrameSelectionHeader();

        if (_frames.Count > 0)
        {
            UpdateStatus(
                $"已恢复上次图纸列表，共 {_frames.Count} 张图纸。",
                "提示：预览、显示范围和开始打印都会按 V_YYY“打印与保存”中的打印顺序执行。");
            return;
        }

        UpdateStatus(
            "已载入识别项。",
            "提示：点击刷新读取勾选项，或点击 ○ 选择图块、点击 □ 框选窗口手动追加图纸。");
    }

    private string PersistDisplayedFrames()
    {
        CancelPendingFrameListPersist();
        return PersistFrameListRequest(CreateDisplayedFrameListPersistRequest());
    }

    private void ScheduleDisplayedFrameListPersist()
    {
        _pendingFrameListPersist = CreateDisplayedFrameListPersistRequest();
        _frameSelectionPersistTimer.Stop();
        _frameSelectionPersistTimer.Start();
    }

    private string PersistWorkingFrames()
    {
        CancelPendingFrameListPersist();
        return PersistFrameListRequest(CreateWorkingFrameListPersistRequest());
    }

    private void FrameSelectionPersistTimer_Tick(object? sender, EventArgs e)
    {
        FlushPendingFrameListPersist(showStatusOnError: true);
    }

    private string FlushPendingFrameListPersist(bool showStatusOnError = false)
    {
        _frameSelectionPersistTimer.Stop();

        var request = _pendingFrameListPersist;
        _pendingFrameListPersist = null;
        if (request == null)
            return "";

        var errorMessage = PersistFrameListRequest(request);
        if (showStatusOnError && !string.IsNullOrWhiteSpace(errorMessage))
            UpdateStatus("图纸勾选状态已更新。", errorMessage);

        return errorMessage;
    }

    private void CancelPendingFrameListPersist()
    {
        _frameSelectionPersistTimer.Stop();
        _pendingFrameListPersist = null;
    }

    private FrameListPersistRequest CreateDisplayedFrameListPersistRequest()
    {
        if (SavedSheetSetTabs?.SelectedItem is SavedSheetSetTabInfo { IsWorkingSet: false, Definition: not null } tab)
            return CreateSavedSheetSetFrameListPersistRequest(tab);

        var firstTab = _savedSheetSetTabs.FirstOrDefault(static x => x.Definition != null);
        return firstTab != null
            ? CreateSavedSheetSetFrameListPersistRequest(firstTab)
            : CreateWorkingFrameListPersistRequest();
    }

    private FrameListPersistRequest CreateWorkingFrameListPersistRequest()
    {
        return new FrameListPersistRequest
        {
            TargetKind = FrameListPersistTargetKind.WorkingSet,
            DocumentPath = GetCurrentDocumentName(),
            Frames = SnapshotFrames()
        };
    }

    private FrameListPersistRequest CreateSavedSheetSetFrameListPersistRequest(SavedSheetSetTabInfo tab)
    {
        var frames = SnapshotFrames();
        tab.Definition!.Frames = frames.Select(CloneFrameInfo).ToList();

        return new FrameListPersistRequest
        {
            TargetKind = FrameListPersistTargetKind.SavedSheetSet,
            DocumentPath = GetCurrentDocumentName(),
            SavedSheetSetName = tab.DisplayName,
            SavedSheetSetDefinitions = BuildSavedSheetSetDefinitionsFromTabs()
        };
    }

    private List<FrameInfo> SnapshotFrames()
    {
        return _frames.Select(CloneFrameInfo).ToList();
    }

    private static string PersistFrameListRequest(FrameListPersistRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentPath) && request.TargetKind != FrameListPersistTargetKind.SavedSheetSet)
        {
            return "提示：当前没有活动图纸，未能保存 V_QQQ 当前图纸列表。";
        }

        if (request.TargetKind == FrameListPersistTargetKind.SavedSheetSet)
        {
            return QqqSavedSheetSetStore.Save(request.DocumentPath, request.SavedSheetSetDefinitions)
                ? ""
                : $"提示：未能保存 TAD 标签“{request.SavedSheetSetName}”的图纸列表变更。";
        }

        return QqqSavedSheetSetStore.SaveWorkingSet(request.DocumentPath, request.Frames)
            ? ""
            : "提示：未能保存 V_QQQ 当前图纸列表。";
    }

    private List<QqqSavedSheetSetDefinition> BuildSavedSheetSetDefinitionsFromTabs()
    {
        return _savedSheetSetTabs
            .Where(static x => !x.IsWorkingSet && x.Definition != null)
            .Select(static x => new QqqSavedSheetSetDefinition
            {
                Name = x.Definition!.Name,
                HasDddPanelListsSnapshot = x.Definition.HasDddPanelListsSnapshot,
                DddPanelListsSnapshot = x.Definition.DddPanelListsSnapshot,
                HasDddTextEditHistorySnapshot = x.Definition.HasDddTextEditHistorySnapshot,
                DddTextEditHistorySnapshot = x.Definition.DddTextEditHistorySnapshot,
                Frames = x.Definition.Frames.Select(CloneFrameInfo).ToList()
            })
            .ToList();
    }

    private void BeginSavedSheetSetRename(SavedSheetSetTabInfo tab)
    {
        FlushPendingFrameListPersist(showStatusOnError: true);

        foreach (var otherTab in _savedSheetSetTabs.Where(static x => x.IsRenaming && !x.IsWorkingSet))
        {
            if (!ReferenceEquals(otherTab, tab))
                CommitSavedSheetSetRename(otherTab);
        }

        tab.EditName = tab.DisplayName;
        tab.IsRenaming = true;
    }

    private void CancelSavedSheetSetRename(SavedSheetSetTabInfo tab)
    {
        if (!tab.IsRenaming)
            return;

        tab.EditName = tab.DisplayName;
        tab.IsRenaming = false;
    }

    private void CommitSavedSheetSetRename(SavedSheetSetTabInfo tab)
    {
        if (!tab.IsRenaming || tab.IsWorkingSet || tab.Definition == null)
            return;

        var originalName = (tab.DisplayName ?? "").Trim();
        var renamed = (tab.EditName ?? "").Trim();
        tab.IsRenaming = false;

        if (string.Equals(renamed, originalName, StringComparison.Ordinal))
        {
            tab.EditName = originalName;
            return;
        }

        if (renamed.Length == 0)
        {
            tab.EditName = originalName;
            UpdateStatus($"TAD 标签“{originalName}”重命名失败：名称不能为空。");
            return;
        }

        if (_savedSheetSetTabs.Any(x =>
                !ReferenceEquals(x, tab) &&
                !x.IsWorkingSet &&
                !string.IsNullOrWhiteSpace(x.DisplayName) &&
                string.Equals(x.DisplayName.Trim(), renamed, StringComparison.OrdinalIgnoreCase)))
        {
            tab.EditName = originalName;
            UpdateStatus($"TAD 标签“{originalName}”重命名失败：名称“{renamed}”已存在。");
            return;
        }

        var documentPath = GetCurrentDocumentName();
        tab.DisplayName = renamed;
        tab.Definition.Name = renamed;
        tab.EditName = renamed;

        if (!QqqSavedSheetSetStore.Save(documentPath, BuildSavedSheetSetDefinitionsFromTabs()))
        {
            tab.DisplayName = originalName;
            tab.Definition.Name = originalName;
            tab.EditName = originalName;
            UpdateStatus($"重命名 TAD 标签失败：未能保存“{renamed}”。");
            return;
        }

        UpdateStatus(
            $"已将 TAD 标签“{originalName}”重命名为“{renamed}”。",
            "提示：双击 TAD 标签名称可再次重命名。");
    }

    private string CreateNextSavedSheetSetName(IEnumerable<QqqSavedSheetSetDefinition> definitions)
    {
        var existingNames = definitions
            .Select(static x => x.Name)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"TAD {i}";
            if (!existingNames.Contains(candidate))
                return candidate;
        }

        return $"TAD {DateTime.Now:HHmmss}";
    }

    private void ApplySavedSheetSet(SavedSheetSetTabInfo tab)
    {
        QqqPlotService.ClearFrameRangeDisplay();
        var snapshotRestoreOk = QqqSavedSheetSetStore.RestoreTextSnapshots(tab.Definition!);

        _frames.ReplaceAll(tab.Definition!.Frames.Select(CloneFrameInfo));

        UpdateFrameSelectionHeader();

        var restoreHint = tab.Definition.HasAttachedTextSnapshots
            ? snapshotRestoreOk
                ? "；并已同步恢复 DD/F_ED 文字记录"
                : "；但 DD/F_ED 文字记录恢复失败"
            : "";

        UpdateStatus(
            $"已切换到 TAD 标签“{tab.DisplayName}”。",
            _frames.Count == 0
                ? "提示：该标签当前没有保存图纸。"
                : $"提示：已恢复 {_frames.Count} 张图纸，可直接预览或开始打印{restoreHint}。");
    }

    private static FrameInfo CloneFrameInfo(FrameInfo source)
    {
        return new FrameInfo
        {
            AddedOrder = source.AddedOrder,
            Key = source.Key,
            LayoutName = source.LayoutName,
            SpaceName = source.SpaceName,
            FrameType = source.FrameType,
            FrameName = source.FrameName,
            LayerName = source.LayerName,
            BlockName = source.BlockName,
            RecognitionSource = source.RecognitionSource,
            HandleText = source.HandleText,
            Width = source.Width,
            Height = source.Height,
            CenterX = source.CenterX,
            CenterY = source.CenterY,
            WcsExtents = source.WcsExtents,
            IsSelected = source.IsSelected,
            PaperSize = source.PaperSize,
            PlotScale = source.PlotScale,
            Status = "待打印",
            OutputFile = ""
        };
    }

    private static string MergeTemplateSizeText(string current, string incoming)
    {
        var left = NormalizeTemplateSizeText(current);
        var right = NormalizeTemplateSizeText(incoming);

        if (left.Length == 0)
            return right;
        if (right.Length == 0)
            return left;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return left;
        if (string.Equals(left, MultiSizeText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(right, MultiSizeText, StringComparison.OrdinalIgnoreCase))
            return MultiSizeText;

        return MultiSizeText;
    }

    private static string NormalizeTemplateSizeText(string? sizeText)
    {
        var value = (sizeText ?? "").Trim();
        return value == "-" ? "" : value;
    }

    private async Task<QqqPlotRunResult> RunCombinedPlotAsync(
        Document document,
        IReadOnlyList<FrameInfo> selectedFrames,
        QqqPlotOptions options,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<QqqPlotRunResult>();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    try
                    {
                        var result = QqqPlotService.PlotFramesToCombinedPdf(
                            document,
                            selectedFrames,
                            options,
                            (index, status, outputPath) =>
                            {
                                if (index < 0 || index >= selectedFrames.Count)
                                    return;

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    var frame = selectedFrames[index];
                                    frame.Status = status;
                                    if (!string.IsNullOrWhiteSpace(outputPath))
                                        frame.OutputFile = outputPath;
                                    UpdateStatus($"打印中…（{index + 1}/{selectedFrames.Count}）{frame.FrameName}", status);
                                }));
                            });

                        Dispatcher.BeginInvoke(new Action(() => tcs.TrySetResult(result)));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() => tcs.TrySetException(ex)));
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            return await tcs.Task.ConfigureAwait(true);
        }
    }

    private static string BuildSheetListHint(string primaryHint, string? extraHint)
    {
        var main = (primaryHint ?? "").Trim();
        var extra = (extraHint ?? "").Trim();

        if (main.Length == 0)
            return extra;
        if (extra.Length == 0)
            return main;

        return main + "；" + extra;
    }

    private async Task<string> RunSinglePreviewPlotAsync(
        Document document,
        FrameInfo previewFrame,
        QqqPlotOptions options,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>();

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    try
                    {
                        var previewFile = QqqPlotService.PlotFrame(document, previewFrame, options, 1);
                        Dispatcher.BeginInvoke(new Action(() => tcs.TrySetResult(previewFile)));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() => tcs.TrySetException(ex)));
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            return await tcs.Task.ConfigureAwait(true);
        }
    }

    private void OpenCadNativeCommand(
        Document document,
        string globalCommandName,
        string commandMacro,
        string openingMessage,
        string closingMessage,
        string closingHint,
        string failureActionText)
    {
        RegisterPendingNativeCadCommand(document, globalCommandName, closingMessage, closingHint);
        UpdateStatus(openingMessage, closingHint);
        Hide();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                document.SendStringToExecute(commandMacro, true, false, false);
            }
            catch (Exception ex)
            {
                UnregisterPendingNativeCadCommand();
                EnsureShown();
                UpdateStatus($"{failureActionText}失败：{ex.Message}");
                C_toolsDiagnostics.LogNonFatal($"V_QQQ {failureActionText}失败", ex);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RegisterPendingNativeCadCommand(
        Document document,
        string globalCommandName,
        string closingMessage,
        string closingHint)
    {
        UnregisterPendingNativeCadCommand();

        _pendingNativeCadCommandDocument = document;
        _pendingNativeCadCommandName = globalCommandName;
        _pendingNativeCadCommandCloseMessage = closingMessage;
        _pendingNativeCadCommandCloseHint = closingHint;

        document.CommandEnded += OnPendingNativeCadCommandEnded;
        document.CommandCancelled += OnPendingNativeCadCommandCancelledOrFailed;
        document.CommandFailed += OnPendingNativeCadCommandCancelledOrFailed;
    }

    private void UnregisterPendingNativeCadCommand()
    {
        var document = _pendingNativeCadCommandDocument;
        _pendingNativeCadCommandDocument = null;
        _pendingNativeCadCommandName = "";
        _pendingNativeCadCommandCloseMessage = "";
        _pendingNativeCadCommandCloseHint = "";

        if (document == null)
            return;

        try
        {
            document.CommandEnded -= OnPendingNativeCadCommandEnded;
            document.CommandCancelled -= OnPendingNativeCadCommandCancelledOrFailed;
            document.CommandFailed -= OnPendingNativeCadCommandCancelledOrFailed;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 取消原生命令事件订阅失败", ex);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        UnregisterRangeDisplaySpaceRestore();
        UnregisterPendingNativeCadCommand();
        FlushPendingFrameListPersist();
        QqqPlotService.ClearFrameRangeDisplay();

        if (_printCancellation == null)
            return;

        try
        {
            _printCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _printCancellation.Dispose();
            _printCancellation = null;
        }
    }

    private bool IsPendingNativeCadCommand(string globalCommandName)
    {
        return !string.IsNullOrWhiteSpace(_pendingNativeCadCommandName) &&
               string.Equals(globalCommandName, _pendingNativeCadCommandName, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPendingNativeCadCommandEnded(object? sender, CommandEventArgs e)
    {
        if (!IsPendingNativeCadCommand(e.GlobalCommandName))
            return;

        CompletePendingNativeCadCommand();
    }

    private void OnPendingNativeCadCommandCancelledOrFailed(object? sender, CommandEventArgs e)
    {
        if (!IsPendingNativeCadCommand(e.GlobalCommandName))
            return;

        CompletePendingNativeCadCommand();
    }

    private void CompletePendingNativeCadCommand()
    {
        var closingMessage = _pendingNativeCadCommandCloseMessage;
        var closingHint = _pendingNativeCadCommandCloseHint;
        UnregisterPendingNativeCadCommand();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            EnsureShown();
            if (!string.IsNullOrWhiteSpace(closingMessage))
                UpdateStatus(closingMessage, closingHint);
        }));
    }

    private string GetCurrentDocumentName()
    {
        return AcAp.DocumentManager.MdiActiveDocument?.Name ?? "";
    }

    private static QqqPlotOptions CreatePdfPreviewOptions(QqqPlotOptions options, string outputFolder)
    {
        return new QqqPlotOptions
        {
            UseCadPlotSettings = options.UseCadPlotSettings,
            PageSetupName = options.PageSetupName,
            PrinterName = options.PrinterName,
            MediaName = options.MediaName,
            StyleSheet = options.StyleSheet,
            ScaleText = options.ScaleText,
            FileNameTemplate = "{drawing}_{layout}_{frame}_preview_{datetime:yyyyMMdd_HHmmss}",
            PlotToFile = true,
            AutoRotate = options.AutoRotate,
            CenterPlot = options.CenterPlot,
            OffsetX = options.OffsetX,
            OffsetY = options.OffsetY,
            FitToPaper = options.FitToPaper,
            AdaptPaper = options.AdaptPaper,
            ScaleLineweights = options.ScaleLineweights,
            Landscape = options.Landscape,
            UpsideDown = options.UpsideDown,
            SortRule = options.SortRule,
            OutputFolder = outputFolder
        };
    }

    private QqqPlotOptions BuildPlotOptions()
    {
        var saved = PrintSaveAutoStore.LoadOrDefault();
        var mediaName = (saved.CanonicalMediaName ?? "").Trim();
        var scaleText = (saved.ScaleText ?? "").Trim();

        return new QqqPlotOptions
        {
            UseCadPlotSettings = false,
            PageSetupName = "",
            PrinterName = string.IsNullOrWhiteSpace(saved.PrinterName) ? DefaultPdfPrinter : saved.PrinterName.Trim(),
            MediaName = mediaName.Length == 0 ? PrintSaveService.MediaAutoMatchText : mediaName,
            StyleSheet = string.IsNullOrWhiteSpace(saved.StyleSheet) ? PrintSaveService.DefaultStyleSheet : saved.StyleSheet.Trim(),
            ScaleText = scaleText.Length == 0 ? PrintSaveService.ScaleFitText : scaleText,
            PlotToFile = true,
            CenterPlot = saved.CenterPlot,
            OffsetX = saved.OffsetX,
            OffsetY = saved.OffsetY,
            FitToPaper = saved.FitToPaper,
            AdaptPaper = string.Equals(mediaName, PrintSaveService.MediaAutoMatchText, StringComparison.OrdinalIgnoreCase) ||
                         mediaName.Length == 0,
            ScaleLineweights = saved.ScaleLineweights,
            AutoRotate = saved.AutoMatchOrientation,
            Landscape = saved.Landscape,
            UpsideDown = saved.UpsideDown,
            SortRule = string.IsNullOrWhiteSpace(saved.PrintOrderRule) ? PrintSaveService.PlotOrderAddedOrder : saved.PrintOrderRule.Trim(),
            OutputFolder = (OutputFolderCombo.SelectedItem as string ?? OutputFolderCombo.Text ?? "").Trim(),
            FileNameTemplate = ResolveFileNameTemplateSelection()
        };
    }

    private static string BuildPlotResultHint(string primaryHint, IReadOnlyList<string> infoMessages)
    {
        var parts = new List<string>();
        var main = (primaryHint ?? "").Trim();
        if (main.Length > 0)
            parts.Add(main);

        foreach (var message in infoMessages.Where(static x => !string.IsNullOrWhiteSpace(x)))
            parts.Add(message.Trim());

        return string.Join("；", parts);
    }

    private static string BuildFrameRangeDisplayMessage(QqqFrameRangeDisplayResult result)
    {
        var spaceText = FormatFrameRangeSpaceText(result.LayoutName);
        if (result.DisplayedCount <= 1)
        {
            return result.DeferredCount > 0
                ? $"已在{spaceText}显示“{result.PrimaryFrameName}”的打印范围与顺序。"
                : $"已显示“{result.PrimaryFrameName}”的打印范围与顺序。";
        }

        return result.DeferredCount > 0
            ? $"已在{spaceText}显示 {result.DisplayedCount} 个打印范围与顺序。"
            : $"已显示 {result.DisplayedCount} 个打印范围与顺序。";
    }

    private static string BuildFrameRangeDisplayHint(QqqFrameRangeDisplayResult result)
    {
        var parts = new List<string>();
        var spaceText = FormatFrameRangeSpaceText(result.LayoutName);
        parts.Add(result.HighlightedCount > 0
            ? $"提示：已切到{spaceText}并选中对应图框对象"
            : $"提示：已切到{spaceText}并按识别范围定位");
        parts.Add("蓝色数字即打印顺序");

        if (result.DeferredCount > 0)
            parts.Add($"另有 {result.DeferredCount} 个范围位于其他布局或空间，可切换后再次点击“显示范围”");

        if (result.UnresolvedCount > 0)
            parts.Add(result.HighlightedCount > 0
                ? $"其中 {result.UnresolvedCount} 个未找到原对象，已按识别范围定位"
                : $"当前未找到原对象句柄，已按识别范围定位");

        parts.Add("面板已隐藏，按空格或重新执行 V_QQQ 可返回批量打印面板");
        return string.Join("；", parts);
    }

    private static string FormatFrameRangeSpaceText(string layoutName)
    {
        return string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
            ? "模型空间"
            : $"布局“{layoutName}”";
    }

    private void UpdateStatus(string message, string hint = "")
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = ResolveStatusBrush(message);

        HintTextBlock.Text = hint;
        HintTextBlock.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        HintTextBlock.Foreground = ResolveHintBrush(hint);
    }

    private System.Windows.Media.Brush ResolveStatusBrush(string text)
    {
        return GetPageBrush(
            IsErrorText(text) ? "Cad.StatusError" : "Cad.StatusSuccess",
            IsErrorText(text) ? FallbackStatusErrorBrush : FallbackStatusSuccessBrush);
    }

    private System.Windows.Media.Brush ResolveHintBrush(string text)
    {
        if (IsErrorText(text))
            return GetPageBrush("Cad.StatusError", FallbackStatusErrorBrush);

        return GetPageBrush(
            IsFixedHintText(text) ? "Cad.HintFixed" : "Cad.StatusSuccess",
            IsFixedHintText(text) ? FallbackHintFixedBrush : FallbackStatusSuccessBrush);
    }

    private System.Windows.Media.Brush GetPageBrush(string resourceKey, System.Windows.Media.Brush fallback)
    {
        return TryFindResource(resourceKey) as System.Windows.Media.Brush ?? fallback;
    }

    private static bool IsFixedHintText(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               text.StartsWith("提示：", StringComparison.Ordinal);
    }

    private static bool IsErrorText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.IndexOf("失败", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("错误", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("出错", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("异常", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("无法", StringComparison.Ordinal) >= 0;
    }

    private static SolidColorBrush CreateFrozenBrush(string colorText)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colorText)!;
        brush.Freeze();
        return brush;
    }

    private void RefreshOutputFolderSuggestions(string primaryFolder)
    {
        _outputFolders.Clear();

        var candidates = new[]
        {
            primaryFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        foreach (var path in candidates.Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            _outputFolders.Add(path);

        if (_outputFolders.Count > 0 && !_outputFolders.Contains(OutputFolderCombo.Text))
            OutputFolderCombo.SelectedItem = _outputFolders[0];
    }

    private void RefreshCadPlotResourcePaths()
    {
        PrinterConfigPathTextBox.Text = TryReadPrinterConfigPath();
        PlotStylePathTextBox.Text = TryReadPlotStylePath();
    }

    private static string TryReadPrinterConfigPath()
    {
        try
        {
            dynamic files = ((dynamic)AcAp.AcadApplication).Preferences.Files;
            string? path = files.PrinterConfigPath;
            return (path ?? "").Trim();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 读取打印机配置目录失败", ex);
            return "";
        }
    }

    private static string TryReadPlotStylePath()
    {
        try
        {
            dynamic files = ((dynamic)AcAp.AcadApplication).Preferences.Files;
            var path = "";

            try
            {
                string? printerStyleSheetPath = files.PrinterStyleSheetPath;
                path = printerStyleSheetPath ?? "";
            }
            catch
            {
                // Older AutoCAD-compatible hosts can expose the plot style folder under PrintStylePath.
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                string? printStylePath = files.PrintStylePath;
                path = printStylePath ?? "";
            }

            return path.Trim();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 读取打印样式表目录失败", ex);
            return "";
        }
    }

    private void SetPrintingState(bool isPrinting)
    {
        _isPrinting = isPrinting;
        StartPrintButton.IsEnabled = !isPrinting;
        PreviewButton.IsEnabled = !isPrinting;
        ShowRangesButton.IsEnabled = !isPrinting;
        AddBlockSheetsButton.IsEnabled = !isPrinting;
        AddWindowSheetsButton.IsEnabled = !isPrinting;
    }

    private List<FrameInfo> GetSelectedFramesInPlotOrder(QqqPlotOptions options)
    {
        var selectedFrames = _frames
            .Where(static x => x.IsSelected)
            .ToList();

        return QqqPlotService.SortFrames(selectedFrames, options.SortRule).ToList();
    }

    private void EnsureShown()
    {
        if (Visibility != Visibility.Visible)
            Show();

        ShowActivated = false;
    }

    private void RegisterRangeDisplaySpaceRestore()
    {
        if (_isAwaitingSpaceToRestoreFromRangeDisplay)
            return;

        ComponentDispatcher.ThreadPreprocessMessage += OnRangeDisplaySpaceRestorePreprocessMessage;
        _isAwaitingSpaceToRestoreFromRangeDisplay = true;
    }

    private void UnregisterRangeDisplaySpaceRestore()
    {
        if (!_isAwaitingSpaceToRestoreFromRangeDisplay)
            return;

        ComponentDispatcher.ThreadPreprocessMessage -= OnRangeDisplaySpaceRestorePreprocessMessage;
        _isAwaitingSpaceToRestoreFromRangeDisplay = false;
    }

    private void OnRangeDisplaySpaceRestorePreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled ||
            !_isAwaitingSpaceToRestoreFromRangeDisplay ||
            Visibility == Visibility.Visible ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        const int VkSpace = 0x20;

        if (msg.message != WmKeyDown && msg.message != WmSysKeyDown)
            return;

        if (((int)msg.wParam & 0xFFFF) != VkSpace)
            return;

        if (IsCadCommandActive())
            return;

        handled = true;
        UnregisterRangeDisplaySpaceRestore();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            QqqCommands.ShowBatchPanel();
            UpdateStatus(
                "已返回批量打印面板。",
                "提示：当前显示范围仍保留；可继续调整、预览或开始打印。");
        }));
    }

    private static bool IsCadCommandActive()
    {
        try
        {
            return Convert.ToInt32(AcAp.GetSystemVariable("CMDACTIVE")) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void TryAttachOwner()
    {
        try
        {
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow != null && !ReferenceEquals(mainWindow, this))
                Owner = mainWindow;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 设置窗口 Owner 失败", ex);
        }
    }

    private static string ResolveDefaultOutputFolder()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T matched)
                return matched;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private static T? FindNamedVisualChild<T>(DependencyObject parent, string elementName) where T : FrameworkElement
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T matched &&
                string.Equals(matched.Name, elementName, StringComparison.Ordinal))
            {
                return matched;
            }

            var descendant = FindNamedVisualChild<T>(child, elementName);
            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private void BeginOutputFolderEdit()
    {
        _outputFolderTextBeforeEdit = OutputFolderCombo.Text;
        OutputFolderCombo.IsDropDownOpen = false;
        OutputFolderCombo.IsEditable = true;
        OutputFolderCombo.ApplyTemplate();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (OutputFolderCombo.Template.FindName("PART_EditableTextBox", OutputFolderCombo) is System.Windows.Controls.TextBox editor)
            {
                editor.Focus();
                editor.SelectAll();
                return;
            }

            OutputFolderCombo.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndOutputFolderEdit(bool commitChanges)
    {
        var nextText = commitChanges
            ? OutputFolderCombo.Text.Trim()
            : _outputFolderTextBeforeEdit;

        if (string.IsNullOrWhiteSpace(nextText))
            nextText = _outputFolderTextBeforeEdit;

        var matchedExisting = _outputFolders.FirstOrDefault(path =>
            string.Equals(path, nextText, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(nextText) && matchedExisting == null)
            _outputFolders.Insert(0, nextText);

        OutputFolderCombo.IsEditable = false;
        OutputFolderCombo.Text = matchedExisting ?? nextText;

        if (!string.IsNullOrWhiteSpace(matchedExisting ?? nextText))
            OutputFolderCombo.SelectedItem = matchedExisting ?? nextText;
    }

    private void InitializeStaticOptions()
    {
        _fileNameTemplates.Add(QqqPlotService.FileNameTemplateDrawing);
        _fileNameTemplates.Add(QqqPlotService.FileNameTemplateLayout);
        FileNameTemplateCombo.SelectedItem = DefaultFileNameTemplate;
    }

    private string ResolveFileNameTemplateSelection()
    {
        var selected = (FileNameTemplateCombo.SelectedItem as string ?? FileNameTemplateCombo.Text ?? "").Trim();
        return string.Equals(selected, QqqPlotService.FileNameTemplateLayout, StringComparison.OrdinalIgnoreCase)
            ? QqqPlotService.FileNameTemplateLayout
            : QqqPlotService.FileNameTemplateDrawing;
    }

    private void OpenOutputFolder(string? outputFolder, string? outputFile = null)
    {
        var filePath = (outputFile ?? "").Trim();
        var path = (outputFolder ?? "").Trim();

        try
        {
            if (filePath.Length > 0 && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
                return;
            }

            if (path.Length == 0)
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 打开输出目录失败", ex);
        }
    }

    private sealed class SavedSheetSetTabInfo : INotifyPropertyChanged
    {
        private string _displayName = "";
        private string _editName = "";
        private bool _isRenaming;

        public string DisplayName
        {
            get => _displayName;
            set
            {
                var normalized = (value ?? "").Trim();
                if (string.Equals(_displayName, normalized, StringComparison.Ordinal))
                    return;

                _displayName = normalized;
                OnPropertyChanged();
            }
        }

        public string EditName
        {
            get => _editName;
            set
            {
                var normalized = value ?? "";
                if (string.Equals(_editName, normalized, StringComparison.Ordinal))
                    return;

                _editName = normalized;
                OnPropertyChanged();
            }
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (_isRenaming == value)
                    return;

                _isRenaming = value;
                OnPropertyChanged();
            }
        }

        public bool IsWorkingSet { get; set; }
        public QqqSavedSheetSetDefinition? Definition { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class FrameTemplateInfo : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _sizeText = "";

        public string Name { get; set; } = "";

        public string SizeText
        {
            get => _sizeText;
            set
            {
                var normalized = NormalizeTemplateSizeText(value);
                if (string.Equals(_sizeText, normalized, StringComparison.Ordinal))
                    return;

                _sizeText = normalized;
                OnPropertyChanged();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private enum FrameListPersistTargetKind
    {
        WorkingSet,
        SavedSheetSet
    }

    private sealed class FrameListPersistRequest
    {
        public FrameListPersistTargetKind TargetKind { get; set; }
        public string DocumentPath { get; set; } = "";
        public string SavedSheetSetName { get; set; } = "";
        public List<FrameInfo> Frames { get; set; } = new();
        public List<QqqSavedSheetSetDefinition> SavedSheetSetDefinitions { get; set; } = new();
    }

    private sealed class ResettableObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
            RaiseReset();
        }

        public void AddRange(IEnumerable<T> items)
        {
            var changed = false;
            foreach (var item in items)
            {
                Items.Add(item);
                changed = true;
            }

            if (changed)
                RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
}
