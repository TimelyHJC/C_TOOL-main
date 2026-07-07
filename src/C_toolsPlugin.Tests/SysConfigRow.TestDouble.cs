namespace C_toolsPlugin;

public sealed class SysConfigRow
{
    public SysConfigRow(
        string varName,
        string initialValue,
        string comment,
        string? argRegistryKey = null,
        bool argIsDword = false)
    {
        VarName = varName;
        Value = initialValue;
        Comment = comment;
        ArgRegistryKey = argRegistryKey;
        ArgIsDword = argIsDword;
    }

    public string VarName { get; }

    public string Value { get; }

    public string Comment { get; }

    public string? ArgRegistryKey { get; }

    public bool ArgIsDword { get; }
}
