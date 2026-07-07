using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(C_toolsAaaPlugin.AaaPluginApp))]
[assembly: CommandClass(typeof(C_toolsAaaPlugin.AaaCommands))]

namespace C_toolsAaaPlugin;

public class AaaPluginApp : CadPluginAppBase
{
    protected override string PluginLogPrefix => C_toolsCommandIds.Aaa.Main;

    protected override void OnFirstIdleCore()
    {
        C_toolsPaths.EnsureFolders();
    }

    protected override void OnTerminateCore()
    {
        AaaCommands.CloseAaaPanelIfAny();
    }
}
