using System;
using System.Windows;

namespace C_toolsShared;

public static class ModelessWindowDisplayHelper
{
    public static void RestoreAndShow(Window window, bool activateIfVisible = false)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        if (window.Visibility != Visibility.Visible)
            window.Show();

        if (!activateIfVisible)
            return;

        try
        {
            window.Activate();
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"激活模型窗口 {window.GetType().Name} 失败（无效操作）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"激活模型窗口 {window.GetType().Name} 失败", ex);
        }
    }
}
