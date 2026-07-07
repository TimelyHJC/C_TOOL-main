namespace C_toolsPlugin;

/// <summary>F_SQT 快速墙体命令的用户设置。</summary>
internal sealed class QuickWallSettingsDto
{
    public int Version { get; set; }

    public string WallLayerName { get; set; } = "";

    public int? ColorIndex { get; set; }

    public bool UseSecondaryWidth { get; set; }

    public double PrimaryWidth { get; set; }

    public double? SecondaryWidth { get; set; }
}
