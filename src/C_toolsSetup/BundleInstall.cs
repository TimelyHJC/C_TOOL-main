using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace C_toolsSetup;

internal static class BundleInstall
{
    internal const string DefaultInstallFolderName = "C_tool插件";
    internal const string PluginSubfolderName = "Plugin";
    internal const string UserSubfolderName = "User";
    internal const string UserC_toolsFolderName = "C_TOOL";
    internal const string AaaLibraryFolderName = "AA图库";
    internal const string InitialUserDataFolderName = "model";
    internal const string InitialDataFileName = "初始化文件.md";
    internal const string DefaultPlotStyleFileName = "C_tool.ctb";
    private const string LegacyInitialShortcutFileName = "初始化快捷键文件.md";

    internal const string RegistryKeyPath = @"Software\C_TOOL";
    internal const string RegistryValueInstallRoot = "InstallRoot";
    internal const string RegistryValuePluginFolder = "PluginFolder";
    internal const string RegistryValueUserDataRoot = "UserDataRoot";
    internal const string RegistryValueInstalledVersion = "InstalledVersion";
    internal const string RegistryValueUpdateManifestUrl = "UpdateManifestUrl";
    internal const string RegistryValueLastAcadVersionKey = "LastAcadVersionKey";
    internal const string RegistryValueLastAcadProductKey = "LastAcadProductKey";
    private const string InitialUserDataResourcePrefix = "InitialUserData/";
    internal const string PublishedPluginBundleDirectoryName2024 = "C_TOOL_2024.bundle";

    private static readonly string[] s_retiredBundleBaseNames =
    {
        "V_KKK",
        "V_YYY",
        "V_AAA",
        "V_BBB",
        "V_DDD",
        "V_QQQ"
    };
    /// <summary>当前默认发布的 AutoCAD 2024 bundle 文件夹名。</summary>
    internal const string PublishedPluginBundleDirectoryName = PublishedPluginBundleDirectoryName2024;

    /// <summary>插件仓库子文件夹名：可将多个 <c>*.bundle</c> 放入此目录（支持子文件夹）。</summary>
    internal const string RepositoryBundlesSubfolderName = "Bundles";

