using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// 写入 acad.pgp 后触发 PGP 表重载。优先使用系统变量 RE-INIT 位 16（无对话框）。
/// </summary>
internal static class CadPgpReload
{
    private static class SysVars
    {
        public const string ReInit = "RE-INIT";
    }

    private const int ReInitPgpFlag = 16;

    internal static (bool Ok, string Message) TryReloadPgp(Document? doc)
    {
        CadPgpSupportPath.EnsureC_toolsSupportFirst();

        if (CadSystemVariableService.TrySetValue(SysVars.ReInit, ReInitPgpFlag))
            return (true, $"已设置 {SysVars.ReInit}={ReInitPgpFlag}，PGP 别名表已重载。");

        return TrySendStringFallback(doc);
    }

    private static (bool Ok, string Message) TrySendStringFallback(Document? doc)
    {
        try
        {
            var d = doc ?? AcAp.DocumentManager.MdiActiveDocument;
            if (d == null)
                return (false, "无活动文档，无法通过 setvar 重载 PGP。");

            d.SendStringToExecute($"(setvar \"{SysVars.ReInit}\" {ReInitPgpFlag})\n", true, false, false);
            return (true, $"已通过 (setvar \"{SysVars.ReInit}\" {ReInitPgpFlag}) 请求重载 PGP。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("TryReloadPgp：SendString 回退失败（无效操作）", ex);
            return (false, $"自动重载失败：{ex.Message}。请手动执行 REINIT 并勾选「PGP 文件」。");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("TryReloadPgp：SendString 回退仍失败", ex);
            return (false, $"自动重载失败：{ex.Message}。请手动执行 REINIT 并勾选「PGP 文件」。");
        }
    }
}
