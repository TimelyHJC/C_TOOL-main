using System.Collections.Generic;
using System.IO;

namespace C_toolsPlugin;

/// <summary>
/// 将系统配置表中的变量值保存到用户目录下的 JSON，下次打开面板时恢复。
/// 路径：<see cref="C_toolsPaths.UserConfigFolder"/>\<c>V_YYY_sysvars.json</c>。
/// </summary>
internal static class SysConfigUserStore
{
    private const int FileVersion = 1;
    private const string FileName = "V_YYY_sysvars.json";

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    /// <summary>自用户文件覆盖 <paramref name="rows"/> 中同名变量的 <see cref="SysConfigRow.Value"/>；文件不存在则不做任何事。</summary>
    internal static void TryLoadInto(IEnumerable<SysConfigRow> rows)
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxedCamelCase,
                "读取 V_YYY 用户配置 JSON",
                "解析 V_YYY 用户配置 JSON",
                C_toolsDiagnostics.LogNonFatal,
                out UserFileDto? doc))
            return;

        if (doc?.Values is null || doc.Values.Count == 0)
            return;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in doc.Values)
            map[kv.Key] = kv.Value ?? "";

        foreach (var r in rows)
        {
            if (map.TryGetValue(r.VarName, out var v))
                r.Value = v;
        }
    }

    /// <summary>将当前表格全部变量写入用户文件（覆盖写入）。</summary>
    internal static void SaveFrom(IEnumerable<SysConfigRow> rows)
    {
        C_toolsPaths.EnsureFolders();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            dict[r.VarName] = r.Value ?? "";

        var payload = new UserFileDto { Version = FileVersion, Values = dict };
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            payload,
            JsonOptionsCache.WriteIndentedCamelCase,
            "写入 V_YYY 用户配置 JSON",
            C_toolsDiagnostics.LogNonFatal);
    }

    /// <summary>若尚无用户文件，则用当前行集合生成一份（与内置默认一致或已合并后的值）。</summary>
    internal static void EnsureInitialFileIfMissing(IEnumerable<SysConfigRow> rows)
    {
        if (File.Exists(FilePath))
            return;

        SaveFrom(rows);
    }

    private sealed class UserFileDto
    {
        public int Version { get; set; }
        public Dictionary<string, string>? Values { get; set; }
    }
}
