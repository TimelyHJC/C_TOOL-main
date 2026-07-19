namespace QlPlugin;

/// <summary>
/// PGP 文件路径配置（含海龙等插件）
/// </summary>
public static class PgpPaths
{
    /// <summary>
    /// 海龙插件 PGP 路径
    /// </summary>
    public static readonly string HaiLongPgp = @"D:\《海龙软件》\Sys\配置\GM\acad.pgp";

    /// <summary>
    /// 获取所有要读取的 PGP 路径（AutoCAD 默认 + 海龙等）
    /// </summary>
    public static IEnumerable<string> GetAllPgpPaths()
    {
        var autodesk = FindAutodeskPgpPath();
        if (autodesk is not null && autodesk.Length > 0) yield return autodesk;
        if (File.Exists(HaiLongPgp)) yield return HaiLongPgp;
    }

    /// <summary>
    /// 获取用于写入的主 PGP 路径（AutoCAD 默认）
    /// </summary>
    public static string? GetWritePgpPath() => FindAutodeskPgpPath();

    private static string? FindAutodeskPgpPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var autodesk = Path.Combine(appData, "Autodesk");
            if (!Directory.Exists(autodesk)) return null;
            return SearchPgpRecursive(autodesk);
        }
        catch { }
        return null;
    }

    private static string? SearchPgpRecursive(string dir, int depth = 0)
    {
        if (depth > 4) return null;
        try
        {
            var pgp = Path.Combine(dir, "acad.pgp");
            if (File.Exists(pgp)) return pgp;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var found = SearchPgpRecursive(sub, depth + 1);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }
}
