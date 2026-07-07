using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace C_toolsShared;

/// <summary>C_TOOL 用户数据目录；安装布局下用户可编辑文件在 User 根，插件自动数据在 User\C_TOOL。</summary>
public static class C_toolsPaths
{
    private static readonly object SnapshotSyncRoot = new();
    private static readonly object EnsureFoldersSyncRoot = new();
    private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EnsureFoldersRefreshInterval = TimeSpan.FromSeconds(2);
    private static PathSnapshot? _snapshot;
    private static string? _lastEnsuredKey;
    private static DateTime _lastEnsuredUtc;

    /// <summary>是否使用安装程序写入的自定义 UserDataRoot（与便携模式区分）。</summary>
    private static bool HasCustomUserDataRoot() => CurrentSnapshot.HasCustomUserDataRoot;

    /// <summary>
    /// 插件自动数据目录（JSON、命令说明、LISP、PGP、缓存等）。
    /// 安装布局（写过 UserDataRoot）时为 <c>…\User\C_TOOL</c>；否则为 <c>%AppData%\C_TOOL</c>。
    /// </summary>
    public static string UserConfigFolder => CurrentSnapshot.UserConfigFolder;

    /// <summary>
    /// 用户可直接查看/编辑的目录。安装布局下为 <c>…\User</c>，用于 .arg、初始化文件等；
    /// 未安装布局下与 <see cref="UserConfigFolder"/> 相同。
    /// </summary>
    public static string UserEditableFolder => CurrentSnapshot.UserEditableFolder;

    /// <summary>与 <see cref="UserConfigFolder"/> 相同；图层 <c>layer_shortcuts.json</c> / <c>c_tools_layer_shortcuts.lsp</c> 目录。</summary>
    public static string LayerShortcutsDataFolder => UserConfigFolder;

    /// <summary>
    /// 若安装程序写过 HKCU\Software\C_TOOL\UserDataRoot（例如 D:\C_tool插件\User\C_TOOL），则优先使用；
    /// 否则为 %AppData%\C_TOOL。
    /// </summary>
    public static string AppDataRoot => CurrentSnapshot.AppDataRoot;

    /// <summary>置于支持路径首位。合并后的 acad.pgp 见 <see cref="UserAcadPgpPath"/>。</summary>
    public static string SupportFolder => CurrentSnapshot.SupportFolder;

    /// <summary>
    /// 合并后的命令别名 acad.pgp，位于 <see cref="UserConfigFolder"/>，不在 Support 子目录。
    /// </summary>
    public static string UserAcadPgpPath => CurrentSnapshot.UserAcadPgpPath;

    /// <summary>
    /// 安装布局下 <see cref="AppDataRoot"/> 常为 <c>…\User\C_TOOL</c>，本目录为其上一级 <c>…\User</c>；
    /// 未使用安装路径时与 AppDataRoot 相同。
    /// </summary>
    public static string UserSiblingFolder => CurrentSnapshot.UserSiblingFolder;

    /// <summary>仅含 C_TOOL 管理块（; --- C_TOOL aliases ---）的副本，位于插件自动数据目录；不参与 AutoCAD 加载。</summary>
    public static string UserSiblingC_toolsAliasesPgpPath => CurrentSnapshot.UserSiblingC_toolsAliasesPgpPath;

    /// <summary>模型窗口布局持久化目录。</summary>
    public static string WindowStateFolder => CurrentSnapshot.WindowStateFolder;

    private static PathSnapshot CurrentSnapshot
    {
        get
        {
            var now = DateTime.UtcNow;
            var snapshot = _snapshot;
            if (snapshot != null && now - snapshot.CreatedUtc < SnapshotRefreshInterval)
                return snapshot;

            lock (SnapshotSyncRoot)
            {
                snapshot = _snapshot;
                if (snapshot != null && now - snapshot.CreatedUtc < SnapshotRefreshInterval)
                    return snapshot;

                snapshot = PathSnapshot.Create(now);
                _snapshot = snapshot;
                return snapshot;
            }
        }
    }

    public static void EnsureFolders()
    {
        var snapshot = CurrentSnapshot;
        var now = DateTime.UtcNow;
        lock (EnsureFoldersSyncRoot)
        {
            if (string.Equals(_lastEnsuredKey, snapshot.EnsureKey, StringComparison.OrdinalIgnoreCase) &&
                now - _lastEnsuredUtc < EnsureFoldersRefreshInterval)
            {
                return;
            }
        }

        Directory.CreateDirectory(snapshot.SupportFolder);
        try
        {
            Directory.CreateDirectory(snapshot.UserSiblingFolder);
            Directory.CreateDirectory(snapshot.UserEditableFolder);
            Directory.CreateDirectory(snapshot.UserConfigFolder);
            Directory.CreateDirectory(snapshot.WindowStateFolder);
        }
        catch
        {
            // 忽略无法创建上一级时
        }

        var pgpDir = Path.GetDirectoryName(snapshot.UserAcadPgpPath);
        if (!string.IsNullOrEmpty(pgpDir))
        {
            try
            {
                Directory.CreateDirectory(pgpDir);
            }
            catch
            {
                // 忽略
            }
        }

        lock (EnsureFoldersSyncRoot)
        {
            _lastEnsuredKey = snapshot.EnsureKey;
            _lastEnsuredUtc = now;
        }
    }

