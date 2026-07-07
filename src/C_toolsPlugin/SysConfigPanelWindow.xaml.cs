using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsShared;

namespace C_toolsPlugin;

public partial class SysConfigPanelWindow : Window, IModelessWindowPlacement
{
    public string PlacementKey => "C_TOOL_SysConfigPanel";

    private string _currentTabKey = SysConfigTabClassifier.TabKeysInOrder[0];
    private bool _argProfileComboSuppressSelectionChanged;
    private bool _saveClosingInProgress;

    public SysConfigPanelWindow()
    {
        InitializeComponent();
        WindowDpiHelper.InstallWindowSizeFromCurrentPixels(this);
        DimStylePanel.SetStatusCallback = SetStatus;
        PrintSavePanel.SetStatusCallback = SetStatus;
        PathsInfoPanel.SetStatusCallback = SetStatus;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowTitleBarHelper.TryApplyDarkTitleBar(this);

        C_toolsPaths.EnsureFolders();
        SetStatus("已就绪。可编辑标注样式、打印与保存设置，并可从 .arg 安全应用系统配置。");
        UpdateTabUi();
        if (_currentTabKey == "DimStyle")
            DimStylePanel.RefreshFromDocument();
        else if (_currentTabKey == "PrintSave")
            PrintSavePanel.RefreshFromDocument();
        RefreshArgProfileComboItems();
    }

