namespace C_toolsPlugin;

/// <summary>F_WCC 墙面完成面命令的用户图层设置。</summary>
internal sealed class WallFinishSettingsDto
{
    public int Version { get; set; }

    public double OffsetDistance { get; set; }

    /// <summary>完成面图层颜色；ACI 1-255，空表示沿用目标图层现有颜色。</summary>
    public int? ColorIndex { get; set; }

    /// <summary>
    /// 目标图层名；留空表示自动模式：
    /// 优先使用说明为“完成面”的唯一图层快捷层，否则沿用源墙面线图层。
    /// </summary>
    public string TargetLayerName { get; set; } = "";

    /// <summary>上次使用的命令模式；Quick 或 Standard。</summary>
    public string Mode { get; set; } = "";
}
