using System.Diagnostics;
using System.IO;
using System.Text;

namespace C_toolsPlugin;

/// <summary>
/// 命令表「基础」缓存键：反射/PGP/说明；不含 <see cref="LayerShortcutStore"/>（图层行始终从 JSON 即时合并）。
/// </summary>
internal static class CatalogLoadFingerprint
{
    internal static string ComputeBase()
    {
        var sb = new StringBuilder(512);

        var sw = Stopwatch.StartNew();
        sb.AppendLine(CadCommandCatalogBuilder.GetDotNetScanFingerprint());
        sb.AppendLine("native-commands|" + string.Join(",", AcadNativeCommandDescriptions.CommandNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        sw.Stop();
        C_toolsDiagnostics.LogPerf("指纹·程序集列表与 mtime", sw.ElapsedMilliseconds);

        sw.Restart();
        AppendFileMtimeLine(sb, C_toolsPaths.UserAcadPgpPath);
        AppendFileMtimeLine(sb, CommandDescriptionStore.FilePath);
        sw.Stop();
        C_toolsDiagnostics.LogPerf("指纹·附属配置 mtime(acad.pgp+说明,不含图层JSON)", sw.ElapsedMilliseconds);

        return sb.ToString();
    }

    private static void AppendFileMtimeLine(StringBuilder sb, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                sb.AppendLine($"{path}|missing");
                return;
            }

            sb.AppendLine($"{path}|{File.GetLastWriteTimeUtc(path).Ticks}");
        }
        catch
        {
            sb.AppendLine($"{path}|err");
        }
    }
}
