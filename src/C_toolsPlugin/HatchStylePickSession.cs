using System.Windows.Threading;

namespace C_toolsPlugin;

/// <summary>从浮层点「拾取填充样式」到 <see cref="HatchStylePickCommand"/> 完成之间的上下文（拾取前会 <see cref="FloatingPanelWindow.HideForHatchStylePick"/>）。</summary>
internal static class HatchStylePickSession
{
    private static readonly object Sync = new();

    private static CommandCatalogRow? _row;
    private static Dispatcher? _dispatcher;
    private static FloatingPanelWindow? _owner;

    internal static void Begin(CommandCatalogRow row, Dispatcher uiDispatcher, FloatingPanelWindow owner)
    {
        lock (Sync)
        {
            _row = row;
            _dispatcher = uiDispatcher;
            _owner = owner;
        }
    }

    internal static bool HasPendingContext()
    {
        lock (Sync)
            return _row != null;
    }

    internal static void Complete(HatchStyleSnapshot snap)
    {
        var json = snap.ToJson();
        CommandCatalogRow? r;
        Dispatcher? d;
        FloatingPanelWindow? owner;
        lock (Sync)
        {
            r = _row;
            d = _dispatcher;
            owner = _owner;
            ClearCore();
        }

        if (r == null || d == null)
            return;
        _ = d.BeginInvoke(() =>
        {
            r.LayerRunDimensionWhenNoSelection = false;
            r.LayerHatchStyleJson = json;
            r.IsUserModified = true;
            owner?.ShowAfterHatchStylePick();
        });
    }

    /// <summary>取消拾取或失败时重新显示配置窗口。</summary>
    internal static void RestorePanelIfHidden()
    {
        FloatingPanelWindow? owner;
        Dispatcher? d;
        lock (Sync)
        {
            owner = _owner;
            d = _dispatcher;
            ClearCore();
        }

        if (owner == null || d == null)
            return;
        _ = d.BeginInvoke(() => owner.ShowAfterHatchStylePick());
    }

    internal static void Clear()
    {
        lock (Sync)
            ClearCore();
    }

    private static void ClearCore()
    {
        _row = null;
        _dispatcher = null;
        _owner = null;
    }
}
