namespace C_toolsShared;

/// <summary>文字标注「设置引线」等使用的插件引线参数（User 目录 JSON）。</summary>
public sealed class MLeaderToolSettingsDto
{
    public int Version { get; set; } = 4;

    /// <summary>当前应用的 DDD 引线版式预设 ID；空表示未选择预设。</summary>
    public string LayoutPresetId { get; set; } = "";

    /// <summary>引线与箭头同色：BYLAYER / BYBLOCK / 0～256；空字符串表示不覆盖，沿用当前多重引线样式。</summary>
    public string LeaderArrowColor { get; set; } = "";

    /// <summary>箭头块名；空表示沿用当前多重引线样式中的箭头符号。</summary>
    public string ArrowBlockName { get; set; } = "";

    /// <summary>箭头大小；0 表示不覆盖样式默认。</summary>
    public double ArrowSize { get; set; }

    /// <summary>插入多重引线时 MText 字高（图纸单位）；0 表示沿用当前多重引线样式（CMLEADERSTYLE）中的字高。</summary>
    public double TextHeight { get; set; }

    /// <summary>文字样式名；空表示沿用样式默认。</summary>
    public string TextStyleName { get; set; } = "";

    /// <summary>文字颜色：BYLAYER / BYBLOCK / 0～256；空字符串表示不覆盖，沿用样式。</summary>
    public string TextColor { get; set; } = "";

    /// <summary>水平连接时「连接位置-左」：TextAttachmentType 枚举名；空表示不覆盖，沿用样式。</summary>
    public string LeftTextAttachmentType { get; set; } = "";

    /// <summary>水平连接时「连接位置-右」；空表示不覆盖，沿用样式。</summary>
    public string RightTextAttachmentType { get; set; } = "";

    /// <summary>文字连接方向：<see cref="Autodesk.AutoCAD.DatabaseServices.TextAttachmentDirection"/> 枚举名；空表示不覆盖。</summary>
    public string TextAttachmentDirection { get; set; } = "";

    /// <summary>写入当前多重引线样式的最大引线点数（与样式「引线结构」一致）；≤0 表示不修改样式。</summary>
    public int MaxLeaderSegmentsPoints { get; set; }

    /// <summary>是否覆盖样式中的 EnableLanding；null 表示不覆盖。</summary>
    public bool? EnableLanding { get; set; }

    /// <summary>是否覆盖样式中的 EnableDogleg；null 表示不覆盖。</summary>
    public bool? EnableDogleg { get; set; }

    /// <summary>Dogleg 长度（图纸单位）；0 表示不覆盖样式。</summary>
    public double DoglegLength { get; set; }

    /// <summary>基线长度（图纸单位），对应 MLeader.LandingGap；0 表示不覆盖样式。</summary>
    public double LandingGap { get; set; }
}
