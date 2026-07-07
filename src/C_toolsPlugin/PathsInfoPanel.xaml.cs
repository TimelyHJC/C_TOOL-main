using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

public sealed class PathsInfoSectionVm
{
    public string Title { get; init; } = "";
    public ObservableCollection<PathsInfoRowVm> Rows { get; } = new();
}

public sealed class PathsInfoRowVm
{
    private readonly Action<string>? _statusCallback;

    public string Title { get; init; } = "";
    public string PathText { get; init; } = "";
    public string? Hint { get; init; }
    
    public PathStatus Status { get; init; }
    public string StatusIcon => GetStatusIcon(Status);
    public bool CanOpen => Status == PathStatus.Exists;
    
    public ICommand CopyCommand { get; }
    public ICommand OpenCommand { get; }
    
    public PathsInfoRowVm(string title, string pathText, string? hint = null, Action<string>? statusCallback = null)
    {
        _statusCallback = statusCallback;
        Title = title;
        PathText = pathText;
        Hint = hint;
        Status = CheckPathStatus(pathText);
        
        CopyCommand = new RelayCommand(() => CopyPath());
        OpenCommand = new RelayCommand(() => OpenDirectory(), () => CanOpen);
    }
    
    private PathStatus CheckPathStatus(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PathStatus.Unknown;
        
        try
        {
            if (Directory.Exists(path))
                return PathStatus.Exists;
            
            if (File.Exists(path))
                return PathStatus.Exists;
            
            return PathStatus.Missing;
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("检查路径状态失败（权限）", ex);
            return PathStatus.Error;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("检查路径状态失败（IO）", ex);
            return PathStatus.Error;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("检查路径状态失败（参数错误）", ex);
            return PathStatus.Error;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("检查路径状态失败（不支持）", ex);
            return PathStatus.Error;
        }
    }
    
    private string GetStatusIcon(PathStatus status)
    {
        return status switch
        {
            PathStatus.Exists => "✓",
            PathStatus.Missing => "!",
            PathStatus.Error => "!",
            _ => "?"
        };
    }
    
    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(PathText);
            _statusCallback?.Invoke("已复制路径：" + Title);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("复制路径到剪贴板失败（无效操作）", ex);
            _statusCallback?.Invoke("复制失败：" + ex.Message);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            C_toolsDiagnostics.LogNonFatal("复制路径到剪贴板失败（剪贴板）", ex);
            _statusCallback?.Invoke("复制失败：" + ex.Message);
        }
    }
    
    private void OpenDirectory()
    {
        try
        {
            if (File.Exists(PathText))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + PathText + "\"")
                {
                    UseShellExecute = true
                });
                _statusCallback?.Invoke("已定位文件：" + Title);
                return;
            }

            if (Directory.Exists(PathText))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PathText + "\"")
                {
                    UseShellExecute = true
                });
                _statusCallback?.Invoke("已打开目录：" + Title);
                return;
            }

            _statusCallback?.Invoke("路径不存在，无法打开：" + Title);
        }
        catch (ObjectDisposedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开路径所在目录失败（对象已释放）", ex);
            _statusCallback?.Invoke("打开失败：" + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开路径所在目录失败（无效操作）", ex);
            _statusCallback?.Invoke("打开失败：" + ex.Message);
        }
        catch (Win32Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开路径所在目录失败（Win32）", ex);
            _statusCallback?.Invoke("打开失败：" + ex.Message);
        }
    }
}

public enum PathStatus
{
    Unknown,
    Exists,
    Missing,
    Error
}

public class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_successBrush = CreateFrozenBrush(0x3F, 0xB9, 0x50);
    private static readonly SolidColorBrush s_warningBrush = CreateFrozenBrush(0xF5, 0x9E, 0x0B);
    private static readonly SolidColorBrush s_errorBrush = CreateFrozenBrush(0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush s_mutedBrush = CreateFrozenBrush(0x8B, 0x94, 0x9E);

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return value is PathStatus status
            ? status switch
            {
                PathStatus.Exists => s_successBrush,
                PathStatus.Missing => s_warningBrush,
                PathStatus.Error => s_errorBrush,
                _ => s_mutedBrush
            }
            : s_mutedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
    
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }
    
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }
    
    public void Execute(object? parameter)
    {
        _execute();
    }
}

public partial class PathsInfoPanel : UserControl
{
    public ObservableCollection<PathsInfoSectionVm> Sections { get; } = new();
    public Action<string>? SetStatusCallback { get; set; }

    public PathsInfoPanel()
    {
        InitializeComponent();
        DataContext = this;
    }

    internal void RefreshPaths()
    {
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var merged = new List<PathsInfoCatalog.Section>();
                    merged.AddRange(PathsInfoCatalog.BuildPluginSections());
                    merged.AddRange(PathsInfoCatalog.BuildCadSections());
                    Dispatcher.Invoke(() =>
                    {
                        ApplySections(merged);
                    });
                },
                null);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("PathsInfoPanel.RefreshPaths（ExecuteInApplicationContext）", ex);
            ApplySections(PathsInfoCatalog.BuildPluginSections());
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("PathsInfoPanel.RefreshPaths（ExecuteInApplicationContext）", ex);
            ApplySections(PathsInfoCatalog.BuildPluginSections());
        }
    }

    internal bool TrySaveSettings(out string message)
    {
        message = "";
        return true;
    }

    private void ApplySections(IReadOnlyList<PathsInfoCatalog.Section> list)
    {
        Sections.Clear();
        foreach (var sec in list)
        {
            var vm = new PathsInfoSectionVm { Title = sec.Title };
            foreach (var r in sec.Rows)
                vm.Rows.Add(new PathsInfoRowVm(r.Title, r.PathText, r.Hint, SetStatusCallback));
            Sections.Add(vm);
        }
    }
}
