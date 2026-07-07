namespace C_toolsSysPlugin;

internal static class SysPluginCommandIds
{
    internal const string CommandGroup = C_toolsCommandIds.CommandGroup;

    /// <summary>系统配置浮窗（别名 YY）：标注样式、打印与保存、路径说明，以及 .arg 安全应用。</summary>
    internal const string Yyy = C_toolsCommandIds.Sys.Main;

    /// <summary>打开系统配置浮窗并切到「标注样式」页（辅助命令 F_YYY）。</summary>
    internal const string YyyLeaderStyle = C_toolsCommandIds.Sys.OpenDimStyleTab;

    /// <summary>打开系统配置浮窗并切到「打印与保存」页（供 V_QQQ 设置入口调用）。</summary>
    internal const string YyyPrintSave = C_toolsCommandIds.Sys.OpenPrintSaveTab;
}
