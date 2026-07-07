namespace C_toolsPlugin;

/// <summary>图层命令行代号规则（与 AutoLISP <c>(defun c:XXX)</c> 及历史 DLL 生成器一致）。</summary>
internal static class LayerAliasRules
{
    private static readonly string[] ProtectedCadCommandAliases =
        AcadNativeCommandDescriptions.CommandNames
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly HashSet<string> ProtectedCadCommandAliasSet =
        new(ProtectedCadCommandAliases, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> ProtectedCadCommandNames => ProtectedCadCommandAliases;

    internal static bool IsProtectedCadCommandName(string normalizedAlias) =>
        !string.IsNullOrWhiteSpace(normalizedAlias) &&
        ProtectedCadCommandAliasSet.Contains(normalizedAlias.Trim());

    internal static bool IsValidGeneratedCommandAlias(string normalizedAlias, out string reason)
    {
        reason = "";
        if (normalizedAlias.Length == 0)
        {
            reason = "空别名";
            return false;
        }

        if (normalizedAlias.Length > 32)
        {
            reason = "别名过长";
            return false;
        }

        if (IsProtectedCadCommandName(normalizedAlias))
        {
            reason = "与 CAD 自带命令同名";
            return false;
        }

        // 纯数字：生成 (defun c:123 …)，符记名为单一代号且以字母 c 起头，AutoLISP 可接受。
        if (IsAllAsciiDigits(normalizedAlias))
            return true;

        if (!CharAsciiCompat.IsAsciiLetter(normalizedAlias[0]) && normalizedAlias[0] != '_')
        {
            reason = "须以英文字母或下划线开头";
            return false;
        }

        for (var i = 0; i < normalizedAlias.Length; i++)
        {
            var c = normalizedAlias[i];
            if (CharAsciiCompat.IsAsciiLetterOrDigit(c) || c == '_')
                continue;
            reason = "仅允许英文字母、数字、下划线";
            return false;
        }

        return true;
    }

    private static bool IsAllAsciiDigits(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }
}
