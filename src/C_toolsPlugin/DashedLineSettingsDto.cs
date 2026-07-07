namespace C_toolsPlugin;

internal sealed class DashedLineSettingsDto
{
    public int Version { get; set; }

    public string LinetypeName { get; set; } = LinetypeNames.Dashed;

    public double LinetypeScale { get; set; } = 1.0;

    public string ColorMode { get; set; } = DashedLineColorModes.Keep;

    public int? ColorIndex { get; set; }

    public string TargetLayerName { get; set; } = "";
}

internal static class DashedLineColorModes
{
    internal const string Keep = "Keep";
    internal const string ByLayer = "ByLayer";
    internal const string ByBlock = "ByBlock";
    internal const string Aci = "Aci";
}
