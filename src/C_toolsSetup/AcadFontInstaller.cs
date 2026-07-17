using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace C_toolsSetup;

/// <summary>安装初始化文件夹中的 SHX 字体到 AutoCAD 可搜索的字体/Support 目录。</summary>
internal static class AcadFontInstaller
{
    private const string CadRegistryRoot = @"Software\Autodesk\AutoCAD";
    private const string SupportSearchPathValueName = "ACAD";
    private static readonly IReadOnlyDictionary<string, string[]> s_fontAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["hztxt.shx"] = new[] { "HZDX.shx" }
        };

    internal static int InstallFontsToProfiles(
        IReadOnlyList<string> sourceFontPaths,
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        var sources = ResolveSourceFontPaths(sourceFontPaths);

        if (sources.Count == 0)
        {
            log?.Invoke("未找到可安装的 CAD SHX 字体，跳过复制到 CAD 字体目录。");
            return 0;
        }

        var targetDirs = ResolveTargetFontDirectories(acadVersionKey, acadProductKey, log)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetDirs.Count == 0)
        {
            log?.Invoke("未找到 CAD 字体/Support 目录，未复制 SHX 字体。");
            return 0;
        }

        var copied = 0;
        foreach (var targetDir in targetDirs)
        {
            foreach (var source in sources)
            {
                var fileName = Path.GetFileName(source);
                try
                {
                    Directory.CreateDirectory(targetDir);
                    var dest = Path.Combine(targetDir, fileName);
                    File.Copy(source, dest, overwrite: true);
                    copied++;
                    log?.Invoke($"已复制 CAD 字体：{fileName} -> {targetDir}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    log?.Invoke($"复制 CAD 字体失败：{fileName} -> {targetDir} — {ex.Message}");
                }
            }
        }

        return copied;
    }

    internal static int CopyFontsToDirectory(
        IReadOnlyList<string> sourceFontPaths,
        string targetDir,
        Action<string>? log)
    {
        var sources = ResolveSourceFontPaths(sourceFontPaths);
        if (sources.Count == 0)
            return 0;

        if (string.IsNullOrWhiteSpace(targetDir))
            return 0;

        var copied = 0;
        foreach (var source in sources)
        {
            var fileName = Path.GetFileName(source);
            try
            {
                Directory.CreateDirectory(targetDir);
                var dest = Path.Combine(targetDir, fileName);
                File.Copy(source, dest, overwrite: true);
                copied++;
                log?.Invoke($"已复制 CAD 字体到 Plugin：{fileName} -> {targetDir}");
                copied += CopyFontAliasesToDirectory(source, targetDir, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                log?.Invoke($"复制 CAD 字体到 Plugin 失败：{fileName} -> {targetDir} — {ex.Message}");
            }
        }

        return copied;
    }

    private static int CopyFontAliasesToDirectory(string sourceFontPath, string targetDir, Action<string>? log)
    {
        var sourceName = Path.GetFileName(sourceFontPath);
        if (!s_fontAliases.TryGetValue(sourceName, out var aliases) || aliases.Length == 0)
            return 0;

        var copied = 0;
        foreach (var alias in aliases)
        {
            try
            {
                var dest = Path.Combine(targetDir, alias);
                File.Copy(sourceFontPath, dest, overwrite: true);
                copied++;
                log?.Invoke($"已复制 CAD 字体别名到 Plugin：{alias} -> {targetDir}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                log?.Invoke($"复制 CAD 字体别名到 Plugin 失败：{alias} -> {targetDir} — {ex.Message}");
            }
        }

        return copied;
    }

    internal static int AppendSupportSearchPathToProfiles(
        string directoryToAdd,
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(directoryToAdd))
            return 0;

        var fullPath = Path.GetFullPath(directoryToAdd)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullPath))
        {
            log?.Invoke($"CAD 支持搜索路径不存在，未写入 ACAD：{fullPath}");
            return 0;
        }

        using var cadRoot = Registry.CurrentUser.OpenSubKey(CadRegistryRoot, writable: true);
        if (cadRoot == null)
        {
            log?.Invoke("未找到注册表：HKCU\\Software\\Autodesk\\AutoCAD（可能未安装 AutoCAD）。");
            return 0;
        }

        var updated = 0;
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

                var profilesPath = $"{versionName}\\{productName}\\Profiles";
                using var profiles = cadRoot.OpenSubKey(profilesPath, writable: true);
                if (profiles == null || profiles.GetSubKeyNames().Length == 0)
                {
                    updated += TryCreateDefaultProfileAndMergeSupportPath(
                        cadRoot,
                        profilesPath,
                        fullPath,
                        versionName,
                        productName,
                        log);
                    continue;
                }

                foreach (var profileName in profiles.GetSubKeyNames())
                {
                    using var profileKey = profiles.OpenSubKey(profileName, writable: true);
                    if (profileKey == null)
                        continue;

                    using var general = profileKey.OpenSubKey("General", writable: true)
                                        ?? profileKey.CreateSubKey("General", writable: true);
                    if (general == null)
                        continue;

                    updated += TryMergeSupportPath(
                        general,
                        fullPath,
                        $"{versionName}\\{productName}\\Profiles\\{profileName}",
                        log);
                }
            }
        }

        if (updated == 0)
            log?.Invoke("未写入任何 CAD 支持搜索路径 (ACAD)（可能路径已存在或未找到目标 AutoCAD 配置）。");

        return updated;
    }

    internal static bool IsCadFontFile(string fileName) =>
        fileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

    private static List<string> ResolveSourceFontPaths(IReadOnlyList<string> sourceFontPaths)
    {
        return sourceFontPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           IsCadFontFile(Path.GetFileName(path)) &&
                           File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int TryCreateDefaultProfileAndMergeSupportPath(
        RegistryKey cadRoot,
        string profilesPath,
        string fullPath,
        string versionName,
        string productName,
        Action<string>? log)
    {
        try
        {
            var rel = $"{profilesPath}\\<<Unnamed Profile>>\\General";
            using var general = cadRoot.CreateSubKey(rel, writable: true);
            return general == null
                ? 0
                : TryMergeSupportPath(general, fullPath, $"{versionName}\\{productName}\\Profiles\\<<Unnamed Profile>>", log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"创建默认 Profile 并写入 CAD 支持搜索路径失败：{versionName}\\{productName} — {ex.Message}");
            return 0;
        }
    }

    private static int TryMergeSupportPath(
        RegistryKey general,
        string fullPath,
        string profileDisplayPath,
        Action<string>? log)
    {
        try
        {
            var existing = general.GetValue(SupportSearchPathValueName) as string ?? "";
            var merged = MergePathList(existing, fullPath);
            if (string.Equals(existing, merged, StringComparison.Ordinal))
                return 0;

            general.SetValue(SupportSearchPathValueName, merged, GetWritableStringValueKind(general, SupportSearchPathValueName));
            log?.Invoke($"已合并 CAD 支持搜索路径 (ACAD)：{profileDisplayPath}");
            return 1;
        }
        catch (Exception ex)
        {
            log?.Invoke($"跳过 CAD 支持搜索路径写入（无权限或只读）：{profileDisplayPath} — {ex.Message}");
            return 0;
        }
    }

    private static string MergePathList(string existing, string toAdd)
    {
        var list = new List<string>();

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var value = path.Trim();
            if (value.Length == 0)
                return;

            if (list.Exists(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
                return;

            list.Add(value);
        }

        foreach (var path in existing.Split(';', StringSplitOptions.RemoveEmptyEntries))
            TryAdd(path);
        TryAdd(toAdd);

        return string.Join(";", list);
    }

    private static RegistryValueKind GetWritableStringValueKind(RegistryKey key, string valueName)
    {
        try
        {
            var kind = key.GetValueKind(valueName);
            return kind is RegistryValueKind.String or RegistryValueKind.ExpandString
                ? kind
                : RegistryValueKind.String;
        }
        catch (ArgumentException)
        {
            return RegistryValueKind.String;
        }
        catch (IOException)
        {
            return RegistryValueKind.String;
        }
    }

    private static IEnumerable<string> ResolveTargetFontDirectories(
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        var foundTarget = false;
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
                    foreach (var target in ResolveProfileFontDirectories(cadRoot, versionName, productName, macros))
                    {
                        foundTarget = true;
                        yield return target;
                    }

                    foreach (var fallback in ResolveFallbackFontDirectories(versionName, productName, macros))
                    {
                        foundTarget = true;
                        yield return fallback;
                    }
                }
            }
        }

        if (foundTarget)
            yield break;

        foreach (var fallback in ResolveFallbackFontDirectories(acadVersionKey, acadProductKey, log))
            yield return fallback;
    }

    private static IEnumerable<string> ResolveProfileFontDirectories(
        RegistryKey cadRoot,
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        var profilesPath = $"{versionName}\\{productName}\\Profiles";
        using var profiles = cadRoot.OpenSubKey(profilesPath, writable: false);
        if (profiles == null)
            yield break;

        foreach (var profileName in profiles.GetSubKeyNames())
        {
            using var general = profiles.OpenSubKey(profileName + "\\General", writable: false);
            var rawPath = general?.GetValue(SupportSearchPathValueName) as string;
            foreach (var resolved in ResolvePathListExpression(rawPath, versionName, productName, macros))
            {
                if (IsFontTargetDirectory(resolved, macros))
                    yield return resolved;
            }
        }
    }

    private static IEnumerable<string> ResolveFallbackFontDirectories(
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(acadVersionKey) || string.IsNullOrWhiteSpace(acadProductKey))
            yield break;

        var macros = BuildMacroValues(acadVersionKey, acadProductKey);
        var any = false;
        foreach (var resolved in ResolveFallbackFontDirectories(acadVersionKey, acadProductKey, macros))
        {
            any = true;
            yield return resolved;
        }

        if (!any)
            log?.Invoke("未能解析 AutoCAD RoamableRootFolder/InstallFolder，无法定位默认字体目录。");
    }

    private static IEnumerable<string> ResolveFallbackFontDirectories(
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        foreach (var resolved in ResolvePathExpression(@"%RoamableRootFolder%\support", versionName, productName, macros))
            yield return resolved;

        foreach (var resolved in ResolvePathExpression(@"%InstallFolder%\fonts", versionName, productName, macros))
            yield return resolved;
    }

    private static bool IsFontTargetDirectory(string path, IReadOnlyDictionary<string, string> macros)
    {
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (leaf.Equals("fonts", StringComparison.OrdinalIgnoreCase))
            return true;

        return leaf.Equals("support", StringComparison.OrdinalIgnoreCase) && IsUnderMacroPath(path, macros, "RoamableRootFolder");
    }

    private static bool IsUnderMacroPath(string path, IReadOnlyDictionary<string, string> macros, string macroName)
    {
        if (!macros.TryGetValue(macroName, out var root) || string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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

    private static IEnumerable<string> ResolvePathListExpression(
        string? rawPathList,
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        if (string.IsNullOrWhiteSpace(rawPathList))
            yield break;

        foreach (var rawPath in rawPathList.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var resolved in ResolvePathExpression(rawPath, versionName, productName, macros))
                yield return resolved;
        }
    }

    private static IEnumerable<string> ResolvePathExpression(
        string? rawPath,
        string versionName,
        string productName,
        IReadOnlyDictionary<string, string> macros)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            yield break;

        var raw = rawPath.Trim().Trim('"');
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
