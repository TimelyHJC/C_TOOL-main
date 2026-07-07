using Microsoft.Win32;

namespace C_toolsSetup;

/// <summary>从 HKCU / HKLM 枚举 AutoCAD 配置（用于下拉选择要写入 TRUSTEDPATHS 的版本）。</summary>
internal static class AcadInstallationScanner
{
    /// <summary>仅列出带此后缀的 <c>ACAD-*</c> 产品键（简体中文安装常见为 <c>:804</c>），忽略无语言后缀的重复项。</summary>
    internal const string ProductKeyZhCnLocaleSuffix = ":804";

    internal sealed record Choice(
        string Display,
        string? VersionKey,
        string? ProductKey,
        bool IsPreferredLocale,
        bool IsLocaleFallback,
        bool IsManualInstallOnly);

    /// <summary>
    /// 优先返回带 <see cref="ProductKeyZhCnLocaleSuffix"/> 的 R* / ACAD-*；若当前机器未安装该语言包，则自动回退到其它语言产品键。
    /// </summary>
    internal static IReadOnlyList<Choice> EnumerateChoices()
    {
        var allChoices = new List<Choice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendChoicesFromHive(Registry.CurrentUser, @"Software\Autodesk\AutoCAD", allChoices, seen);
        try
        {
            using var lm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
            AppendChoicesFromKey(lm, allChoices, seen);
        }
        catch
        {
            // 无权限访问 HKLM 时忽略
        }

        var selected = SelectPreferredChoices(allChoices);
        if (selected.Count > 0)
            return selected;

        return
        [
            new Choice(
                "未检测到 AutoCAD：仅复制插件文件（手动模式）",
                VersionKey: null,
                ProductKey: null,
                IsPreferredLocale: false,
                IsLocaleFallback: false,
                IsManualInstallOnly: true)
        ];
    }

    private static IReadOnlyList<Choice> SelectPreferredChoices(IEnumerable<Choice> choices)
    {
        var preferred = choices
            .Where(choice => !choice.IsManualInstallOnly)
            .Where(choice => choice.IsPreferredLocale)
            .OrderByDescending(choice => GetReleaseSortKey(choice.VersionKey))
            .ThenBy(choice => choice.ProductKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (preferred.Count > 0)
            return preferred;

        return choices
            .OrderByDescending(choice => GetReleaseSortKey(choice.VersionKey))
            .ThenBy(choice => choice.ProductKey, StringComparer.OrdinalIgnoreCase)
            .Select(choice => choice with
            {
                Display = choice.Display + "  （语言回退）",
                IsLocaleFallback = true
            })
            .ToList();
    }

    private static int GetReleaseSortKey(string? versionKey) =>
        AcadReleaseTargeting.TryParseReleaseMajor(versionKey, out var major) ? major : -1;

    private static void AppendChoicesFromHive(RegistryKey hiveRoot, string cadRelativePath, List<Choice> list, HashSet<string> seen)
    {
        using var cadRoot = hiveRoot.OpenSubKey(cadRelativePath);
        AppendChoicesFromKey(cadRoot, list, seen);
    }

    private static void AppendChoicesFromKey(RegistryKey? cadRoot, List<Choice> list, HashSet<string> seen)
    {
        if (cadRoot == null)
            return;

        foreach (var versionName in cadRoot.GetSubKeyNames())
        {
            if (versionName.Length < 2 || versionName[0] != 'R')
                continue;
            if (!AcadReleaseTargeting.TryParseReleaseMajor(versionName, out var releaseMajor) || releaseMajor != 24)
                continue;

            using var verKey = cadRoot.OpenSubKey(versionName);
            if (verKey == null)
                continue;

            foreach (var productName in verKey.GetSubKeyNames())
            {
                if (!productName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var prodKey = verKey.OpenSubKey(productName);
                if (prodKey == null)
                    continue;

                var key = versionName + "|" + productName;
                if (!seen.Add(key))
                    continue;

                var isPreferredLocale = productName.Contains(ProductKeyZhCnLocaleSuffix, StringComparison.Ordinal);
                list.Add(new Choice(
                    $"{versionName}  /  {productName}",
                    versionName,
                    productName,
                    isPreferredLocale,
                    IsLocaleFallback: false,
                    IsManualInstallOnly: false));
            }
        }
    }
}
