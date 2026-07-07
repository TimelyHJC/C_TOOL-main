using Microsoft.Win32;

namespace C_toolsSetup;

/// <summary>
/// 在安装目录保留 *.bundle 的前提下，通过 AutoCAD 的 Applications 注册表项在启动时 NETLOAD 托管 DLL（不再依赖 ApplicationPlugins 副本）。
/// </summary>
internal static class AcadPluginStartupRegistry
{
    private const string AppSubKeyPrefix = "C_TOOL_Bundle_";

    /// <summary>LOADCTRLS：随 AutoCAD 启动加载（与 ARX 文档 0x02 一致）。</summary>
    private const int LoadOnStartup = 2;

    /// <summary>
    /// 在指定 AutoCAD 版本下注册各 bundle 的 Win64 DLL，并清理另一注册表根下同名项，避免 HKCU+HKLM 重复加载。
    /// </summary>
    /// <returns>成功写入的 Applications 子项数量（每个 bundle × 每个目标产品）。</returns>
    internal static int RegisterBundlesAndCleanupOppositeHive(
        string pluginDir,
        IReadOnlyList<string> bundleDirectoryNames,
        string? acadVersionKey,
        string? acadProductKey,
        bool useLocalMachineHive,
        Action<string>? log)
    {
        if (bundleDirectoryNames.Count == 0)
            return 0;

        var loaderEntries = new List<(string KeyName, string DllPath, AcadBundleReleaseBand Band)>();
        var seenRegistryKeyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundleName in bundleDirectoryNames)
        {
            var root = Path.Combine(pluginDir, bundleName);
            var startupModules = AcadBundleLayout.GetStartupModules(root);
            if (startupModules.Count == 0)
            {
                log?.Invoke($"警告：未在 {bundleName} 的 PackageContents.xml / Contents\\Win64 中找到可用启动 DLL，已跳过该包的启动注册。");
                continue;
            }

            var shortName = bundleName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                ? bundleName[..^".bundle".Length]
                : bundleName;
            var band = AcadReleaseTargeting.ClassifyBundle(bundleName, Path.GetFileName(startupModules[0].ModulePath));
            foreach (var module in startupModules)
            {
                var regKeyName = BuildUniqueBundleRegistryKeyName(shortName, module, seenRegistryKeyNames);
                loaderEntries.Add((regKeyName, module.ModulePath, band));
            }
        }

        if (loaderEntries.Count == 0)
            return 0;

        var targets = EnumerateAcadProductTargets(acadVersionKey, acadProductKey, log);
        if (targets.Count == 0)
        {
            log?.Invoke(
                "未在注册表中找到可写入的 AutoCAD 产品项（HKCU/HKLM 下 Software\\Autodesk\\AutoCAD\\R*\\ACAD-*）。请确认已安装 AutoCAD；若已安装，可先启动一次该版本再运行安装程序。");
            return 0;
        }

        var primaryHive = useLocalMachineHive ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var oppositeHive = useLocalMachineHive ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

        var written = 0;
        foreach (var (versionKey, productKey) in targets)
        {
            var acadMajor = AcadReleaseTargeting.TryParseReleaseMajor(versionKey, out var mj) ? mj : -1;
            var forThisRelease = new List<(string RegKeyName, string DllPath)>();
            foreach (var (keyName, dllPath, band) in loaderEntries)
            {
                if (AcadReleaseTargeting.IsBandApplicableToRelease(band, acadMajor))
                    forThisRelease.Add((keyName, dllPath));
            }

            RemoveOurBundleKeysFromHive(primaryHive, versionKey, productKey, log);
            RemoveOurBundleKeysFromHive(oppositeHive, versionKey, productKey, log);
            if (forThisRelease.Count == 0)
            {
                log?.Invoke($"跳过 Applications 写入：{versionKey}\\{productKey}（当前只支持 AutoCAD 2024 / R24）。");
                continue;
            }

            written += WriteBundleKeysToHive(primaryHive, versionKey, productKey, forThisRelease, log);
        }

