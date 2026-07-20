namespace C_toolsQqqPlugin;

internal sealed class QqqPlotOptions
{
    public bool UseCadPlotSettings { get; set; }
    public string PageSetupName { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string MediaName { get; set; } = "";
    public string StyleSheet { get; set; } = "";
    public string ScaleText { get; set; } = "布满图纸";
    public string FileNameTemplate { get; set; } = "";
    public string TadLabelName { get; set; } = "";
    public bool AutoRotate { get; set; } = true;
    public bool CenterPlot { get; set; } = true;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool FitToPaper { get; set; } = true;
    public bool AdaptPaper { get; set; } = true;
    public bool ScaleLineweights { get; set; } = true;
    public bool Landscape { get; set; } = true;
    public bool UpsideDown { get; set; }
    public string SortRule { get; set; } = "";
    public bool PlotToFile { get; set; } = true;
    public string OutputFolder { get; set; } = "";
}

internal sealed class QqqPlotRunResult
{
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string OutputFile { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string FallbackOutputFolder { get; set; } = "";
    public int FallbackOutputFileCount { get; set; }
    public string MergeErrorMessage { get; set; } = "";
    public List<string> ErrorMessages { get; } = new();
    public List<string> InfoMessages { get; } = new();
}
