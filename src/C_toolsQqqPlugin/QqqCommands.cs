using Autodesk.AutoCAD.Runtime;

namespace C_toolsQqqPlugin;

public class QqqCommands
{
    private static readonly ModelessWindowHost<QqqPlotWindow> s_qqqWindowHost = new();

    [CommandMethod(QqqPluginCommandIds.CommandGroup, QqqPluginCommandIds.Qqq, CommandFlags.Modal | CommandFlags.UsePickSet)]
    [CommandMethod(QqqPluginCommandIds.CommandGroup, C_toolsCommandIds.Qqq.AliasShort, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ToggleQqqPanel()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "打开批量打印面板",
            (_, _) => s_qqqWindowHost.ShowOrActivate(() => new QqqPlotWindow()));
    }

    internal static void ShowBatchPanel()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "显示批量打印面板",
            (_, _) => s_qqqWindowHost.ShowOrActivate(() => new QqqPlotWindow()));
    }

    internal static void CloseQqqPanelIfAny()
    {
        s_qqqWindowHost.CloseIfAny(
            onInvalidOperation: ex => C_toolsDiagnostics.LogNonFatal("关闭 V_QQQ 面板失败（无效操作）", ex),
            onError: ex => C_toolsDiagnostics.LogNonFatal("关闭 V_QQQ 面板失败", ex));
    }
}
