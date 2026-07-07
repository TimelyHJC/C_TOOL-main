using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(C_toolsQqqPlugin.QqqPluginApp))]
[assembly: CommandClass(typeof(C_toolsQqqPlugin.QqqCommands))]

namespace C_toolsQqqPlugin;

public class QqqPluginApp : CadPluginAppBase
{
    protected override string PluginLogPrefix => C_toolsCommandIds.Qqq.Main;

    protected override void OnInitializeCore()
    {
        QqqRuntimeAssemblyResolver.EnsureInitialized();
    }

    protected override void OnFirstIdleCore()
    {
        C_toolsPaths.EnsureFolders();
    }

    protected override void OnTerminateCore()
    {
        QqqCommands.CloseQqqPanelIfAny();
    }
}