    /// <summary>
    /// 在「仓库根」（EXE 所在文件夹）收集插件包：<c>*.bundle</c> 可在根目录一层，或在 <see cref="RepositoryBundlesSubfolderName"/> 下任意深度。
    /// 去重后按文件名、全路径排序。
    /// </summary>
    internal static IReadOnlyList<string> DiscoverBundleDirectoriesInRepository(string? repositoryRoot)
    {
        if (string.IsNullOrEmpty(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return Array.Empty<string>();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(string dir, SearchOption option)
        {
            if (!Directory.Exists(dir))
                return;
            try
            {
                foreach (var path in Directory.EnumerateDirectories(dir, "*.bundle", option))
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(path);
                        if (File.Exists(Path.Combine(fullPath, "PackageContents.xml")))
                            set.Add(fullPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[C_TOOL Setup] 枚举 bundle 目录异常: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[C_TOOL Setup] 扫描目录失败: {ex.Message}");
            }
        }

        Collect(repositoryRoot, SearchOption.TopDirectoryOnly);
        Collect(Path.Combine(repositoryRoot, RepositoryBundlesSubfolderName), SearchOption.AllDirectories);

        return set
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>由 <c>某.bundle</c> 目录名与主 DLL 文件名推断目标 CAD 代际。</summary>
    internal static AcadBundleReleaseBand ClassifyBundleDirectory(string bundleDirectoryPath)
    {
        var bundleName = Path.GetFileName(bundleDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var primaryDll = AcadBundleLayout.TryFindPrimaryDll(bundleDirectoryPath);
        return AcadReleaseTargeting.ClassifyBundle(bundleName, primaryDll != null ? Path.GetFileName(primaryDll) : null);
    }

    /// <summary>验证 bundle 目录结构是否可安装，至少需包含根层 <c>PackageContents.xml</c>。</summary>
    internal static string? ValidateBundleDirectoryLayout(string bundleDirectoryPath)
    {
        try
        {
            if (!Directory.Exists(bundleDirectoryPath))
                return "bundle 目录不存在。";

            var dirName = Path.GetFileName(bundleDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!dirName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                return "目录名不是 *.bundle。";

            var packageContents = Path.Combine(bundleDirectoryPath, "PackageContents.xml");
            if (!File.Exists(packageContents))
                return "缺少 PackageContents.xml，无法作为 AutoCAD bundle 安装。";

            var startupModules = AcadBundleLayout.GetStartupModules(bundleDirectoryPath);
            if (startupModules.Count == 0)
                return "PackageContents.xml 中未找到可加载的插件 DLL（应位于 Contents\\Win64 下），无法作为 AutoCAD bundle 安装。";

            return null;
        }
        catch (IOException ex)
        {
            return "无法读取 bundle 目录：" + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return "读取 bundle 目录时被拒绝访问：" + ex.Message;
        }
    }

    /// <summary>将 <paramref name="fullPath"/> 显示为相对仓库根的路径（便于日志）。</summary>
    internal static string FormatPathRelativeToRepository(string repositoryRoot, string fullPath)
    {
        try
        {
            var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fp = Path.GetFullPath(fullPath);
            if (fp.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fp.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fp[(root.Length + 1)..];
            return fp;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] FormatPathRelativeToRepository: {ex.Message}");
            return fullPath;
        }
    }

    /// <summary>将 zip 解压到 <paramref name="extractParent"/> 下（根目录可为 <c>*.bundle</c> 文件夹）。</summary>
    internal static void ExtractZipToDirectory(string zipPath, string extractParent)
    {
        using var stream = File.OpenRead(zipPath);
        ExtractZipStreamToDirectory(stream, extractParent);
    }

    /// <summary>在真正解压前验证 zip 结构是否为单一的 <c>*.bundle</c> 根目录，并包含 <c>PackageContents.xml</c>。</summary>
    internal static string? ValidateBundleZipLayout(string zipPath)
    {
        try
        {
            using var stream = File.OpenRead(zipPath);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var expectedBundleName = Path.GetFileNameWithoutExtension(zipPath);
            string? rootBundleName = null;
            var hasAnyEntry = false;
            var hasPackageContents = false;

            foreach (var entry in zip.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/').Trim('/');
                if (normalized.Length == 0)
                    continue;

                hasAnyEntry = true;
                if (normalized.Equals("..", StringComparison.Ordinal) ||
                    normalized.StartsWith("../", StringComparison.Ordinal) ||
                    normalized.Contains("/../", StringComparison.Ordinal))
                {
                    return "压缩包包含非法相对路径（..），已拒绝安装。";
                }

                var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                var root = parts[0];
                if (!root.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                {
                    return "压缩包根目录不是 *.bundle。请重新打包为“插件名.bundle\\...”结构后重试。";
                }

                if (rootBundleName == null)
                {
                    rootBundleName = root;
                }
                else if (!string.Equals(rootBundleName, root, StringComparison.OrdinalIgnoreCase))
                {
                    return "压缩包同时包含多个顶层 *.bundle 目录。请每个 zip 只保留一个 bundle。";
                }

                if (parts.Length >= 2 &&
                    string.Equals(parts[1], "PackageContents.xml", StringComparison.OrdinalIgnoreCase))
                {
                    hasPackageContents = true;
                }
            }

            if (!hasAnyEntry)
                return "压缩包为空，无法安装。";

            if (rootBundleName == null)
                return "压缩包中未找到顶层 *.bundle 目录。";

            if (!string.Equals(rootBundleName, expectedBundleName, StringComparison.OrdinalIgnoreCase))
            {
                return $"压缩包文件名与内部 bundle 目录不一致：zip 为 {expectedBundleName}，包内为 {rootBundleName}。";
            }

            if (!hasPackageContents)
                return "压缩包缺少 PackageContents.xml，无法作为 AutoCAD bundle 安装。";

            return null;
        }
        catch (InvalidDataException ex)
        {
            return "压缩包已损坏或不是有效 zip：" + ex.Message;
        }
        catch (IOException ex)
        {
            return "无法读取压缩包：" + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return "读取压缩包时被拒绝访问：" + ex.Message;
        }
    }

    private static bool IsResolvedPathWithinDirectory(string candidateFullPath, string directoryFullPath)
    {
        var root = Path.GetFullPath(directoryFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(candidateFullPath);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return true;
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private const int ZipExtractRetryCount = 5;
    private static readonly TimeSpan ZipExtractRetryDelay = TimeSpan.FromMilliseconds(450);

    private static void ExtractZipStreamToDirectory(Stream stream, string extractParent)
    {
        var extractRoot = Path.GetFullPath(extractParent);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (name.Contains("..", StringComparison.Ordinal))
                continue;
            if (string.IsNullOrEmpty(name))
                continue;
            var dest = Path.Combine(extractParent, name);
            var fullDest = Path.GetFullPath(dest);
            if (!IsResolvedPathWithinDirectory(fullDest, extractRoot))
                continue;
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(fullDest);
                continue;
            }

            var dir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            ExtractZipEntryToFileWithRetry(entry, fullDest);
        }
    }

    private static void PrepareExistingFileForOverwrite(string fullPath)
    {
        if (!File.Exists(fullPath))
            return;
        try
        {
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] 清除只读属性失败: {fullPath} — {ex.Message}");
        }
    }

    private static void ExtractZipEntryToFileWithRetry(ZipArchiveEntry entry, string fullDest)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < ZipExtractRetryCount; attempt++)
        {
            try
            {
                PrepareExistingFileForOverwrite(fullDest);
                entry.ExtractToFile(fullDest, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            if (attempt < ZipExtractRetryCount - 1)
                Thread.Sleep(ZipExtractRetryDelay);
        }

        throw new IOException(BuildZipExtractAccessDeniedMessage(fullDest), last);
    }

    private static string BuildZipExtractAccessDeniedMessage(string fullDest)
    {
        var sb = new StringBuilder();
        sb.Append("无法覆盖文件（访问被拒绝）：").Append(fullDest).Append("\r\n");
        if (fullPathLooksLike2024BundleDll(fullDest))
        {
            sb.Append("该路径属于 *_2024.bundle（2024 插件）。请先关闭可能已加载这些 DLL 的 AutoCAD 2024，再重试安装。");
        }
        else
        {
            sb.Append("请关闭可能占用该文件的程序后重试。");
        }

        return sb.ToString();
    }

    private static bool fullPathLooksLike2024BundleDll(string fullDest)
    {
        return fullDest.Contains("_2024.bundle" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullDest.Contains("_2024.bundle/", StringComparison.OrdinalIgnoreCase);
    }

    internal static void CopyDirectoryRecursive(
        string sourceDir,
        string destDir,
        Action<DirectoryCopyProgress>? onFileCopied = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException(sourceDir);
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var sourceInfo = new FileInfo(file);
            var destinationPath = Path.Combine(destDir, name);
            File.Copy(file, destinationPath, overwrite: true);
            onFileCopied?.Invoke(new DirectoryCopyProgress(sourceInfo.FullName, destinationPath, sourceInfo.Length));
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name), onFileCopied);
        }
    }

    /// <summary>复制初始用户数据到 <c>User</c> 根目录；显式选择的初始化文件会作为当前安装来源写入。</summary>
    internal static int SeedInitialUserData(
        string installRoot,
        string sourceRoot,
        string? initialUserDataFolderPath = null,
        Action<string>? log = null)
    {
        var userRoot = Path.Combine(installRoot, UserSubfolderName);
        Directory.CreateDirectory(userRoot);

        var copied = 0;
        var sourceDir = !string.IsNullOrWhiteSpace(initialUserDataFolderPath)
            ? Path.GetFullPath(initialUserDataFolderPath)
            : TryResolveInitialUserDataFolder(sourceRoot);
        if (sourceDir == null)
        {
            var embeddedCopied = CopyEmbeddedInitialUserData(userRoot);
            copied += embeddedCopied;
            if (embeddedCopied > 0)
                log?.Invoke($"已复制内置初始用户数据：{InitialUserDataFolderName} -> User（{embeddedCopied} 个文件）。");
        }
        else
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException(sourceDir);

            var modelCopied = CopyInitialUserDataDirectory(sourceDir, userRoot);
            copied += modelCopied;
            if (modelCopied > 0)
                log?.Invoke($"已复制初始用户数据：{sourceDir} -> User（{modelCopied} 个文件）。");
            else
                log?.Invoke($"初始用户数据已存在，未覆盖 User 中的已有文件：{InitialUserDataFolderName}");
        }

        return copied;
    }

    internal static string? TryResolveDefaultInitialUserDataFolderPath(string? sourceRoot)
    {
        return TryResolveInitialUserDataFolder(sourceRoot);
    }

    internal static string? TryResolveInitialPlotStyleFilePath(string? initialUserDataFolderPath)
    {
        if (string.IsNullOrWhiteSpace(initialUserDataFolderPath))
            return null;

        try
        {
            var candidate = Path.Combine(Path.GetFullPath(initialUserDataFolderPath), DefaultPlotStyleFileName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveInitialUserDataFolder(string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
            return null;

        try
        {
            var candidate = Path.Combine(Path.GetFullPath(sourceRoot), InitialUserDataFolderName);
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] Resolve initial user data folder failed: {ex.Message}");
            return null;
        }
    }

    private static int CopyInitialUserDataDirectory(string sourceDir, string destDir)
    {
        var copied = 0;
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            if (IsCadPlotStyleFile(fileName))
                continue;

            var dest = Path.Combine(destDir, MapInitialUserDataFileName(fileName));
            if (File.Exists(dest))
                continue;

            File.Copy(file, dest, overwrite: false);
            copied++;
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            copied += CopyInitialUserDataDirectory(sub, Path.Combine(destDir, name));
        }

        return copied;
    }

    private static bool IsCadPlotStyleFile(string fileName)
    {
        return fileName.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".stb", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapInitialUserDataFileName(string fileName)
    {
        return string.Equals(fileName, LegacyInitialShortcutFileName, StringComparison.OrdinalIgnoreCase)
            ? InitialDataFileName
            : fileName;
    }

    private static string MapInitialUserDataRelativePath(string relativePath)
    {
        var dir = Path.GetDirectoryName(relativePath);
        var fileName = Path.GetFileName(relativePath);
        var mapped = MapInitialUserDataFileName(fileName);
        return string.IsNullOrEmpty(dir) ? mapped : Path.Combine(dir, mapped);
    }

    private static int CopyEmbeddedInitialUserData(string destDir)
    {
        var assembly = typeof(BundleInstall).Assembly;
        var copied = 0;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var relative = TryGetInitialUserDataResourceRelativePath(resourceName);
            if (relative == null)
                continue;
            if (IsCadPlotStyleFile(Path.GetFileName(relative)))
                continue;

            var dest = Path.GetFullPath(Path.Combine(destDir, MapInitialUserDataRelativePath(relative)));
            if (!IsResolvedPathWithinDirectory(dest, destDir) || File.Exists(dest))
                continue;

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var source = assembly.GetManifestResourceStream(resourceName);
            if (source == null)
                continue;

            using var target = File.Create(dest);
            source.CopyTo(target);
            copied++;
        }

        return copied;
    }

    private static string? TryGetInitialUserDataResourceRelativePath(string resourceName)
    {
        if (!resourceName.StartsWith(InitialUserDataResourcePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = resourceName[InitialUserDataResourcePrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (relative.Length == 0 || Path.IsPathRooted(relative))
            return null;

        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, "..", StringComparison.Ordinal)) ? null : relative;
    }

    /// <summary>安装根下的 <c>User\C_TOOL\</c> 自动数据目录；插件通过注册表 UserDataRoot 读写此目录。</summary>
    internal static void WriteUserFolderLayout(string installRoot)
    {
        var userRoot = Path.Combine(installRoot, UserSubfolderName);
        var userC_toolRoot = Path.Combine(userRoot, UserC_toolsFolderName);
        var support = Path.Combine(userC_toolRoot, "Support");
        var aaaLibraryFolder = Path.Combine(installRoot, AaaLibraryFolderName);
        Directory.CreateDirectory(support);
        Directory.CreateDirectory(aaaLibraryFolder);
    }

    internal static void WriteC_toolsRegistry(string installRoot, string pluginFolder, string userDataRoot)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        k.SetValue(RegistryValueInstallRoot, installRoot);
        k.SetValue(RegistryValuePluginFolder, pluginFolder);
        k.SetValue(RegistryValueUserDataRoot, userDataRoot);
    }

    internal static string? TryReadInstalledVersion()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            return (k?.GetValue(RegistryValueInstalledVersion) as string)?.Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] Read installed version failed: {ex.Message}");
            return null;
        }
    }

    internal static void WriteInstalledVersion(string? version)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (k == null)
                return;

            if (string.IsNullOrWhiteSpace(version))
                k.DeleteValue(RegistryValueInstalledVersion, throwOnMissingValue: false);
            else
                k.SetValue(RegistryValueInstalledVersion, version.Trim());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] Write installed version failed: {ex.Message}");
        }
    }

    internal static (string? VersionKey, string? ProductKey) ReadLastAcadSelection()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            var versionKey = (k?.GetValue(RegistryValueLastAcadVersionKey) as string)?.Trim();
            var productKey = (k?.GetValue(RegistryValueLastAcadProductKey) as string)?.Trim();

            if (string.IsNullOrWhiteSpace(versionKey) || string.IsNullOrWhiteSpace(productKey))
                return (null, null);

            return (versionKey, productKey);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] 读取上次 CAD 版本失败: {ex.Message}");
            return (null, null);
        }
    }

    internal static void WriteLastAcadSelection(string? versionKey, string? productKey)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (k == null)
                return;

            if (string.IsNullOrWhiteSpace(versionKey) || string.IsNullOrWhiteSpace(productKey))
            {
                k.DeleteValue(RegistryValueLastAcadVersionKey, throwOnMissingValue: false);
                k.DeleteValue(RegistryValueLastAcadProductKey, throwOnMissingValue: false);
                return;
            }

            k.SetValue(RegistryValueLastAcadVersionKey, versionKey.Trim());
            k.SetValue(RegistryValueLastAcadProductKey, productKey.Trim());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] 写入上次 CAD 版本失败: {ex.Message}");
        }
    }

    /// <summary>读取上次安装写入的 Plugin 目录（用于安装前卸载旧版 ApplicationPlugins 副本）。</summary>
    internal static string? TryGetRegistryPluginFolder()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            var s = k?.GetValue(RegistryValuePluginFolder) as string;
            if (string.IsNullOrWhiteSpace(s))
                return null;
            return Path.GetFullPath(s.Trim());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] 读取注册表 PluginFolder: {ex.Message}");
            return null;
        }
    }

    /// <summary>本机两处 Autodesk ApplicationPlugins 根目录（当前用户 / 所有用户）。</summary>
    internal static IEnumerable<string> EnumerateApplicationPluginRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk",
            "ApplicationPlugins");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk",
            "ApplicationPlugins");
    }

    /// <summary>枚举 <paramref name="pluginFolder"/> 下以 .bundle 结尾的目录名（不含路径）。</summary>
    internal static IReadOnlyList<string> ListBundleDirectoryNames(string pluginFolder)
    {
        if (!Directory.Exists(pluginFolder))
            return Array.Empty<string>();

        return Directory
            .EnumerateDirectories(pluginFolder)
            .Select(Path.GetFileName)
            .Where(n => n != null && n.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool IsLegacyCtoolsBundleDirectoryName(string bundleDirectoryName)
    {
        var normalized = Path.GetFileName(bundleDirectoryName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var baseName in s_retiredBundleBaseNames)
        {
            if (string.Equals(normalized, baseName + ".bundle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, baseName + "_2024.bundle", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> GetLegacyCtoolsBundleDirectoryNames(AcadBundleReleaseBand band)
    {
        return s_retiredBundleBaseNames
            .Select(baseName => baseName + "_2024.bundle")
            .ToList();
    }

    /// <summary>默认安装父目录：存在则用 D:\\，否则用「文档」。</summary>
    internal static string DefaultParentInstallPath()
    {
        try
        {
            if (Directory.Exists(@"D:\"))
                return @"D:\";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] DefaultParentInstallPath 检测 D:\\: {ex.Message}");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>与安装程序 EXE 同目录（单文件发布时为 EXE 所在文件夹）。</summary>
    internal static string? GetSetupExeDirectory()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (string.IsNullOrEmpty(p))
                return null;
            return Path.GetDirectoryName(p);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C_TOOL Setup] GetSetupExeDirectory: {ex.Message}");
            return null;
        }
    }
}
