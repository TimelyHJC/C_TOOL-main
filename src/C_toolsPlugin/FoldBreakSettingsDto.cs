namespace C_toolsPlugin;

internal sealed class FoldBreakSettingsDto
{
    public int Version { get; set; }

    public double HorizontalLeftPart { get; set; }

    public double HorizontalRightPart { get; set; }

    public double VerticalTopPart { get; set; }

    public double VerticalBottomPart { get; set; }

    public int ColorIndex { get; set; }
}
