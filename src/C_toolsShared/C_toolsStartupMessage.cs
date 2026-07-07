using Autodesk.AutoCAD.ApplicationServices;
using System.Threading;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

internal static class C_toolsStartupMessage
{
    private const string Message = "\nC_TOOL插件已顺利加载成功，系统运行正常，随时为您服务～";
    private static int s_hasShown;
    private static int s_retryHooksAttached;

    public static void TryShowOnce()
    {
        if (Volatile.Read(ref s_hasShown) != 0)
            return;

        EnsureRetryHooks();

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        if (Interlocked.Exchange(ref s_hasShown, 1) != 0)
            return;

        try
        {
            doc.Editor.WriteMessage(Message);
            DetachRetryHooks();
        }
        catch (System.Exception ex)
        {
            Interlocked.Exchange(ref s_hasShown, 0);
            C_toolsDiagnostics.LogNonFatal("写入 C_TOOL 启动提示失败", ex);
        }
    }

    private static void EnsureRetryHooks()
    {
        if (Volatile.Read(ref s_hasShown) != 0)
        {
            DetachRetryHooks();
            return;
        }

        if (Interlocked.Exchange(ref s_retryHooksAttached, 1) != 0)
            return;

        try
        {
            AcAp.DocumentManager.DocumentCreated += OnDocumentAvailable;
            AcAp.DocumentManager.DocumentActivated += OnDocumentAvailable;
        }
        catch
        {
            Interlocked.Exchange(ref s_retryHooksAttached, 0);
        }
    }

    private static void DetachRetryHooks()
    {
        if (Interlocked.Exchange(ref s_retryHooksAttached, 0) == 0)
            return;

        try
        {
            AcAp.DocumentManager.DocumentCreated -= OnDocumentAvailable;
            AcAp.DocumentManager.DocumentActivated -= OnDocumentAvailable;
        }
        catch
        {
            // 忽略宿主关闭阶段的事件解绑异常
        }
    }

    private static void OnDocumentAvailable(object? sender, DocumentCollectionEventArgs e)
    {
        _ = sender;
        _ = e;
        TryShowOnce();
    }
}
