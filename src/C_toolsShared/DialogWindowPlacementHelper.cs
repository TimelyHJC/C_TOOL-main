using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

/// <summary>
/// 为弹窗提供统一的屏幕居中定位辅助。
/// </summary>
public static class DialogWindowPlacementHelper
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// 优先恢复上次保存的位置；若没有保存位置，则居中到拥有者所在显示器。
    /// </summary>
    public static void TryRestoreOrCenterOnOwnerMonitor(Window window, string? placementKey = null, IntPtr fallbackOwnerHandle = default)
    {
        if (DialogWindowPlacementStore.TryRestore(window, ResolvePlacementKey(window, placementKey)))
            return;

        TryCenterOnOwnerMonitor(window, fallbackOwnerHandle);
    }

    /// <summary>
    /// 保存当前对话框位置，供下次恢复。
    /// </summary>
    public static void TrySavePlacement(Window window, string? placementKey = null)
    {
        DialogWindowPlacementStore.TrySave(window, ResolvePlacementKey(window, placementKey));
    }

    /// <summary>
    /// 将对话框定位到拥有者所在显示器的工作区中心；若无法解析拥有者，则退回 AutoCAD 主窗口所在显示器。
    /// </summary>
    public static void TryCenterOnOwnerMonitor(Window window, IntPtr fallbackOwnerHandle = default)
    {
        try
        {
            var ownerHandle = ResolveOwnerHandle(window, fallbackOwnerHandle);
            if (ownerHandle == IntPtr.Zero)
                ownerHandle = AcAp.MainWindow?.Handle ?? IntPtr.Zero;

            if (!TryGetWorkingAreaDip(window, ownerHandle, out var workArea))
                return;

            var windowWidth = ResolveWindowWidth(window);
            var windowHeight = ResolveWindowHeight(window);
            if (windowWidth <= 0d || windowHeight <= 0d)
                return;

            var left = workArea.Left + Math.Max(0d, (workArea.Width - windowWidth) * 0.5d);
            var top = workArea.Top + Math.Max(0d, (workArea.Height - windowHeight) * 0.5d);

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"定位弹窗 {window.GetType().Name} 到显示器中心", ex);
        }
    }

    private static string ResolvePlacementKey(Window window, string? placementKey)
    {
        if (!string.IsNullOrWhiteSpace(placementKey))
            return placementKey!.Trim();

        var typeName = window.GetType().FullName ?? window.GetType().Name;
        return "Dialog_" + typeName.Replace('+', '.');
    }

    private static IntPtr ResolveOwnerHandle(Window window, IntPtr fallbackOwnerHandle)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Owner != IntPtr.Zero)
            return helper.Owner;

        if (window.Owner != null)
        {
            var ownerHandle = new WindowInteropHelper(window.Owner).Handle;
            if (ownerHandle != IntPtr.Zero)
                return ownerHandle;
        }

        return fallbackOwnerHandle;
    }

    private static bool TryGetWorkingAreaDip(Window window, IntPtr ownerHandle, out Rect workArea)
    {
        workArea = Rect.Empty;

        if (ownerHandle != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && TryGetMonitorWorkArea(window, monitor, out workArea))
                return true;
        }

        var fallbackArea = SystemParameters.WorkArea;
        if (fallbackArea.Width <= 0d || fallbackArea.Height <= 0d)
            return false;

        workArea = fallbackArea;
        return true;
    }

    private static bool TryGetMonitorWorkArea(Window window, IntPtr monitor, out Rect workArea)
    {
        workArea = Rect.Empty;

        var monitorInfo = new MonitorInfo();
        monitorInfo.CbSize = Marshal.SizeOf<MonitorInfo>();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;

        workArea = new Rect(
            monitorInfo.Work.Left / scaleX,
            monitorInfo.Work.Top / scaleY,
            (monitorInfo.Work.Right - monitorInfo.Work.Left) / scaleX,
            (monitorInfo.Work.Bottom - monitorInfo.Work.Top) / scaleY);

        return workArea.Width > 0d && workArea.Height > 0d;
    }

    private static double ResolveWindowWidth(Window window)
    {
        if (IsFinitePositive(window.ActualWidth))
            return window.ActualWidth;
        if (IsFinitePositive(window.Width))
            return window.Width;
        if (IsFinitePositive(window.MinWidth))
            return window.MinWidth;

        return 0d;
    }

    private static double ResolveWindowHeight(Window window)
    {
        if (IsFinitePositive(window.ActualHeight))
            return window.ActualHeight;
        if (IsFinitePositive(window.Height))
            return window.Height;
        if (IsFinitePositive(window.MinHeight))
            return window.MinHeight;

        return 0d;
    }

    private static bool IsFinitePositive(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0d;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }
}
