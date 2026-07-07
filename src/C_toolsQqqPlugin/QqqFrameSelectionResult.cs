namespace C_toolsQqqPlugin;

internal sealed class QqqFrameSelectionResult
{
    public List<QqqPlotFrameInfo> Frames { get; set; } = new();
    public int SelectedCount { get; set; }
    public int AcceptedCount { get; set; }
    public string Message { get; set; } = "";
}
