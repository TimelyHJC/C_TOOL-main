using System.Collections.Generic;

namespace C_toolsPlugin;

/// <summary>
/// 为命令目录提供默认别名：优先使用显式短别名；若命令名本身是短 <c>V_*</c>/<c>F_*</c> 形式，则默认取前缀后的短字母。
/// </summary>
internal static class CommandAliasDefaults
{
    private static readonly Dictionary<string, string[]> ExplicitAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["COPY"] = ["CC"],
        ["MATCHPROP"] = ["V"],
        ["RECTANG"] = ["R"],
        ["ROTATE"] = ["RR"],
        [C_toolsCommandIds.MainToolset.LayerDisplayToggle] = [C_toolsCommandIds.MainToolset.LayerDisplayToggleAliasShort],
        [C_toolsCommandIds.Sys.Main] = [C_toolsCommandIds.Sys.AliasShort],
        [C_toolsCommandIds.Aaa.Main] = [C_toolsCommandIds.Aaa.AliasShort],
        [C_toolsCommandIds.Bbb.Main] = [C_toolsCommandIds.Bbb.AliasShort],
        [C_toolsCommandIds.Ddd.Main] = [C_toolsCommandIds.Ddd.AliasShort],
        [C_toolsCommandIds.Qqq.Main] = [C_toolsCommandIds.Qqq.AliasShort]
    };

    internal static void ApplyMissingAliases(IEnumerable<CommandCatalogRow> rows)
    {
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Alias))
                continue;
            if (!TryGetDefaultAliasCell(row, out var aliasCell))
                continue;
            row.SetSuggestedDefaultAlias(aliasCell);
        }
    }

    private static bool TryGetDefaultAliasCell(CommandCatalogRow row, out string aliasCell)
    {
        aliasCell = "";
        if (row == null)
            return false;

        if (TryGetDefaultAliasCell(row.CommandName, out aliasCell))
            return true;

        return TryGetDirectCommandAliasCell(row.CommandName, row.CategoryTag, out aliasCell);
    }

    internal static bool TryGetDefaultAliasCell(string? commandName, out string aliasCell)
    {
        aliasCell = "";
        var cmd = (commandName ?? "").Trim();
        if (cmd.Length == 0)
            return false;

        if (ExplicitAliases.TryGetValue(cmd, out var explicitAliases))
        {
            var normalized = explicitAliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalized.Count == 0)
                return false;
            aliasCell = string.Join(", ", normalized);
            return true;
        }

        if (!TryGetShortPrefixAlias(cmd, out var shortAlias))
            return false;

        aliasCell = shortAlias;
        return true;
    }

    /// <summary>
    /// 外部插件里常见的短命令本身就是直接输入的命令字，如 KDR / CTP / BBOO。
    /// 这类命令在目录中也作为「默认别名」展示，但不写回用户 PGP，避免覆盖用户自定义。
    /// </summary>
    private static bool TryGetDirectCommandAliasCell(string? commandName, string? categoryTag, out string aliasCell)
    {
        aliasCell = "";
        if (string.Equals(categoryTag, CadCommandCatalogBuilder.TagCadNative, StringComparison.Ordinal) ||
            string.Equals(categoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal))
        {
            return false;
        }

        var cmd = (commandName ?? "").Trim();
        if (cmd.Length < 2 || cmd.Length > 10 || cmd.IndexOf('_') >= 0)
            return false;
        if (!string.Equals(cmd, cmd.ToUpperInvariant(), StringComparison.Ordinal))
            return false;

        var hasAsciiLetter = false;
        foreach (var c in cmd)
        {
            if (!CharAsciiCompat.IsAsciiLetterOrDigit(c))
                return false;
            if (CharAsciiCompat.IsAsciiLetter(c))
                hasAsciiLetter = true;
        }

        if (!hasAsciiLetter)
            return false;

        aliasCell = cmd;
        return true;
    }

    private static bool TryGetShortPrefixAlias(string commandName, out string alias)
    {
        alias = "";
        if (!commandName.StartsWith("V_", StringComparison.OrdinalIgnoreCase) &&
            !commandName.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = commandName[2..].Trim();
        if (suffix.Length < 2 || suffix.Length > 4 || suffix.IndexOf('_') >= 0)
            return false;

        foreach (var c in suffix)
        {
            if (!CharAsciiCompat.IsAsciiLetterOrDigit(c))
                return false;
        }

        alias = suffix.ToUpperInvariant();
        return true;
    }
}
