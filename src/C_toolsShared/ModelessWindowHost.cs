using System;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

/// <summary>
/// 模式窗口宿主类，用于管理 WPF 窗口的显示/隐藏切换。
/// 提供线程安全的窗口实例管理，确保同一时间只有一个窗口实例。
/// </summary>
/// <typeparam name="TWindow">窗口类型，必须继承自 <see cref="Window"/></typeparam>
public sealed class ModelessWindowHost<TWindow> where TWindow : Window
{
    private readonly object _sync = new();
    private TWindow? _window;

    /// <summary>
    /// 切换窗口的显示/隐藏状态。
    /// 如果窗口不存在则创建新窗口，否则切换其可见性。
    /// </summary>
    /// <param name="factory">窗口工厂函数，用于创建新窗口</param>
    /// <param name="showCreated">新窗口创建后执行的显示操作</param>
    /// <param name="showExisting">现有窗口显示时执行的操作</param>
    /// <param name="hideExisting">现有窗口隐藏时执行的操作</param>
    public void Toggle(
        Func<TWindow> factory,
        Action<TWindow> showCreated,
        Action<TWindow> showExisting,
        Action<TWindow> hideExisting)
    {
        lock (_sync)
        {
            if (_window == null)
            {
                _window = InitializeWindow(factory());
                InvokeBeforeShow(_window);
                showCreated(_window);
                return;
            }

            if (_window.Visibility == Visibility.Visible)
            {
                InvokeBeforeHide(_window);
                hideExisting(_window);
            }
            else
            {
                InvokeBeforeShow(_window);
                showExisting(_window);
            }
        }
    }

    /// <summary>
    /// 以 AutoCAD 模型窗口默认行为切换窗口：显示时统一走 <see cref="Application.ShowModelessWindow(IntPtr, Window, bool)"/>，
    /// 隐藏走 <see cref="Window.Hide"/>。
    /// </summary>
    public void Toggle(
        Func<TWindow> factory,
        Action<TWindow>? beforeShow = null,
        Action<TWindow>? beforeHide = null)
    {
        Toggle(
            factory,
            window =>
            {
                beforeShow?.Invoke(window);
                ShowCreatedWindow(window);
            },
            window =>
            {
                beforeShow?.Invoke(window);
                ShowExistingWindow(window, activateIfVisible: false);
            },
            window =>
            {
                beforeHide?.Invoke(window);
                window.Hide();
            });
    }

    /// <summary>
    /// 获取现有窗口实例或创建新窗口。
    /// </summary>
    /// <param name="factory">窗口工厂函数，用于创建新窗口</param>
    /// <param name="created">输出参数，指示是否创建了新窗口</param>
    /// <returns>窗口实例</returns>
    public TWindow GetOrCreate(Func<TWindow> factory, out bool created)
    {
        lock (_sync)
        {
            if (_window != null)
            {
                created = false;
                return _window;
            }

            _window = InitializeWindow(factory());
            created = true;
            return _window;
        }
    }

    /// <summary>
    /// 以默认模型窗口行为显示窗口；若已可见则不激活。
    /// </summary>
    public TWindow Show(Func<TWindow> factory, Action<TWindow>? beforeShow = null)
    {
        return ShowInternal(factory, beforeShow, activateIfVisible: false);
    }

    /// <summary>
    /// 以默认模型窗口行为显示窗口；若已可见则尝试激活。
    /// </summary>
    public TWindow ShowOrActivate(Func<TWindow> factory, Action<TWindow>? beforeShow = null)
    {
        return ShowInternal(factory, beforeShow, activateIfVisible: true);
    }

