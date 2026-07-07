namespace C_toolsPlugin;

/// <summary>
/// 命令表内置说明：用于首次扫描时填充默认值，也用于判断某条说明是否只是默认文案。
/// </summary>
internal static class CommandDescriptionDefaults
{
    internal static bool TryGet(CommandCatalogRow row, out string description)
    {
        description = "";
        if (row == null)
            return false;
        return TryGet(row.CommandName, row.CategoryTag, out description);
    }

    internal static bool TryGet(string? commandName, string? categoryTag, out string description)
    {
        description = "";
        var cmd = (commandName ?? "").Trim();
        if (cmd.Length == 0)
            return false;

        if (string.Equals(categoryTag, CadCommandCatalogBuilder.TagCadNative, StringComparison.Ordinal) &&
            AcadNativeCommandDescriptions.TryGet(cmd, out description))
            return true;

        if (FeatureCommandCatalog.TryGetDescription(cmd, out description))
            return true;

        return !string.Equals(categoryTag, CadCommandCatalogBuilder.TagCadNative, StringComparison.Ordinal) &&
               ExternalPluginCommandDescriptions.TryGet(cmd, out description);
    }
}
