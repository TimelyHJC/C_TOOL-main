using System.IO;
namespace C_toolsPlugin;

/// <summary>记录上次在 V_YYY 中<strong>成功</strong>应用的 .arg 完整路径，用于重新打开窗口时恢复下拉显示。</summary>
internal static class ArgProfileLastStore
{
    private const int FileVersion = 1;
    private const string FileName = "V_YYY_last_arg_profile.json";

    private static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static string? TryGetLastPath()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxedCamelCase,
                "读取 V_YYY 上次 .arg 路径",
                "解析 V_YYY 上次 .arg 路径",
                C_toolsDiagnostics.LogNonFatal,
                out Dto? dto))
            return null;

        var p = dto?.ArgPath?.Trim();
        return string.IsNullOrEmpty(p) ? null : p;
    }

    internal static void Save(string fullPath)
    {
        var p = (fullPath ?? "").Trim();
        if (p.Length == 0)
            return;

        var dto = new Dto { Version = FileVersion, ArgPath = p };
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            dto,
            JsonOptionsCache.WriteIndentedCamelCase,
            "写入 V_YYY 上次 .arg 路径",
            C_toolsDiagnostics.LogNonFatal);
    }

    private sealed class Dto
    {
        public int Version { get; set; }
        public string? ArgPath { get; set; }
    }
}