    private void SetStatus(string? text)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(text) ? "未提供状态信息。" : text;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 系统变量表已移除；保留控件占位，不执行筛选。
    }

    private void CategoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, CategoryTabs))
            return;
        if (CategoryTabs.SelectedItem is not TabItem tab || tab.Tag is not string key)
            return;
        if (string.Equals(_currentTabKey, key, StringComparison.Ordinal))
            return;
        _currentTabKey = key;
        UpdateTabUi();
        if (_currentTabKey == "DimStyle")
            DimStylePanel.RefreshFromDocument();
        else if (_currentTabKey == "PrintSave")
            PrintSavePanel.RefreshFromDocument();
    }

    private void UpdateTabUi()
    {
        var isDim = string.Equals(_currentTabKey, "DimStyle", StringComparison.Ordinal);
        var isPrintSave = string.Equals(_currentTabKey, "PrintSave", StringComparison.Ordinal);
        var isPaths = string.Equals(_currentTabKey, "Paths", StringComparison.Ordinal);
        PathsInfoHost.Visibility = isPaths ? Visibility.Visible : Visibility.Collapsed;
        DimStylePanel.Visibility = isDim ? Visibility.Visible : Visibility.Collapsed;
        PrintSavePanel.Visibility = isPrintSave ? Visibility.Visible : Visibility.Collapsed;
        if (isPaths)
            PathsInfoPanel.RefreshPaths();
    }

    private void ArgProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_argProfileComboSuppressSelectionChanged)
            return;
        if (ArgProfileCombo.SelectedItem is not ArgProfilePickItem pick)
            return;
        if (!TryGetArgProfileSelection(pick, out var argPath, out var validationMessage))
        {
            SetStatus(validationMessage ?? UIMessages.Errors.ArgFileInvalid);
            return;
        }

        ApplyArgProfileToCad(argPath);
    }

    /// <summary>写入当前 CAD 会话（须打开图纸并锁定文档）；不写入 配置.ini / .arg。</summary>
    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_currentTabKey, "DimStyle", StringComparison.Ordinal))
        {
            DimStylePanel.ApplyToCurrentGroup((ok, _) =>
            {
                if (ok)
                    ContinueSaveAfterCurrentTab();
            });
            return;
        }

        if (string.Equals(_currentTabKey, "Paths", StringComparison.Ordinal))
        {
            ContinueSaveAfterCurrentTab();
            return;
        }

        if (string.Equals(_currentTabKey, "PrintSave", StringComparison.Ordinal))
        {
            PrintSavePanel.ApplyFromMainSave((ok, _) =>
            {
                if (ok)
                    ContinueSaveAfterCurrentTab();
            });
            return;
        }
    }

    private void ContinueSaveAfterCurrentTab()
    {
        if (_saveClosingInProgress)
            return;

        if (!TrySavePathsSettings())
            return;

        if (!TryGetSelectedArgProfile(out var argPath, out var validationMessage))
        {
            if (!string.IsNullOrEmpty(validationMessage))
            {
                SetStatus(validationMessage);
                return;
            }

            _saveClosingInProgress = true;
            Close();
            return;
        }

        ApplyArgProfileToCad(argPath, (ok, _) =>
        {
            if (!ok || _saveClosingInProgress)
                return;
            _saveClosingInProgress = true;
            Close();
        });
    }

    internal void EnsureShown()
    {
        Show();
        ShowActivated = false;
    }

    /// <summary>切换到「标注样式」选项卡并刷新（供 F_YYY 等辅助命令使用）。</summary>
    internal void NavigateToDimStyleTab()
    {
        _currentTabKey = "DimStyle";
        TabCatDimStyle.IsSelected = true;
        UpdateTabUi();
        DimStylePanel.RefreshFromDocument();
    }

    /// <summary>切换到「打印与保存」选项卡并刷新（供 V_QQQ 设置入口使用）。</summary>
    internal void NavigateToPrintSaveTab()
    {
        _currentTabKey = "PrintSave";
        TabCatPrintSave.IsSelected = true;
        UpdateTabUi();
        PrintSavePanel.RefreshFromDocument();
    }

    private bool TrySavePathsSettings()
    {
        if (!PathsInfoPanel.TrySaveSettings(out var message))
        {
            SetStatus(message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message) && string.Equals(_currentTabKey, "Paths", StringComparison.Ordinal))
            SetStatus(message);

        return true;
    }

    private void RefreshArgProfileComboItems()
    {
        ArgProfileCombo.Items.Clear();
        foreach (var path in EnumerateUserArgFiles().OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            ArgProfileCombo.Items.Add(new ArgProfilePickItem(path));

        var last = ArgProfileLastStore.TryGetLastPath();
        ArgProfilePickItem? pick = null;
        if (!string.IsNullOrEmpty(last))
        {
            foreach (ArgProfilePickItem item in ArgProfileCombo.Items)
            {
                if (string.Equals(item.FullPath, last, StringComparison.OrdinalIgnoreCase))
                {
                    pick = item;
                    break;
                }
            }
        }

        _argProfileComboSuppressSelectionChanged = true;
        try
        {
            ArgProfileCombo.SelectedItem = pick;
            if (pick == null && ArgProfileCombo.Items.Count > 0)
                ArgProfileCombo.SelectedIndex = 0;
        }
        finally
        {
            _argProfileComboSuppressSelectionChanged = false;
        }
    }

    private static IEnumerable<string> EnumerateUserArgFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { C_toolsPaths.UserEditableFolder, C_toolsPaths.UserConfigFolder })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.arg", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files)
            {
                if (seen.Add(f))
                    yield return f;
            }
        }
    }

    private bool TryGetSelectedArgProfile(out string argPath, out string? validationMessage)
    {
        if (ArgProfileCombo.SelectedItem is not ArgProfilePickItem pick)
        {
            argPath = "";
            validationMessage = null;
            return false;
        }

        return TryGetArgProfileSelection(pick, out argPath, out validationMessage);
    }

    private static bool TryGetArgProfileSelection(
        ArgProfilePickItem pick,
        out string argPath,
        out string? validationMessage)
    {
        argPath = pick.FullPath;
        validationMessage = null;

        if (argPath is not { Length: > 0 })
        {
            validationMessage = UIMessages.Errors.ArgFileInvalid;
            return false;
        }

        if (!File.Exists(argPath))
        {
            validationMessage = "选定的 .arg 配置文件不存在。";
            return false;
        }

        return true;
    }

    private void ApplyArgProfileToCad(string argPath, Action<bool, string>? completed = null)
    {
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var result = CadArgSessionApplier.ApplyToCurrentSession(argPath);
                    var pathForUi = argPath;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!result.Ok)
                        {
                            SetStatus(result.Message);
                            completed?.Invoke(false, result.Message);
                            return;
                        }

                        ArgProfileLastStore.Save(pathForUi);
                        var successMessage = BuildArgApplySuccessMessage(pathForUi, result);
                        SetStatus(successMessage);
                        completed?.Invoke(true, successMessage);
                    });
                },
                null);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（应用 .arg）", ex);
            var failMessage = "无法切换到 CAD 应用程序上下文：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
    }

    private static string BuildArgApplySuccessMessage(string argPath, CadArgSessionApplier.ApplyResult result)
    {
        var fileName = Path.GetFileName(argPath);
        if (result.FailedCount <= 0)
            return $"已将「{fileName}」中的 {result.AppliedCount} 项系统变量应用到当前 CAD；已保留菜单栏、标签栏和工作区。";

        var preview = string.Join("；", result.FailedVars.Take(3));
        if (result.FailedVars.Count > 3)
            preview += "；…";
        return $"已将「{fileName}」中的 {result.AppliedCount} 项系统变量应用到当前 CAD；另有 {result.FailedCount} 项未写入：{preview}";
    }

    private sealed class ArgProfilePickItem
    {
        internal ArgProfilePickItem(string fullPath)
        {
            FullPath = fullPath;
        }

        internal string FullPath { get; }

        public override string ToString() => Path.GetFileName(FullPath);
    }
}