    /// <summary>
    /// 若 <see cref="UserAcadPgpPath"/> 尚不存在，从当前 AutoCAD 安装目录下 <c>UserDataCache\&lt;语言&gt;\Support\acad.pgp</c> 复制一次；不覆盖已有目标文件。不读取 <c>C_TOOL\Support\acad.pgp</c>。
    /// </summary>
    public static void TryMigrateAcadPgpIfDestinationMissing()
    {
        var dest = UserAcadPgpPath;
        if (File.Exists(dest))
            return;

        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            return;
        }

        var legacyRootPgp = Path.Combine(UserSiblingFolder, "acad.pgp");
        try
        {
            if (!string.Equals(legacyRootPgp, dest, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(legacyRootPgp) &&
                !File.Exists(dest))
            {
                File.Copy(legacyRootPgp, dest, overwrite: false);
                return;
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("迁移 User 根目录 acad.pgp 到 C_TOOL 失败", ex);
        }

        var oem = TryFindOemUserDataCacheAcadPgp();
        if (string.IsNullOrEmpty(oem))
            return;

        try
        {
            if (!File.Exists(dest))
                File.Copy(oem, dest, overwrite: false);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("C_toolsPaths（非关键路径操作）", ex);
        }
    }

    /// <summary>解析当前进程（acad.exe）旁 <c>UserDataCache\*\Support\acad.pgp</c>，优先当前 UI 语言、再 zh-CN、en-US、其余子目录。</summary>
    public static string? TryFindOemUserDataCacheAcadPgp()
    {
        try
        {
            var main = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(main))
                return null;
            var acadRoot = Path.GetDirectoryName(main);
            if (string.IsNullOrEmpty(acadRoot))
                return null;

            var cacheRoot = Path.Combine(acadRoot, "UserDataCache");
            if (!Directory.Exists(cacheRoot))
                return null;

            var culture = CultureInfo.CurrentUICulture.Name.Trim();
            if (culture.Length > 0)
            {
                var p = Path.Combine(cacheRoot, culture, "Support", "acad.pgp");
                if (File.Exists(p))
                    return p;
            }

            foreach (var name in new[] { "zh-CN", "en-US" })
            {
                var p = Path.Combine(cacheRoot, name, "Support", "acad.pgp");
                if (File.Exists(p))
                    return p;
            }

            foreach (var sub in Directory.GetDirectories(cacheRoot))
            {
                var p = Path.Combine(sub, "Support", "acad.pgp");
                if (File.Exists(p))
                    return p;
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("C_toolsPaths（非关键路径操作）", ex);
        }

        return null;
    }

    private sealed class PathSnapshot
    {
        private PathSnapshot(
            DateTime createdUtc,
            bool hasCustomUserDataRoot,
            string appDataRoot,
            string userSiblingFolder)
        {
            CreatedUtc = createdUtc;
            HasCustomUserDataRoot = hasCustomUserDataRoot;
            AppDataRoot = appDataRoot;
            UserSiblingFolder = userSiblingFolder;
            UserEditableFolder = hasCustomUserDataRoot ? userSiblingFolder : appDataRoot;
            UserConfigFolder = appDataRoot;
            SupportFolder = Path.Combine(appDataRoot, "Support");
            UserAcadPgpPath = Path.Combine(UserConfigFolder, "acad.pgp");
            UserSiblingC_toolsAliasesPgpPath = Path.Combine(UserConfigFolder, "C_TOOL_command_aliases.pgp");
            WindowStateFolder = Path.Combine(UserConfigFolder, "WindowState");
            EnsureKey = string.Join("|", SupportFolder, UserSiblingFolder, UserEditableFolder, UserConfigFolder, WindowStateFolder, UserAcadPgpPath);
        }

        public DateTime CreatedUtc { get; }
        public bool HasCustomUserDataRoot { get; }
        public string AppDataRoot { get; }
        public string UserSiblingFolder { get; }
        public string UserEditableFolder { get; }
        public string UserConfigFolder { get; }
        public string SupportFolder { get; }
        public string UserAcadPgpPath { get; }
        public string UserSiblingC_toolsAliasesPgpPath { get; }
        public string WindowStateFolder { get; }
        public string EnsureKey { get; }

        public static PathSnapshot Create(DateTime createdUtc)
        {
            var customRoot = TryReadCustomUserDataRoot();
            var hasCustomRoot = !string.IsNullOrWhiteSpace(customRoot);
            var appDataRoot = hasCustomRoot
                ? customRoot!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C_TOOL");
            var userSiblingFolder = hasCustomRoot ? ResolveUserSiblingFolder(appDataRoot) : appDataRoot;

            return new PathSnapshot(createdUtc, hasCustomRoot, appDataRoot, userSiblingFolder);
        }

        private static string? TryReadCustomUserDataRoot()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\C_TOOL");
                var custom = k?.GetValue("UserDataRoot") as string;
                var trimmed = custom?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    return null;

                var path = Path.GetFullPath(trimmed);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveUserSiblingFolder(string appDataRoot)
        {
            try
            {
                var root = Path.GetFullPath(appDataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var parent = Path.GetDirectoryName(root);
                return string.IsNullOrEmpty(parent) ? root : Path.GetFullPath(parent);
            }
            catch
            {
                return appDataRoot;
            }
        }
    }
}
