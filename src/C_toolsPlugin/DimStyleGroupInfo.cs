namespace C_toolsPlugin;

/// <summary>按标注样式名称前缀划分的分组；名称首段带“内”后缀时单独分组。</summary>
internal sealed class DimStyleGroupInfo
{
    internal DimStyleGroupInfo(string prefix, IReadOnlyList<string> styleNames)
    {
        Prefix = prefix;
        StyleNames = styleNames;
    }

    /// <summary>分组键：名称前 1～2 个字符；若首段带“内”后缀则追加“内”。</summary>
    public string Prefix { get; }

    public IReadOnlyList<string> StyleNames { get; }

    public string DisplayLabel => $"{Prefix}（{StyleNames.Count} 个）";

    public override string ToString() => DisplayLabel;
}
