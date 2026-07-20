namespace C_toolsBbbPlugin;

internal static class BbbPluginCommandIds
{
    internal const string CommandGroup = C_toolsCommandIds.CommandGroup;

    /// <summary>设备清单浮窗：读取图块显示设备名称并写入 Excel 设备清单。</summary>
    internal const string Bbb = C_toolsCommandIds.Bbb.Main;

    /// <summary>快速把选中的 DBText/MText 转成带“设备名称”属性的块参照。</summary>
    internal const string TextToAttribute = C_toolsCommandIds.Bbb.TextToAttribute;

    /// <summary>把选中对象创建为设备块，基点可用九宫格选择，块名可从 CAD 文字拾取。</summary>
    internal const string DeviceBlockCreate = C_toolsCommandIds.Bbb.DeviceBlockCreate;

    /// <summary>刷新选中图块的增强属性显示，相当于对选中块执行属性同步。</summary>
    internal const string BlockAttributeRefresh = C_toolsCommandIds.Bbb.BlockAttributeRefresh;
    /// <summary>同步块属性参照。</summary>
    internal const string BlockAttributeSync = C_toolsCommandIds.Bbb.BlockAttributeSync;

    /// <summary>历史 F_BXR 命令号；已停用，仅保留字符串常量兼容。</summary>
    internal const string BlockAssignHiddenDeviceNames = C_toolsCommandIds.Bbb.BlockAssignHiddenDeviceNames;

    /// <summary>历史 F_BXF 命令号；已停用，仅保留字符串常量兼容。</summary>
    internal const string BlockExportMappedDeviceNames = C_toolsCommandIds.Bbb.BlockExportMappedDeviceNames;
}
