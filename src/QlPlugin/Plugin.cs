using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(QlPlugin.Plugin))]

namespace QlPlugin;

/// <summary>
/// 插件入口，加载时自动注册
/// </summary>
public class Plugin : IExtensionApplication
{
    public void Initialize()
    {
        // 插件加载时的初始化
    }

    public void Terminate()
    {
        // 插件卸载时的清理
    }
}
