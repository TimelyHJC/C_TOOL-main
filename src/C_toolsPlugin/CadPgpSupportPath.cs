using System.IO;
using System.Linq;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>将 <see cref="C_toolsPaths.UserAcadPgpPath"/> 所在目录 prepend 到「支持文件搜索路径」，保证优先加载该 acad.pgp。</summary>
internal static class CadPgpSupportPath
{
    private static int s_ensured;

    internal static void EnsureC_toolsSupportFirst()
    {
        if (Volatile.Read(ref s_ensured) != 0)
            return;

        try
        {
            C_toolsPaths.EnsureFolders();
            C_toolsPaths.TryMigrateAcadPgpIfDestinationMissing();
            var pgpDir = Path.GetDirectoryName(C_toolsPaths.UserAcadPgpPath);
            if (string.IsNullOrEmpty(pgpDir))
                return;
            var dir = Path.GetFullPath(pgpDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(dir))
                return;

            dynamic prefs = AcAp.Preferences;
            var sp = (string)(prefs.Files.SupportPath ?? "");
            var parts = sp.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            static string Norm(string p)
            {
                try
                {
                    return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    return p.TrimEnd('\\', '/');
                }
            }

            var dirNorm = Norm(dir);
            if (parts.Count > 0 && string.Equals(Norm(parts[0]), dirNorm, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref s_ensured, 1);
                return;
            }
            parts = parts.Where(p => !string.Equals(Norm(p), dirNorm, StringComparison.OrdinalIgnoreCase)).ToList();
            prefs.Files.SupportPath = dir + ";" + string.Join(";", parts);
            Volatile.Write(ref s_ensured, 1);
        }
        catch
        {
            // 无写配置权限时忽略；FindFile 仍可能命中安装目录下的 acad.pgp
        }
    }
}
