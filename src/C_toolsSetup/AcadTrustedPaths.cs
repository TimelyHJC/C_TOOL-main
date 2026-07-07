using Microsoft.Win32;

namespace C_toolsSetup;

/// <summary>
/// 在 AutoCAD 各版本、各配置下合并 <see cref="TRUSTEDPATHS"/>（系统变量，对应 **选项 → 文件 → 受信任的位置**）。
/// </summary>
internal static class AcadTrustedPaths
{
    private const string ValueName = "TRUSTEDPATHS";

    /// <summary>与英文/多语言 AutoCAD 首次启动生成的默认配置名一致，用于「从未开过 CAD」时尚无 Profiles\...\Variables 的情况。</summary>
    private const string DefaultProfileFolderName = "<<Unnamed Profile>>";

    /// <summary>
    /// 将若干目录加入 TRUSTEDPATHS（带 <c>\...</c> 以包含子文件夹，与 AutoCAD 帮助一致）。
    /// 仅修改当前用户 (HKCU) 注册表。
    /// </summary>
    /// <returns>已写入的 Variables 项数量。</returns>
    internal static int AppendToAllProfiles(IReadOnlyList<string> directoriesToTrust, Action<string>? log) =>
        AppendToProfiles(
            directoriesToTrust,
            Array.Empty<(string BundleRootPath, AcadBundleReleaseBand Band)>(),
            pluginRootForTrustCleanup: null,
            acadVersionKey: null,
            acadProductKey: null,
            log);

