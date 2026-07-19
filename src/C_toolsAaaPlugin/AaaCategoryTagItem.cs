namespace C_toolsAaaPlugin;

internal sealed class AaaCategoryTagItem
{
    internal const string ComboLibraryKey = "__COMBO_LIBRARY__";
    internal const string SingleLibraryKey = "__SINGLE_LIBRARY__";

    public string Key { get; set; } = ComboLibraryKey;
    public string DisplayName { get; set; } = "独立图库";
    public int Count { get; set; }

    public string DisplayText => $"{DisplayName} ({Count})";
}
