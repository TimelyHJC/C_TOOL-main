using System.Xml.Linq;

namespace C_toolsSetup;

internal sealed record InstallOptions(
    string InstallParentPath,
    string InstallSubfolderName,
    bool InstallForAllUsers,
    string? AcadRegistryVersionKey,
    string? AcadRegistryProductKey,
    string? InitialUserDataFolderPath);

internal enum InstallExitCode
{
    Ok,
    InvalidInput,
    NoBundles,
    Unauthorized,
    FileLocked,
    Error
}

internal sealed record InstallEngineResult(
    InstallExitCode Code,
    string? DialogMessage = null,
    string? InstallRoot = null,
    IReadOnlyList<string>? InstalledBundleNames = null);

internal static class InstallEngine
{
    private const int SupportedAcadReleaseMajor = 24;

    internal static InstallEngineResult Execute(
        InstallOptions options,
        string? bundleSourceRoot,
        Action<string>? log,
        Action<SetupProgressUpdate>? progress)
    {
        PluginDeploymentTransaction? transaction = null;

        try
        {
            if (!TryResolveInstallRoot(options, out var installRoot, out var validationMessage))
                return new InstallEngineResult(InstallExitCode.InvalidInput, validationMessage);
            if (!TryResolveInitialUserDataFolderPath(options, out var initialUserDataFolderPath, out validationMessage))
                return new InstallEngineResult(InstallExitCode.InvalidInput, validationMessage);

            var sourceRoot = ResolveBundleSourceRoot(bundleSourceRoot);
            var bundleDirsToInstall = Discover2024BundleDirectories(sourceRoot, log);
            if (bundleDirsToInstall.Count == 0)
            {
                var sourceHint = string.IsNullOrWhiteSpace(sourceRoot) ? "安装程序同目录" : sourceRoot;
                return new InstallEngineResult(
                    InstallExitCode.NoBundles,
                    $"未找到可安装的 AutoCAD 2024 插件包。\r\n\r\n请确认 {sourceHint} 或其 Bundles 子目录中存在 *_2024.bundle。");
            }

            var layoutErrors = ValidateBundleLayouts(bundleDirsToInstall);
            if (layoutErrors.Count > 0)
            {
                return new InstallEngineResult(
                    InstallExitCode.NoBundles,
                    "发现插件包结构不完整，无法安装：\r\n\r\n" + string.Join("\r\n", layoutErrors));
            }

            var bundleNames = bundleDirsToInstall
                .Select(path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pluginDir = Path.Combine(installRoot, BundleInstall.PluginSubfolderName);
            var userDir = Path.Combine(installRoot, BundleInstall.UserSubfolderName);
            var userDataRoot = Path.Combine(userDir, BundleInstall.UserC_toolsFolderName);
            var copyPlan = MeasureBundleCopyPlan(bundleDirsToInstall);
            var progressTracker = new InstallProgressTracker(copyPlan, progress);

            log?.Invoke($"安装目录：{installRoot}");
            log?.Invoke($"插件来源：{sourceRoot}");
            if (!string.IsNullOrWhiteSpace(initialUserDataFolderPath))
                log?.Invoke($"初始化文件夹：{initialUserDataFolderPath}");
            log?.Invoke("本次仅安装 AutoCAD 2024 插件包：" + string.Join(", ", bundleNames));

            Directory.CreateDirectory(installRoot);
            transaction = new PluginDeploymentTransaction(installRoot, pluginDir);
            progressTracker.ReportPreparingStaging();
            transaction.PrepareStagingDirectory(log);

            foreach (var bundleDir in bundleDirsToInstall)
            {
                var bundleName = Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var targetBundleDir = Path.Combine(transaction.StagingPluginDir, bundleName);
                progressTracker.BeginBundleCopy(bundleName);
                log?.Invoke($"复制 {BundleInstall.FormatPathRelativeToRepository(sourceRoot, bundleDir)} → Plugin\\{bundleName}");
                BundleInstall.CopyDirectoryRecursive(
                    bundleDir,
                    targetBundleDir,
                    copied => progressTracker.ReportFileCopied(bundleName, copied.FileSizeBytes));
            }

            progressTracker.ReportWritingInstallRegistry();
            BundleInstall.SeedInitialUserData(installRoot, sourceRoot, initialUserDataFolderPath, log);
            BundleInstall.WriteUserFolderLayout(installRoot);
            var initialDataFolderForInstall =
                initialUserDataFolderPath ?? BundleInstall.TryResolveDefaultInitialUserDataFolderPath(sourceRoot);
            var plotStylePath =
                BundleInstall.TryResolveInitialPlotStyleFilePath(initialDataFolderForInstall) ??
                BundleInstall.TryResolveInitialPlotStyleFilePath(
                    BundleInstall.TryResolveDefaultInitialUserDataFolderPath(sourceRoot));
            AcadPlotStyleInstaller.InstallPlotStyleToProfiles(
                plotStylePath,
                options.AcadRegistryVersionKey,
                options.AcadRegistryProductKey,
                log);
            var fontPaths =
                BundleInstall.TryResolveInitialFontFilePaths(initialDataFolderForInstall)
                    .Concat(BundleInstall.TryResolveInitialFontFilePaths(
                        BundleInstall.TryResolveDefaultInitialUserDataFolderPath(sourceRoot)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            AcadFontInstaller.InstallFontsToProfiles(
                fontPaths,
                options.AcadRegistryVersionKey,
                options.AcadRegistryProductKey,
                log);
            AcadFontInstaller.CopyFontsToDirectory(
                fontPaths,
                transaction.StagingPluginDir,
                log);

            progressTracker.ReportPromotingPluginDirectory();
            transaction.PromoteStagingDirectory(log);

            BundleInstall.WriteC_toolsRegistry(installRoot, pluginDir, userDataRoot);
            var installedVersion = ResolveInstalledBundleVersion(bundleDirsToInstall);
            BundleInstall.WriteInstalledVersion(installedVersion);
            if (!string.IsNullOrWhiteSpace(installedVersion))
                log?.Invoke($"Installed version: {installedVersion}");

            if (HasAcadRegistryTarget(options))
            {
                var bundleTrustRoots = BuildBundleTrustRoots(pluginDir, bundleNames);
                progressTracker.ReportUpdatingTrustedPaths();
                var trustedPathCount = AcadTrustedPaths.AppendToProfiles(
                    new[] { pluginDir, userDir, userDataRoot },
                    bundleTrustRoots,
                    pluginDir,
                    options.AcadRegistryVersionKey,
                    options.AcadRegistryProductKey,
                    log);
                log?.Invoke($"受信任位置写入/更新数量：{trustedPathCount}");
                var supportPathCount = AcadFontInstaller.AppendSupportSearchPathToProfiles(
                    pluginDir,
                    options.AcadRegistryVersionKey,
                    options.AcadRegistryProductKey,
                    log);
                log?.Invoke($"CAD 支持搜索路径写入/更新数量：{supportPathCount}");

                progressTracker.ReportUpdatingStartupEntries();
                var startupCount = AcadPluginStartupRegistry.RegisterBundlesAndCleanupOppositeHive(
                    pluginDir,
                    bundleNames,
                    options.AcadRegistryVersionKey,
                    options.AcadRegistryProductKey,
                    options.InstallForAllUsers,
                    log);
                log?.Invoke($"启动项写入数量：{startupCount}");
            }
            else
            {
                log?.Invoke("手动模式：未选择 AutoCAD 2024 注册表项，本次只复制插件文件并写入 C_TOOL 本地配置。");
            }

            progressTracker.ReportPurgingLegacyCopies();
            RemoveBundlesFromAllApplicationPluginRoots(BuildApplicationPluginCleanupBundleNames(bundleNames), log);

            progressTracker.ReportCleanup();
            transaction.CleanupBackupDirectory(log);
            transaction.CleanupTemporaryDirectories(log);
            progressTracker.ReportCompleted();

            return new InstallEngineResult(
                InstallExitCode.Ok,
                InstallRoot: installRoot,
                InstalledBundleNames: bundleNames);
        }
        catch (UnauthorizedAccessException ex)
        {
            transaction?.Rollback(log);
            transaction?.CleanupTemporaryDirectories(log);
            log?.Invoke("安装失败：访问被拒绝。" + ex.Message);
            return new InstallEngineResult(
                InstallExitCode.Unauthorized,
                BuildAccessDeniedUserHint(options, ex));
        }
        catch (IOException ex) when (IsAccessDeniedIOException(ex))
        {
            transaction?.Rollback(log);
            transaction?.CleanupTemporaryDirectories(log);
            log?.Invoke("安装失败：访问被拒绝。" + ex.Message);
            return new InstallEngineResult(
                InstallExitCode.Unauthorized,
                BuildAccessDeniedUserHint(options, ex));
        }
        catch (IOException ex) when (IsFileLockedIOException(ex))
        {
            transaction?.Rollback(log);
            transaction?.CleanupTemporaryDirectories(log);
            log?.Invoke("安装失败：文件正在被占用。" + ex.Message);
            return new InstallEngineResult(
                InstallExitCode.FileLocked,
                BuildFileAccessBlockedHint(options));
        }
        catch (Exception ex)
        {
            transaction?.Rollback(log);
            transaction?.CleanupTemporaryDirectories(log);
            log?.Invoke("安装失败：" + ex);
            return new InstallEngineResult(
                InstallExitCode.Error,
                "安装失败：\r\n\r\n" + ex.Message);
        }
    }

    private static bool TryResolveInstallRoot(
        InstallOptions options,
        out string installRoot,
        out string validationMessage)
    {
        installRoot = "";
        validationMessage = "";

        var parent = options.InstallParentPath.Trim();
        var folder = options.InstallSubfolderName.Trim();
        if (parent.Length == 0 || folder.Length == 0)
        {
            validationMessage = "请输入有效的完整安装路径，例如 D:\\C_tool插件。";
            return false;
        }

        if (Path.IsPathRooted(folder) ||
            folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            folder.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            folder.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            validationMessage = "安装路径的最后一级文件夹名称无效。";
            return false;
        }

        try
        {
            installRoot = Path.GetFullPath(Path.Combine(parent, folder));
            var normalized = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            {
                validationMessage = "不能直接安装到磁盘根目录，请选择一个子文件夹。";
                return false;
            }
        }
        catch (Exception ex)
        {
            validationMessage = "安装路径无效：" + ex.Message;
            return false;
        }

        return true;
    }

    private static bool TryResolveInitialUserDataFolderPath(
        InstallOptions options,
        out string? initialUserDataFolderPath,
        out string validationMessage)
    {
        initialUserDataFolderPath = null;
        validationMessage = "";

        if (string.IsNullOrWhiteSpace(options.InitialUserDataFolderPath))
            return true;

        try
        {
            var fullPath = Path.GetFullPath(options.InitialUserDataFolderPath.Trim());
            if (!Directory.Exists(fullPath))
            {
                validationMessage = "初始化文件夹不存在：\r\n" + fullPath;
                return false;
            }

            initialUserDataFolderPath = fullPath;
            return true;
        }
        catch (Exception ex)
        {
            validationMessage = "初始化文件夹路径无效：\r\n" + ex.Message;
            return false;
        }
    }

    private static string ResolveBundleSourceRoot(string? bundleSourceRoot)
    {
        if (!string.IsNullOrWhiteSpace(bundleSourceRoot) && Directory.Exists(bundleSourceRoot))
            return Path.GetFullPath(bundleSourceRoot);

        var appBase = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appBase) && Directory.Exists(appBase))
            return Path.GetFullPath(appBase);

        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    private static IReadOnlyList<string> Discover2024BundleDirectories(string sourceRoot, Action<string>? log)
    {
        var discovered = BundleInstall.DiscoverBundleDirectoriesInRepository(sourceRoot);
        var selected = new List<string>();
        foreach (var dir in discovered)
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (IsAutoCad2024BundleName(name))
            {
                selected.Add(dir);
                continue;
            }

            log?.Invoke($"跳过非 AutoCAD 2024 插件包：{BundleInstall.FormatPathRelativeToRepository(sourceRoot, dir)}");
        }

        return selected
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsAutoCad2024BundleName(string? bundleName)
    {
        if (string.IsNullOrWhiteSpace(bundleName))
            return false;

        return string.Equals(bundleName, BundleInstall.PublishedPluginBundleDirectoryName2024, StringComparison.OrdinalIgnoreCase)
               || bundleName.EndsWith("_2024.bundle", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ValidateBundleLayouts(IReadOnlyList<string> bundleDirs)
    {
        var errors = new List<string>();
        foreach (var bundleDir in bundleDirs)
        {
            var error = BundleInstall.ValidateBundleDirectoryLayout(bundleDir);
            if (error == null)
                continue;

            var bundleName = Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            errors.Add($"{bundleName}：{error}");
        }

        return errors;
    }

    private static bool HasAcadRegistryTarget(InstallOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AcadRegistryVersionKey) ||
            string.IsNullOrWhiteSpace(options.AcadRegistryProductKey))
        {
            return false;
        }

        return AcadReleaseTargeting.TryParseReleaseMajor(options.AcadRegistryVersionKey, out var major)
               && major == SupportedAcadReleaseMajor;
    }

    private static string? ResolveInstalledBundleVersion(IReadOnlyList<string> bundleDirs)
    {
        foreach (var bundleDir in bundleDirs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var version = TryReadBundleVersion(bundleDir);
            if (!string.IsNullOrWhiteSpace(version))
                return ProductVersionComparer.NormalizeForDisplay(version);
        }

        return typeof(InstallEngine).Assembly.GetName().Version?.ToString(3);
    }

    private static string? TryReadBundleVersion(string bundleDir)
    {
        try
        {
            var packageContentsPath = Path.Combine(bundleDir, "PackageContents.xml");
            if (!File.Exists(packageContentsPath))
                return null;

            var document = XDocument.Load(packageContentsPath);
            var packageVersion = document.Root?.Attribute("AppVersion")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(packageVersion))
                return packageVersion;

            return document
                .Descendants()
                .Where(x => string.Equals(x.Name.LocalName, "ComponentEntry", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Attribute("Version")?.Value?.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryReadBundleVersion: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<(string Path, AcadBundleReleaseBand Band)> BuildBundleTrustRoots(
        string pluginDir,
        IReadOnlyList<string> bundleNames)
    {
        return bundleNames
            .Select(name => (Path: Path.Combine(pluginDir, name), Band: AcadBundleReleaseBand.R24NetFx))
            .ToList();
    }

    private static IReadOnlyList<string> BuildApplicationPluginCleanupBundleNames(IReadOnlyList<string> installedBundleNames)
    {
        return installedBundleNames
            .Concat(BundleInstall.GetLegacyCtoolsBundleDirectoryNames(AcadBundleReleaseBand.R24NetFx))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildAccessDeniedUserHint(InstallOptions options, Exception ex)
    {
        var parts = new List<string>
        {
            "安装时被系统拒绝访问：\r\n" + ex.Message
        };

        if (options.InstallForAllUsers)
        {
            parts.Add(
                "当前选择了“所有用户”安装范围，写入系统级 AutoCAD 启动项可能需要管理员权限。\r\n" +
                "请右键以管理员身份运行安装器，或改选“当前用户”后重试。");
        }
        else
        {
            var installRoot = TryGetInstallRootFromOptions(options);
            if (installRoot != null && InstallPathLooksLikeCloudSyncedFolder(installRoot))
            {
                parts.Add(
                    "检测到安装路径位于常见网盘/同步目录下。同步软件可能随时锁定文件，导致“拒绝访问”。\r\n" +
                    "建议暂停该目录同步，或将 C_TOOL 安装到不同步的本地文件夹（例如 D:\\C_TOOL_Local）后再安装。");
            }
            else
            {
                parts.Add(
                    "若只安装到当前用户仍被拒绝访问，通常是安装目录权限、防护软件拦截，或同步软件占用。\r\n" +
                    "可尝试换到非同步盘路径，或确认对安装路径所在目录有完全控制权限。");
            }
        }

        parts.Add("若以上已排除，再确认没有程序占用插件文件：\r\n" + BuildFileAccessBlockedHint(options));
        return string.Join("\r\n\r\n", parts);
    }

    private static string? TryGetInstallRootFromOptions(InstallOptions options)
    {
        try
        {
            var parent = options.InstallParentPath.Trim();
            var folder = options.InstallSubfolderName.Trim();
            if (parent.Length == 0 || folder.Length == 0)
                return null;

            return Path.GetFullPath(Path.Combine(parent, folder));
        }
        catch
        {
            return null;
        }
    }

    private static bool InstallPathLooksLikeCloudSyncedFolder(string installRoot)
    {
        var normalized = installRoot.Replace('/', Path.DirectorySeparatorChar);
        return normalized.Contains("BaiduSyncdisk", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("BaiduNetdisk", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("Nutstore", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFileAccessBlockedHint(InstallOptions options)
    {
        var installRoot = TryGetInstallRootFromOptions(options);
        var locationHint = installRoot == null ? "安装目录" : installRoot;
        return $"请关闭可能正在使用 {locationHint} 下 AutoCAD 2024 插件包（*_2024.bundle）的 AutoCAD 2024，或结束占用该文件夹的进程后再试。";
    }

    private static bool IsAccessDeniedIOException(IOException ex) =>
        (ex.HResult & 0xFFFF) == 5;

    private static bool IsFileLockedIOException(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static void RemoveBundlesFromAllApplicationPluginRoots(IEnumerable<string> bundleNames, Action<string>? log)
    {
        foreach (var pluginsRoot in BundleInstall.EnumerateApplicationPluginRoots())
        {
            if (!Directory.Exists(pluginsRoot))
                continue;

            foreach (var name in bundleNames)
            {
                var dest = Path.Combine(pluginsRoot, name);
                if (!Directory.Exists(dest))
                    continue;

                try
                {
                    log?.Invoke($"  删除旧 ApplicationPlugins 副本：{dest}");
                    Directory.Delete(dest, recursive: true);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  警告：无法删除 {dest}：{ex.Message}");
                }
            }
        }
    }

    private static BundleCopyPlan MeasureBundleCopyPlan(IReadOnlyList<string> bundleDirsToInstall)
    {
        var bundleFileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var totalFiles = 0;

        foreach (var bundleDir in bundleDirsToInstall)
        {
            var bundleName = Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var fileCount = 0;
            foreach (var file in Directory.EnumerateFiles(bundleDir, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                totalFiles++;
                totalBytes += new FileInfo(file).Length;
            }

            bundleFileCounts[bundleName] = Math.Max(fileCount, 1);
        }

        return new BundleCopyPlan(totalFiles, totalBytes, bundleFileCounts);
    }

    private sealed class PluginDeploymentTransaction
    {
        private readonly string _livePluginDir;
        private readonly string _stagingPluginDir;
        private readonly string _backupPluginDir;
        private bool _existingPluginMovedToBackup;
        private bool _stagingPromotedToLive;

        internal PluginDeploymentTransaction(string installRoot, string livePluginDir)
        {
            _livePluginDir = livePluginDir;
            var token = Guid.NewGuid().ToString("N")[..8];
            _stagingPluginDir = Path.Combine(installRoot, $".c_tool_plugin_staging_{token}");
            _backupPluginDir = Path.Combine(installRoot, $".c_tool_plugin_backup_{token}");
        }

        internal string StagingPluginDir => _stagingPluginDir;

        internal void PrepareStagingDirectory(Action<string>? log)
        {
            Directory.CreateDirectory(_stagingPluginDir);
            log?.Invoke($"临时安装目录：{_stagingPluginDir}");
        }

        internal void PromoteStagingDirectory(Action<string>? log)
        {
            if (!Directory.Exists(_stagingPluginDir))
                throw new DirectoryNotFoundException(_stagingPluginDir);

            if (Directory.Exists(_livePluginDir))
            {
                log?.Invoke($"检测到旧 Plugin，先移到回滚备份：{_backupPluginDir}");
                Directory.Move(_livePluginDir, _backupPluginDir);
                _existingPluginMovedToBackup = true;
            }
            else
            {
                log?.Invoke($"安装目录下未发现旧 Plugin，将直接启用新目录：{_livePluginDir}");
            }

            try
            {
                Directory.Move(_stagingPluginDir, _livePluginDir);
                _stagingPromotedToLive = true;
                log?.Invoke("新 Plugin 已切换到正式安装目录。");
            }
            catch (IOException ex)
            {
                TryRestoreLiveDirectoryBeforeRethrow(log, ex);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                TryRestoreLiveDirectoryBeforeRethrow(log, ex);
                throw;
            }
        }

        internal void Rollback(Action<string>? log)
        {
            if (_stagingPromotedToLive)
            {
                try
                {
                    if (Directory.Exists(_livePluginDir))
                    {
                        log?.Invoke("安装失败，正在移除未完成的新 Plugin…");
                        Directory.Delete(_livePluginDir, recursive: true);
                    }

                    _stagingPromotedToLive = false;
                }
                catch (IOException ex)
                {
                    log?.Invoke("警告：回滚时无法删除新 Plugin：" + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    log?.Invoke("警告：回滚时无法删除新 Plugin：" + ex.Message);
                }
            }

            if (_existingPluginMovedToBackup)
            {
                try
                {
                    if (Directory.Exists(_backupPluginDir) && !Directory.Exists(_livePluginDir))
                    {
                        log?.Invoke("安装失败，正在恢复旧 Plugin…");
                        Directory.Move(_backupPluginDir, _livePluginDir);
                    }

                    _existingPluginMovedToBackup = false;
                    log?.Invoke("Plugin 目录已完成回滚。");
                }
                catch (IOException ex)
                {
                    log?.Invoke("警告：回滚时无法恢复旧 Plugin：" + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    log?.Invoke("警告：回滚时无法恢复旧 Plugin：" + ex.Message);
                }
            }
        }

        internal void CleanupBackupDirectory(Action<string>? log)
        {
            if (!_existingPluginMovedToBackup || !Directory.Exists(_backupPluginDir))
                return;

            try
            {
                Directory.Delete(_backupPluginDir, recursive: true);
                _existingPluginMovedToBackup = false;
                log?.Invoke("已删除旧 Plugin 回滚备份。");
            }
            catch (IOException ex)
            {
                log?.Invoke("警告：无法删除旧 Plugin 回滚备份：" + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                log?.Invoke("警告：无法删除旧 Plugin 回滚备份：" + ex.Message);
            }
        }

        internal void CleanupTemporaryDirectories(Action<string>? log)
        {
            if (!Directory.Exists(_stagingPluginDir))
                return;

            try
            {
                Directory.Delete(_stagingPluginDir, recursive: true);
                log?.Invoke("已清理临时安装目录。");
            }
            catch (IOException ex)
            {
                log?.Invoke("警告：无法清理临时安装目录：" + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                log?.Invoke("警告：无法清理临时安装目录：" + ex.Message);
            }
        }

        private void TryRestoreLiveDirectoryBeforeRethrow(Action<string>? log, Exception ex)
        {
            log?.Invoke("切换正式 Plugin 目录失败，准备恢复旧目录：" + ex.Message);
            if (!_existingPluginMovedToBackup || Directory.Exists(_livePluginDir) || !Directory.Exists(_backupPluginDir))
                return;

            try
            {
                Directory.Move(_backupPluginDir, _livePluginDir);
                _existingPluginMovedToBackup = false;
                log?.Invoke("切换失败后已恢复旧 Plugin。");
            }
            catch (IOException rollbackEx)
            {
                log?.Invoke("警告：切换失败后无法自动恢复旧 Plugin：" + rollbackEx.Message);
            }
            catch (UnauthorizedAccessException rollbackEx)
            {
                log?.Invoke("警告：切换失败后无法自动恢复旧 Plugin：" + rollbackEx.Message);
            }
        }
    }

    private sealed record BundleCopyPlan(
        int TotalFiles,
        long TotalBytes,
        IReadOnlyDictionary<string, int> BundleFileCounts);

    private sealed class InstallProgressTracker
    {
        private readonly Action<SetupProgressUpdate>? _progress;
        private readonly BundleCopyPlan _bundleCopyPlan;
        private long _copiedBytes;
        private int _copiedFiles;
        private string _currentBundleName = string.Empty;
        private int _currentBundleCopiedFiles;

        private const int PreparingPercent = 10;
        private const int CopyStartPercent = 10;
        private const int CopyEndPercent = 70;
        private const int RegistryPercent = 76;
        private const int PromotePercent = 82;
        private const int TrustedPathsPercent = 88;
        private const int StartupPercent = 94;
        private const int PurgePercent = 97;
        private const int CleanupPercent = 99;

        internal InstallProgressTracker(BundleCopyPlan bundleCopyPlan, Action<SetupProgressUpdate>? progress)
        {
            _bundleCopyPlan = bundleCopyPlan;
            _progress = progress;
        }

        internal void ReportPreparingStaging()
        {
            Report(PreparingPercent, "正在准备临时安装目录…");
        }

        internal void BeginBundleCopy(string bundleName)
        {
            _currentBundleName = bundleName;
            _currentBundleCopiedFiles = 0;
            ReportCopyProgress();
        }

        internal void ReportFileCopied(string bundleName, long fileSizeBytes)
        {
            _currentBundleName = bundleName;
            _currentBundleCopiedFiles++;
            _copiedFiles++;
            _copiedBytes += Math.Max(fileSizeBytes, 0);
            ReportCopyProgress();
        }

        internal void ReportWritingInstallRegistry()
        {
            Report(RegistryPercent, "正在写入安装信息…");
        }

        internal void ReportPromotingPluginDirectory()
        {
            Report(PromotePercent, "插件文件复制完成，正在切换正式目录…");
        }

        internal void ReportUpdatingTrustedPaths()
        {
            Report(TrustedPathsPercent, "正在写入受信任位置…");
        }

        internal void ReportUpdatingStartupEntries()
        {
            Report(StartupPercent, "正在写入启动项…");
        }

        internal void ReportPurgingLegacyCopies()
        {
            Report(PurgePercent, "正在清理旧版副本…");
        }

        internal void ReportCleanup()
        {
            Report(CleanupPercent, "正在清理备份和临时目录…");
        }

        internal void ReportCompleted()
        {
            Report(100, "安装阶段完成。");
        }

        private void ReportCopyProgress()
        {
            var ratio = CalculateCopyRatio();
            var percent = CopyStartPercent +
                          (int)Math.Round((CopyEndPercent - CopyStartPercent) * ratio, MidpointRounding.AwayFromZero);
            var bundleTotalFiles = _bundleCopyPlan.BundleFileCounts.TryGetValue(_currentBundleName, out var count)
                ? count
                : 1;
            var status = _currentBundleName.Length == 0
                ? "正在复制插件文件…"
                : $"正在复制 {_currentBundleName}（{Math.Min(_currentBundleCopiedFiles, bundleTotalFiles)}/{bundleTotalFiles} 文件）";
            Report(percent, status);
        }

        private double CalculateCopyRatio()
        {
            if (_bundleCopyPlan.TotalBytes > 0)
                return Math.Clamp((double)_copiedBytes / _bundleCopyPlan.TotalBytes, 0d, 1d);
            if (_bundleCopyPlan.TotalFiles > 0)
                return Math.Clamp((double)_copiedFiles / _bundleCopyPlan.TotalFiles, 0d, 1d);
            return 1d;
        }

        private void Report(int percent, string status)
        {
            _progress?.Invoke(new SetupProgressUpdate(Math.Clamp(percent, 0, 100), status));
        }
    }
}