    /// <param name="commonDirectories">各版本共用的受信任目录（如 User、C_TOOL 数据根）。</param>
    /// <param name="bundleTrustRoots">按 CAD 代际区分的 bundle 根目录；仅在注册表主版本与该条目的 Band 匹配时合并。</param>
    /// <param name="pluginRootForTrustCleanup">本次安装目录下的 Plugin 文件夹；用于从 TRUSTEDPATHS 中移除历史写入的「整块 Plugin\...」及不适配当前 CAD 主版本的 bundle 路径。</param>
    /// <param name="acadVersionKey">注册表中的版本段，如 R24.2；为 null 则处理全部。</param>
    /// <param name="acadProductKey">如 ACAD-6100:804；为 null 则处理全部。</param>
    internal static int AppendToProfiles(
        IReadOnlyList<string> commonDirectories,
        IReadOnlyList<(string BundleRootPath, AcadBundleReleaseBand Band)> bundleTrustRoots,
        string? pluginRootForTrustCleanup,
        string? acadVersionKey,
        string? acadProductKey,
        Action<string>? log)
    {
        if (commonDirectories.Count == 0 && bundleTrustRoots.Count == 0)
            return 0;

        using var cadRoot = Registry.CurrentUser.OpenSubKey(@"Software\Autodesk\AutoCAD", writable: true);
        if (cadRoot == null)
        {
            log?.Invoke("未找到注册表：HKCU\\Software\\Autodesk\\AutoCAD（可能未安装 AutoCAD）。");
            return 0;
        }

        var updated = 0;
        foreach (var versionName in cadRoot.GetSubKeyNames())
        {
            if (versionName.Length < 2 || versionName[0] != 'R')
                continue;

            if (acadVersionKey != null &&
                !string.Equals(versionName, acadVersionKey, StringComparison.OrdinalIgnoreCase))
                continue;

            using var verKey = cadRoot.OpenSubKey(versionName);
            if (verKey == null)
                continue;

            var acadMajor = AcadReleaseTargeting.TryParseReleaseMajor(versionName, out var mj) ? mj : -1;
            var dottedPaths = BuildDottedTrustPaths(commonDirectories, bundleTrustRoots, acadMajor);
            var canCleanupStaleTrust = !string.IsNullOrWhiteSpace(pluginRootForTrustCleanup);
            if (dottedPaths.Count == 0 && !canCleanupStaleTrust)
                continue;

            foreach (var productName in verKey.GetSubKeyNames())
            {
                if (!productName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (acadProductKey != null &&
                    !string.Equals(productName, acadProductKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var profilesPath = $"{versionName}\\{productName}\\Profiles";
                using var profiles = cadRoot.OpenSubKey(profilesPath, writable: true);
                if (profiles == null)
                {
                    // 尚未运行过 CAD 时通常没有 Profiles，无法合并进 Variables；创建默认配置并写入受信任位置。
                    if (dottedPaths.Count > 0 || canCleanupStaleTrust)
                    {
                        updated += TryCreateDefaultProfileAndMergeTrustedPaths(
                            cadRoot,
                            profilesPath,
                            dottedPaths,
                            acadMajor,
                            pluginRootForTrustCleanup,
                            versionName,
                            productName,
                            log);
                    }

                    continue;
                }

                var profileNames = profiles.GetSubKeyNames();
                if (profileNames.Length == 0)
                {
                    if (dottedPaths.Count > 0 || canCleanupStaleTrust)
                    {
                        updated += TryCreateDefaultProfileAndMergeTrustedPaths(
                            cadRoot,
                            profilesPath,
                            dottedPaths,
                            acadMajor,
                            pluginRootForTrustCleanup,
                            versionName,
                            productName,
                            log);
                    }

                    continue;
                }

                foreach (var profileName in profileNames)
                {
                    using var profileKey = profiles.OpenSubKey(profileName, writable: true);
                    if (profileKey == null)
                        continue;

                    using var vars = profileKey.OpenSubKey("Variables", writable: true)
                                       ?? profileKey.CreateSubKey("Variables", writable: true);
                    if (vars == null)
                        continue;

                    try
                    {
                        var existing = vars.GetValue(ValueName) as string ?? "";
                        var stripped = StripStalePluginTrustPaths(existing, acadMajor, pluginRootForTrustCleanup);
                        var merged = MergePaths(stripped, dottedPaths);
                        if (string.Equals(merged, existing, StringComparison.Ordinal))
                            continue;

                        var kind = GetWritableStringValueKind(vars, ValueName);

                        vars.SetValue(ValueName, merged, kind);
                        updated++;
                        log?.Invoke(
                            $"已合并受信任位置 (TRUSTEDPATHS)：{versionName}\\{productName}\\Profiles\\{profileName}");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"跳过（无权限或只读）：{profilesPath}\\{profileName}\\Variables — {ex.Message}");
                    }
                }
            }
        }

        if (updated == 0)
            log?.Invoke("未写入任何受信任位置 (TRUSTEDPATHS)（可能未安装 AutoCAD、路径已全部存在，或无任何可合并目录）。");

        return updated;
    }

    private static string MergePaths(string existing, IReadOnlyList<string> toAdd)
    {
        var list = new List<string>();

        void TryAdd(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return;
            var t = s.Trim();
            if (t.Length == 0)
                return;
            if (list.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(t);
        }

        foreach (var p in existing.Split(';', StringSplitOptions.RemoveEmptyEntries))
            TryAdd(p);
        foreach (var a in toAdd)
            TryAdd(a);

        return string.Join(";", list);
    }

    private static List<string> BuildDottedTrustPaths(
        IReadOnlyList<string> commonDirectories,
        IReadOnlyList<(string BundleRootPath, AcadBundleReleaseBand Band)> bundleTrustRoots,
        int acadReleaseMajor)
    {
        var toAdd = new List<string>();

        void AddIfExists(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 0 || !Directory.Exists(full))
                return;
            toAdd.Add(full + "\\...");
        }

        foreach (var d in commonDirectories)
            AddIfExists(d);

        foreach (var (bundleRoot, band) in bundleTrustRoots)
        {
            if (!AcadReleaseTargeting.IsBandApplicableToRelease(band, acadReleaseMajor))
                continue;
            AddIfExists(bundleRoot);
        }

        return toAdd;
    }

    /// <summary>
    /// 按「本次安装 Plugin 根目录」解析 TRUSTEDPATHS 每一段：保留整块 Plugin 信任项；移除其中与当前 CAD 主版本不匹配的 *.bundle 子路径
    /// （磁盘上若已删掉某 bundle，仅靠 bundle 列表无法生成删除键，故必须用路径前缀 + bundle 名分类）。
    /// </summary>
    private static string StripStalePluginTrustPaths(
        string existing,
        int acadReleaseMajor,
        string? pluginRootForCleanup)
    {
        if (string.IsNullOrWhiteSpace(pluginRootForCleanup))
            return existing;

        string pluginFull;
        try
        {
            pluginFull = Path.GetFullPath(pluginRootForCleanup).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return existing;
        }

        if (pluginFull.Length == 0)
            return existing;

        var parts = existing.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var raw in parts)
        {
            var t = raw.Trim();
            if (t.Length == 0)
                continue;
            var norm = NormalizeTrustPathToken(t);
            if (ShouldRemoveTrustPathSegmentUnderPlugin(norm, pluginFull, acadReleaseMajor))
                continue;
            kept.Add(t);
        }

        return string.Join(";", kept);
    }

    /// <summary>TRUSTEDPATHS 中带 <c>\...</c> 的项在比较时按「目录本体」处理。</summary>
    private static string ToLogicalDirectoryForTrustCompare(string normalizedSegment)
    {
        var s = normalizedSegment.TrimEnd('\\');
        if (s.EndsWith(@"\...", StringComparison.Ordinal))
            return s[..^3].TrimEnd('\\');
        return s;
    }

    /// <returns>若该段指向本次安装 Plugin 下内容且与 <paramref name="acadReleaseMajor"/> 不匹配则 true（应删除）。</returns>
    private static bool ShouldRemoveTrustPathSegmentUnderPlugin(
        string segmentNormalized,
        string pluginFullNormalized,
        int acadReleaseMajor)
    {
        var logical = ToLogicalDirectoryForTrustCompare(segmentNormalized);
        if (logical.Length == 0)
            return false;

        // 保留「整块 Plugin\...」信任项：否则仅信任 *.bundle 根目录时，部分环境下仍会对 Contents\Win64 内依赖 DLL 逐个提示安全加载。
        if (string.Equals(logical, pluginFullNormalized, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = pluginFullNormalized + "\\";
        if (!logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rel = logical.Substring(prefix.Length);
        foreach (var piece in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!piece.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                continue;

            var bundlePath = Path.Combine(pluginFullNormalized, piece);
            var dllName = Path.GetFileName(AcadBundleLayout.TryFindPrimaryDll(bundlePath) ?? "");
            var band = AcadReleaseTargeting.ClassifyBundle(piece, string.IsNullOrEmpty(dllName) ? null : dllName);
            return !AcadReleaseTargeting.IsBandApplicableToRelease(band, acadReleaseMajor);
        }

        // 在 Plugin 下但未出现 *.bundle 子目录名（如旧版曾信任的子文件夹）
        return true;
    }

    private static string NormalizeTrustPathToken(string segment) =>
        segment.Trim().Replace('/', '\\');

    /// <summary>
    /// 在 <c>Profiles</c> 不存在或为空时创建 <see cref="DefaultProfileFolderName"/> 下的 <c>Variables</c>，
    /// 使「选项 → 文件 → 受信任的位置」在安装后即可出现路径（无需先手动启动过一次 CAD）。
    /// </summary>
    private static int TryCreateDefaultProfileAndMergeTrustedPaths(
        RegistryKey cadRoot,
        string profilesPath,
        IReadOnlyList<string> dottedPaths,
        int acadMajor,
        string? pluginRootForTrustCleanup,
        string versionName,
        string productName,
        Action<string>? log)
    {
        try
        {
            var rel = $"{profilesPath}\\{DefaultProfileFolderName}\\Variables";
            using var vars = cadRoot.CreateSubKey(rel, writable: true);
            if (vars == null)
                return 0;

            var existing = vars.GetValue(ValueName) as string ?? "";
            var stripped = StripStalePluginTrustPaths(existing, acadMajor, pluginRootForTrustCleanup);
            var merged = MergePaths(stripped, dottedPaths);
            if (string.Equals(merged, existing, StringComparison.Ordinal))
                return 0;

            var kind = GetWritableStringValueKind(vars, ValueName);

            vars.SetValue(ValueName, merged, kind);
            log?.Invoke(
                $"已创建默认配置并写入受信任位置 (TRUSTEDPATHS)：{versionName}\\{productName}\\Profiles\\{DefaultProfileFolderName}");
            return 1;
        }
        catch (Exception ex)
        {
            log?.Invoke($"创建默认 Profile 并写入受信任位置失败：{versionName}\\{productName} — {ex.Message}");
            return 0;
        }
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
}