        return written;
    }

    private static string BuildUniqueBundleRegistryKeyName(
        string bundleShortName,
        AcadBundleLayout.StartupModule module,
        ISet<string> seenRegistryKeyNames)
    {
        static string NormalizeKeyName(string bundleName, string componentName)
            => AppSubKeyPrefix + SanitizeRegistryKeyFragment(bundleName + "_" + componentName);

        var candidates = new[]
        {
            NormalizeKeyName(bundleShortName, module.RegistrationName),
            NormalizeKeyName(bundleShortName, Path.GetFileNameWithoutExtension(module.ModulePath) ?? module.RegistrationName),
            NormalizeKeyName(bundleShortName,
                module.RegistrationName + "_" + (Path.GetFileNameWithoutExtension(module.ModulePath) ?? "Dll"))
        };

        foreach (var candidate in candidates)
        {
            if (seenRegistryKeyNames.Add(candidate))
                return candidate;
        }

        var suffix = 2;
        while (true)
        {
            var fallback = NormalizeKeyName(bundleShortName, module.RegistrationName + "_" + suffix);
            if (seenRegistryKeyNames.Add(fallback))
                return fallback;
            suffix++;
        }
    }

    private static string SanitizeRegistryKeyFragment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\\' || chars[i] == '/')
            {
                chars[i] = '_';
                continue;
            }

            foreach (var c in invalid)
            {
                if (chars[i] == c)
                {
                    chars[i] = '_';
                    break;
                }
            }
        }

        var s = new string(chars).Trim();
        return s.Length > 0 ? s : "Plugin";
    }

    private static List<(string VersionKey, string ProductKey)> EnumerateAcadProductTargets(
        string? filterVersion,
        string? filterProduct,
        Action<string>? log)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();

        void ScanHive(RegistryHive hive)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var cadRoot = baseKey.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
                    if (cadRoot == null)
                        continue;

                    foreach (var versionName in cadRoot.GetSubKeyNames())
                    {
                        if (versionName.Length < 2 || versionName[0] != 'R')
                            continue;
                        if (filterVersion != null &&
                            !string.Equals(versionName, filterVersion, StringComparison.OrdinalIgnoreCase))
                            continue;

                        using var verKey = cadRoot.OpenSubKey(versionName);
                        if (verKey == null)
                            continue;

                        foreach (var productName in verKey.GetSubKeyNames())
                        {
                            if (!productName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (filterProduct != null &&
                                !string.Equals(productName, filterProduct, StringComparison.OrdinalIgnoreCase))
                                continue;

                            using var prodKey = verKey.OpenSubKey(productName);
                            if (prodKey == null)
                                continue;

                            // 不要求已存在 Profiles：否则「仅安装 2024 包、尚未开过 CAD」时无法写入 Applications，启动永不加载。
                            var k = versionName + "|" + productName;
                            if (!seen.Add(k))
                                continue;
                            list.Add((versionName, productName));
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"枚举 AutoCAD 注册表时跳过（{hive} / {view}）：{ex.Message}");
                }
            }
        }

        ScanHive(RegistryHive.CurrentUser);
        ScanHive(RegistryHive.LocalMachine);
        return list;
    }

    private static void RemoveOurBundleKeysFromHive(
        RegistryHive hive,
        string versionKey,
        string productKey,
        Action<string>? log)
    {
        var productRel = $@"SOFTWARE\Autodesk\AutoCAD\{versionKey}\{productKey}";
        var relApps = productRel + @"\Applications";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var prod = baseKey.OpenSubKey(productRel);
                if (prod == null)
                    continue;

                using var apps = baseKey.OpenSubKey(relApps, writable: true);
                if (apps == null)
                    continue;

                foreach (var sub in apps.GetSubKeyNames())
                {
                    if (!sub.StartsWith(AppSubKeyPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        apps.DeleteSubKeyTree(sub);
                        log?.Invoke($"已移除旧启动项：{hive} {versionKey}\\{productKey}\\Applications\\{sub}");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"警告：无法删除 {sub}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"清理 Applications 时跳过（{hive}）：{ex.Message}");
            }
        }
    }

    private static int WriteBundleKeysToHive(
        RegistryHive hive,
        string versionKey,
        string productKey,
        IReadOnlyList<(string RegKeyName, string DllPath)> entries,
        Action<string>? log)
    {
        var productRel = $@"SOFTWARE\Autodesk\AutoCAD\{versionKey}\{productKey}";
        var relApps = productRel + @"\Applications";
        var count = 0;

        RegistryView? chosenView = null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var prod = baseKey.OpenSubKey(productRel);
                if (prod != null)
                {
                    chosenView = view;
                    break;
                }
            }
            catch
            {
                // 尝试下一视图
            }
        }

        if (chosenView == null)
        {
            log?.Invoke($"跳过 {hive} {versionKey}\\{productKey}：未找到产品注册表项（请确认已安装对应 AutoCAD）。");
            return 0;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, chosenView.Value);
            using var apps = baseKey.CreateSubKey(relApps, writable: true);
            if (apps == null)
                return 0;

            foreach (var (regKeyName, dllPath) in entries)
            {
                try
                {
                    using var k = apps.CreateSubKey(regKeyName, writable: true);
                    if (k == null)
                        continue;
                    k.SetValue("DESCRIPTION", "C_TOOL 插件（安装目录加载）", RegistryValueKind.String);
                    k.SetValue("LOADER", dllPath, RegistryValueKind.String);
                    k.SetValue("LOADCTRLS", LoadOnStartup, RegistryValueKind.DWord);
                    k.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                    count++;
                    log?.Invoke($"已写入启动加载：{hive} {versionKey}\\{productKey}\\Applications\\{regKeyName} → {dllPath}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    log?.Invoke($"无权限写入 {hive} …\\Applications\\{regKeyName}：{ex.Message}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"写入 Applications\\{regKeyName} 失败：{ex.Message}");
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Invoke($"无法写入 {hive}\\{relApps}：{ex.Message}；若为「所有用户」请右键以管理员运行。");
        }
        catch (Exception ex)
        {
            log?.Invoke($"打开 {hive}\\{relApps} 失败：{ex.Message}");
        }

        return count;
    }
}
