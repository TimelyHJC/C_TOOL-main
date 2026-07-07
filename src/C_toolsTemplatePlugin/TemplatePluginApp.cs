using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(C_toolsTemplatePlugin.TemplatePluginApp))]
[assembly: CommandClass(typeof(C_toolsTemplatePlugin.TemplateCommands))]

namespace C_toolsTemplatePlugin;

/// <summary>
/// AutoCAD 模板插件入口。
/// </summary>
public sealed class TemplatePluginApp : CadPluginAppBase
{
    protected override string PluginLogPrefix => "[CTPL]";

    protected override void OnFirstIdleCore()
    {
        // 模板默认不做额外初始化，保持最小可用骨架。
    }
}
