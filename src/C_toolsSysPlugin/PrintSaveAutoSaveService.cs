using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static class PrintSaveAutoSaveService
{
    private const string QsaveCommand = "_.QSAVE";
    private static DispatcherTimer? s_timer;
    private static bool s_isTicking;

    internal static void Initialize()
    {
        Restart();
    }

    internal static void Restart(PrintSaveAutoOptionsDto? options = null)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Restart(options)));
            return;
        }

        StopTimer();

        var resolvedOptions = options ?? PrintSaveAutoStore.LoadOrDefault();
        if (resolvedOptions.IntervalMinutes <= 0)
            return;

        s_timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMinutes(resolvedOptions.IntervalMinutes)
        };
        s_timer.Tick += OnTimerTick;
        s_timer.Start();
    }

    internal static void Terminate()
    {
        StopTimer();
    }

    private static void StopTimer()
    {
        if (s_timer == null)
            return;

        s_timer.Stop();
        s_timer.Tick -= OnTimerTick;
        s_timer = null;
    }

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        if (s_isTicking)
            return;
        if (IsMouseButtonPressed())
            return;

        s_isTicking = true;
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    try
                    {
                        TryRunQsave();
                    }
                    finally
                    {
                        s_isTicking = false;
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            s_isTicking = false;
            C_toolsDiagnostics.LogNonFatal("打印与保存：自动 QSAVE 上下文", ex);
        }
    }

    private static void TryRunQsave()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null || IsCadCommandActive() || IsMouseButtonPressed())
            return;

        try
        {
            doc.SendStringToExecute(QsaveCommand + "\n", true, false, false);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打印与保存：自动 QSAVE", ex);
        }
    }

    private static bool IsCadCommandActive()
    {
        try
        {
            return Convert.ToInt32(AcAp.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMouseButtonPressed()
    {
        try
        {
            return Mouse.LeftButton == MouseButtonState.Pressed ||
                   Mouse.MiddleButton == MouseButtonState.Pressed ||
                   Mouse.RightButton == MouseButtonState.Pressed ||
                   Mouse.XButton1 == MouseButtonState.Pressed ||
                   Mouse.XButton2 == MouseButtonState.Pressed;
        }
        catch
        {
            return false;
        }
    }
}
