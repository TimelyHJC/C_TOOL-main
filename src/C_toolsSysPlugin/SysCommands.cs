using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using C_toolsPlugin;

namespace C_toolsSysPlugin;

public class SysCommands
{
    private static readonly ModelessWindowHost<SysConfigPanelWindow> s_sysPanelHost = new();

    [CommandMethod(SysPluginCommandIds.CommandGroup, SysPluginCommandIds.Yyy, CommandFlags.Modal)]
    [CommandMethod(SysPluginCommandIds.CommandGroup, C_toolsCommandIds.Sys.AliasShort, CommandFlags.Modal)]
    public void ToggleSysConfigPanel()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "打开系统配置浮窗",
            (_, _) => s_sysPanelHost.Toggle(() => new SysConfigPanelWindow()));
    }

    [CommandMethod(SysPluginCommandIds.CommandGroup, SysPluginCommandIds.YyyLeaderStyle, CommandFlags.Modal)]
    [CommandMethod(C_toolsCommandIds.Sys.OpenDimStyleTab, CommandFlags.Modal)]
    public void OpenSysConfigAtDimStyleTab()
    {
        CadCommandContext.ExecuteInActiveDocument("打开系统配置浮窗并定位到标注样式页", (_, _) =>
            s_sysPanelHost.Show(
                () => new SysConfigPanelWindow(),
                panel => panel.NavigateToDimStyleTab()));
    }

    [CommandMethod(SysPluginCommandIds.CommandGroup, SysPluginCommandIds.YyyPrintSave, CommandFlags.Modal)]
    [CommandMethod(C_toolsCommandIds.Sys.OpenPrintSaveTab, CommandFlags.Modal)]
    public void OpenSysConfigAtPrintSaveTab()
    {
        CadCommandContext.ExecuteInActiveDocument("打开系统配置浮窗并定位到打印保存页", (_, _) =>
            s_sysPanelHost.Show(
                () => new SysConfigPanelWindow(),
                panel => panel.NavigateToPrintSaveTab()));
    }

    internal static void CloseSysConfigPanelIfAny()
    {
        s_sysPanelHost.CloseIfAny(
            onInvalidOperation: ex => C_toolsDiagnostics.LogNonFatal("关闭系统配置浮窗失败（无效操作）", ex),
            onError: ex => C_toolsDiagnostics.LogNonFatal("关闭系统配置浮窗失败", ex));
    }
}
