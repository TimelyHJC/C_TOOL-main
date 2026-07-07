using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(C_toolsBbbPlugin.BbbPluginApp))]
[assembly: CommandClass(typeof(C_toolsBbbPlugin.BbbCommands))]

namespace C_toolsBbbPlugin;

public class BbbPluginApp : CadPluginAppBase
{
    protected override string PluginLogPrefix => C_toolsCommandIds.Bbb.Main;

    protected override void OnFirstIdleCore()
    {
        C_toolsPaths.EnsureFolders();
    }

    protected override void OnTerminateCore()
    {
        BbbCommands.CloseBbbPanelIfAny();
    }
}
