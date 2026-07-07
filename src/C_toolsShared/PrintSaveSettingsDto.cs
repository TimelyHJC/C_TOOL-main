namespace C_toolsShared;

/// <summary>「打印与保存」当前布局打印参数（内存 DTO，用于应用到 CAD / V_QQQ）。</summary>
public sealed class PrintSavePageFileDto
{
    public int Version { get; set; } = 1;

    /// <summary>页面设置名称（当前未持久化，仅保留兼容位）。</summary>
    public string PageSetupName { get; set; } = "";

    public string PrinterName { get; set; } = "";
    public string CanonicalMediaName { get; set; } = "";
    public string StyleSheet { get; set; } = "";
    public bool CenterPlot { get; set; } = true;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool FitToPaper { get; set; } = true;
    public string ScaleText { get; set; } = PrintSaveService.ScaleFitText;
    public bool ScaleLineweights { get; set; } = true;
    public bool AutoMatchOrientation { get; set; }
    public bool Landscape { get; set; } = true;
    public bool UpsideDown { get; set; }
}

/// <summary>V_YYY / V_QQQ 共用的打印与保存配置，存于用户配置目录。</summary>
public sealed class PrintSaveAutoOptionsDto
{
    public int Version { get; set; } = 1;

    /// <summary>定时对当前图纸执行 QSAVE 的间隔（分钟），0 表示关闭。</summary>
    public int IntervalMinutes { get; set; }

    /// <summary>「保存 CAD 到目录」时使用的默认根目录（默认 D:\C_tool插件）。</summary>
    public string SaveBasePath { get; set; } = @"D:\C_tool插件";

    public string PrinterName { get; set; } = PrintSaveService.DefaultPrinterName;
    public string CanonicalMediaName { get; set; } = PrintSaveService.MediaAutoMatchText;
    public string StyleSheet { get; set; } = PrintSaveService.DefaultStyleSheet;
    public bool CenterPlot { get; set; } = true;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool FitToPaper { get; set; } = true;
    public string ScaleText { get; set; } = PrintSaveService.ScaleFitText;
    public bool ScaleLineweights { get; set; } = true;
    public bool AutoMatchOrientation { get; set; }
    public bool Landscape { get; set; } = true;
    public bool UpsideDown { get; set; }
    public string PrintOrderRule { get; set; } = PrintSaveService.PlotOrderAddedOrder;
}
