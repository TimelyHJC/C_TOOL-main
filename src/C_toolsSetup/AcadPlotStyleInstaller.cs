using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace C_toolsSetup;

/// <summary>安装初始化文件夹中的 CTB/STB 到 AutoCAD 打印样式表目录。</summary>
internal static class AcadPlotStyleInstaller
{
    private const string CadRegistryRoot = @"Software\Autodesk\AutoCAD";
    private const string PrinterStyleSheetDirValueName = "PrinterStyleSheetDir";

    internal static int InstallPlotStyleToProfiles(
        string? sourcePlotStylePath,
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(sourcePlotStylePath) || !File.Exists(sourcePlotStylePath))
        {
            log?.Invoke($"未找到 {BundleInstall.DefaultPlotStyleFileName}，跳过复制到 CAD 打印样式目录。");
            return 0;
        }

        var source = Path.GetFullPath(sourcePlotStylePath);
        var fileName = Path.GetFileName(source);
        var targetDirs = ResolveTargetPlotStyleDirectories(acadVersionKey, acadProductKey, log)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetDirs.Count == 0)
        {
            log?.Invoke($"未找到 CAD 打印样式目录，未复制 {fileName}。");
            return 0;
        }

        var copied = 0;
        foreach (var targetDir in targetDirs)
        {
            try
            {
                Directory.CreateDirectory(targetDir);
                var dest = Path.Combine(targetDir, fileName);
                File.Copy(source, dest, overwrite: true);
                copied++;
                log?.Invoke($"已复制打印样式：{fileName} -> {targetDir}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                log?.Invoke($"复制打印样式失败：{targetDir} — {ex.Message}");
            }
        }

        return copied;
    }

    private static IEnumerable<string> ResolveTargetPlotStyleDirectories(
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        var foundFromProfiles = false;
        using var cadRoot = Registry.CurrentUser.OpenSubKey(CadRegistryRoot, writable: false);
        if (cadRoot != null)
        {
            foreach (var versionName in cadRoot.GetSubKeyNames())
            {
                if (!ShouldUseVersion(versionName, acadVersionKey))
                    continue;

                using var versionKey = cadRoot.OpenSubKey(versionName, writable: false);
                if (versionKey == null)
                    continue;

                foreach (var productName in versionKey.GetSubKeyNames())
                {
                    if (!ShouldUseProduct(productName, acadProductKey))
                        continue;

                    var macros = BuildMacroValues(versionName, productName);
                    var profilesPath = $"{versionName}\\{productName}\\Profiles";
                    using var profiles = cadRoot.OpenSubKey(profilesPath, writable: false);
                    if (profiles == null)
                        continue;

                    foreach (var profileName in profiles.GetSubKeyNames())
                    {
                        using var general = profiles.OpenSubKey(profileName + "\\General", writable: false);
                        var rawPath = general?.GetValue(PrinterStyleSheetDirValueName) as string;
                        foreach (var resolved in ResolvePathExpression(rawPath, versionName, productName, macros))
                        {
                            foundFromProfiles = true;
                            yield return resolved;
                        }
                    }
                }
            }
        }

        if (foundFromProfiles)
            yield break;

        foreach (var fallback in ResolveFallbackPlotStyleDirectories(acadVersionKey, acadProductKey, log))
            yield return fallback;
    }

    private static IEnumerable<string> ResolveFallbackPlotStyleDirectories(
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(acadVersionKey) || string.IsNullOrWhiteSpace(acadProductKey))
            yield break;

        var macros = BuildMacroValues(acadVersionKey, acadProductKey);
        var rawPath = @"%RoamableRootFolder%\plotters\plot styles";
        var any = false;
        foreach (var resolved in ResolvePathExpression(rawPath, acadVersionKey, acadProductKey, macros))
        {
            any = true;
            yield return resolved;
        }

        if (!any)
            log?.Invoke("未能解析 AutoCAD RoamableRootFolder，无法定位默认打印样式目录。");
    }

    private static bool ShouldUseVersion(string versionName, string? acadVersionKey)
    {
        if (versionName.Length < 2 || versionName[0] != 'R')
            return false;

        return string.IsNullOrWhiteSpace(acadVersionKey) ||
               string.Equals(versionName, acadVersionKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseProduct(string productName, string? acadProductKey)
    {
        if (!productName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(acadProductKey) ||
               string.Equals(productName, acadProductKey, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildMacroValues(string versionName, string productName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfDirectory(values, "TempFolder", Path.GetTempPath());
        AddProductRegistryValues(values, RegistryHive.CurrentUser, RegistryView.Default, versionName, productName);
        AddProductRegistryValues(values, RegistryHive.LocalMachine, RegistryView.Registry64, versionName, productName);
        AddProductRegistryValues(values, RegistryHive.LocalMachine, RegistryView.Registry32, versionName, productName);

        if (!values.ContainsKey("InstallFolder") &&
            AcadLauncher.TryGetSpecificInstallDirectory(versionName, productName, out var installDir) &&
            !string.IsNullOrWhiteSpace(installDir))
        {
            AddIfDirectory(values, "InstallFolder", installDir);
        }

        if (!values.ContainsKey("RoamableRootFolder") &&
            TryBuildDefaultRoamableRootFolder(versionName, productName, out var defaultRoamableRoot))
        {
            values["RoamableRootFolder"] = defaultRoamableRoot;
        }

        return values;
    }

    private static void AddProductRegistryValues(
        Dictionary<string, string> values,
        RegistryHive hive,
        RegistryView view,
        string versionName,
        string productName)
    {
        try
        {
            if (hive == RegistryHive.CurrentUser && view == RegistryView.Default)
            {
                using var product = Registry.CurrentUser.OpenSubKey(
                    $@"{CadRegistryRoot}\{versionName}\{productName}",
                    writable: false);
                AddKeyValues(values, product);
                using var installer = product?.OpenSubKey("Installer", writable: false);
                AddKeyValues(values, installer);
                return;
            }

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var productFromView = baseKey.OpenSubKey(
                $@"{CadRegistryRoot}\{versionName}\{productName}",
                writable: false);
            AddKeyValues(values, productFromView);
            using var installerFromView = productFromView?.OpenSubKey("Installer", writable: false);
            AddKeyValues(values, installerFromView);
        }
        catch
        {
            // Ignore missing registry hives/views.
        }
    }

    private static void AddKeyValues(Dictionary<string, string> values, RegistryKey? key)
    {
        if (key == null)
            return;

        foreach (var valueName in new[] { "RoamableRootFolder", "LocalRootFolder", "InstallFolder", "AllUserFolder" })
        {
            if (values.ContainsKey(valueName))
                continue;

            var value = key.GetValue(valueName) as string;
            AddIfDirectory(values, valueName, value);
        }
    }

    private static void AddIfDirectory(Dictionary<string, string> values, string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
            return;

        values[name] = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<string> ResolvePathExpression(
        string? rawPath,
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        var raw = string.IsNullOrWhiteSpace(rawPath)
            ? @"%RoamableRootFolder%\plotters\plot styles"
            : rawPath.Trim().Trim('"');

        foreach (var candidate in ExpandRoamableRootCandidates(raw, versionName, productName, macros))
        {
            var expanded = ReplaceKnownMacros(candidate, macros);
            expanded = Environment.ExpandEnvironmentVariables(expanded);
            if (HasUnresolvedMacro(expanded))
                continue;

            if (TryNormalizePath(expanded, out var fullPath))
                yield return fullPath;
        }
    }

    private static IEnumerable<string> ExpandRoamableRootCandidates(
        string rawPath,
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        if (!ContainsMacro(rawPath, "RoamableRootFolder"))
        {
            yield return rawPath;
            yield break;
        }

        foreach (var root in EnumerateRoamableRootCandidates(versionName, productName, macros))
            yield return ReplaceMacro(rawPath, "RoamableRootFolder", root);
    }

    private static IEnumerable<string> EnumerateRoamableRootCandidates(
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (macros.TryGetValue("RoamableRootFolder", out var fromRegistry) && seen.Add(fromRegistry))
            yield return fromRegistry;

        var appDataAutodesk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk");
        if (Directory.Exists(appDataAutodesk))
        {
            IEnumerable<string> versionDirs;
            try
            {
                versionDirs = Directory.EnumerateDirectories(appDataAutodesk, versionName, SearchOption.AllDirectories).ToList();
            }
            catch
            {
                versionDirs = Array.Empty<string>();
            }

            foreach (var versionDir in versionDirs)
            {
                if (seen.Add(versionDir))
                    yield return versionDir;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(versionDir);
                }
                catch
                {
                    children = Array.Empty<string>();
                }

                foreach (var child in children)
                {
                    if (seen.Add(child))
                        yield return child;
                }
            }
        }

        if (TryBuildDefaultRoamableRootFolder(versionName, productName, out var fallback) && seen.Add(fallback))
            yield return fallback;
    }

    private static string ReplaceKnownMacros(string value, IReadOnlyDictionary<string, string> macros)
    {
        return Regex.Replace(value, "%([^%]+)%", match =>
        {
            var key = match.Groups[1].Value;
            return macros.TryGetValue(key, out var replacement) ? replacement : match.Value;
        });
    }

    private static bool ContainsMacro(string value, string macroName)
    {
        return value.IndexOf("%" + macroName + "%", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ReplaceMacro(string value, string macroName, string replacement)
    {
        return Regex.Replace(
            value,
            "%" + Regex.Escape(macroName) + "%",
            _ => replacement,
            RegexOptions.IgnoreCase);
    }

    private static bool HasUnresolvedMacro(string value)
    {
        return Regex.IsMatch(value, "%[^%]+%");
    }

    private static bool TryNormalizePath(string path, out string fullPath)
    {
        fullPath = "";
        try
        {
            fullPath = Path.GetFullPath(path);
            return !string.IsNullOrWhiteSpace(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildDefaultRoamableRootFolder(
        string versionName,
        string productName,
        out string folder)
    {
        folder = "";
        if (!TryResolveAutoCadYear(versionName, out var year))
            return false;

        var locale = ResolveLocaleFolder(productName);
        if (locale.Length == 0)
            return false;

        folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk",
            "AutoCAD " + year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            versionName,
            locale);
        return true;
    }

    private static bool TryResolveAutoCadYear(string versionName, out int year)
    {
        year = 0;
        var match = Regex.Match(versionName, @"^R(?<major>\d+)(?:\.(?<minor>\d+))?$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        var major = int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = match.Groups["minor"].Success
            ? int.Parse(match.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

        year = major == 24 ? 2021 + minor : 2000 + major;
        return year > 0;
    }

    private static string ResolveLocaleFolder(string productName)
    {
        var colonIndex = productName.LastIndexOf(':');
        if (colonIndex < 0 || colonIndex == productName.Length - 1)
            return "enu";

        return productName[(colonIndex + 1)..] switch
        {
            "804" => "chs",
            "409" => "enu",
            _ => "enu"
        };
    }
}
