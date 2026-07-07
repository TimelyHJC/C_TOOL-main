using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(C_toolsSysPlugin.SysPluginApp))]
[assembly: CommandClass(typeof(C_toolsSysPlugin.SysCommands))]

namespace C_toolsSysPlugin;

public class SysPluginApp : CadPluginAppBase
{
    protected override string PluginLogPrefix => C_toolsCommandIds.Sys.Main;

    protected override void OnFirstIdleCore()
    {
        C_toolsPaths.EnsureFolders();
        C_toolsPlugin.PrintSaveAutoSaveService.Initialize();
    }

    protected override void OnTerminateCore()
    {
        SysCommands.CloseSysConfigPanelIfAny();
        C_toolsPlugin.PrintSaveAutoSaveService.Terminate();
    }
}