    /// <summary>
    /// 关闭现有窗口（如果存在）。
    /// </summary>
    /// <param name="beforeClose">关闭前执行的操作（可选）</param>
    /// <param name="onInvalidOperation">处理 <see cref="InvalidOperationException"/> 的回调（可选）</param>
    /// <param name="onError">处理其他异常的回调（可选）</param>
    public void CloseIfAny(
        Action<TWindow>? beforeClose = null,
        Action<InvalidOperationException>? onInvalidOperation = null,
        Action<Exception>? onError = null)
    {
        TWindow? toClose = null;
        lock (_sync)
        {
            if (_window == null)
                return;

            toClose = _window;
            _window = null;
        }

        try
        {
            beforeClose?.Invoke(toClose);
            TrySavePlacement(toClose);
            toClose.IsVisibleChanged -= OnIsVisibleChanged;
            toClose.Closed -= OnClosed;
            toClose.Close();
        }
        catch (InvalidOperationException ex)
        {
            onInvalidOperation?.Invoke(ex);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    private TWindow ShowInternal(Func<TWindow> factory, Action<TWindow>? beforeShow, bool activateIfVisible)
    {
        var window = GetOrCreate(factory, out var created);
        beforeShow?.Invoke(window);
        InvokeBeforeShow(window);

        if (created)
        {
            ShowCreatedWindow(window);
            return window;
        }

        ShowExistingWindow(window, activateIfVisible);
        return window;
    }

    private TWindow InitializeWindow(TWindow window)
    {
        window.Closed += OnClosed;
        window.IsVisibleChanged += OnIsVisibleChanged;

        if (window is IModelessWindowPlacement placement)
            ModelessWindowPlacementStore.TryRestore(window, placement.PlacementKey);

        return window;
    }

    private static void ShowCreatedWindow(TWindow window)
    {
        ShowViaAutoCadHost(window);
    }

    private static void ShowExistingWindow(TWindow window, bool activateIfVisible)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        if (window.Visibility != Visibility.Visible)
        {
            ShowViaAutoCadHost(window);
            return;
        }

        if (!activateIfVisible)
            return;

        try
        {
            window.Activate();
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"激活模型窗口 {typeof(TWindow).Name} 失败（无效操作）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"激活模型窗口 {typeof(TWindow).Name} 失败", ex);
        }
    }

    private static void ShowViaAutoCadHost(TWindow window)
    {
        NormalizeBoundsForCurrentDisplay(window);
        window.ShowActivated = false;

        var owner = AcAp.MainWindow?.Handle ?? IntPtr.Zero;
        if (owner != IntPtr.Zero)
        {
            // 位置/尺寸持久化改由 C_TOOL 自己维护，避免与 AutoCAD 内置持久化互相覆盖。
            AcAp.ShowModelessWindow(owner, window, false);
            return;
        }

        AcAp.ShowModelessWindow(window);
    }

    private static void NormalizeBoundsForCurrentDisplay(TWindow window)
    {
        if (!IsFinite(window.Width) || window.Width <= 0)
            window.Width = Math.Max(window.MinWidth, 960d);
        if (!IsFinite(window.Height) || window.Height <= 0)
            window.Height = Math.Max(window.MinHeight, 540d);

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        if (!IsFinite(screenWidth) || !IsFinite(screenHeight) || screenWidth <= 0 || screenHeight <= 0)
            return;

        var safeWidth = Math.Max(200d, screenWidth - 32d);
        var safeHeight = Math.Max(120d, screenHeight - 32d);

        window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, safeWidth));
        window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, safeHeight));

        if (!IsFinite(window.Left))
            window.Left = screenLeft + 16d;
        if (!IsFinite(window.Top))
            window.Top = screenTop + 16d;

        var maxLeft = screenLeft + Math.Max(0d, screenWidth - window.Width);
        var maxTop = screenTop + Math.Max(0d, screenHeight - window.Height);
        window.Left = Math.Min(Math.Max(window.Left, screenLeft), maxLeft);
        window.Top = Math.Min(Math.Max(window.Top, screenTop), maxTop);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void InvokeBeforeShow(TWindow window)
    {
        if (window is IModelessWindowHostAware aware)
            aware.OnHostShowing();
    }

    private static void InvokeBeforeHide(TWindow window)
    {
        if (window is IModelessWindowHostAware aware)
            aware.OnHostHiding();
    }

    private static void TrySavePlacement(TWindow window)
    {
        if (window is IModelessWindowPlacement placement)
            ModelessWindowPlacementStore.TrySave(window, placement.PlacementKey);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not TWindow window)
            return;

        lock (_sync)
        {
            TrySavePlacement(window);

            if (ReferenceEquals(window, _window))
                _window = null;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TWindow window || window.IsVisible)
            return;

        TrySavePlacement(window);
    }
}
