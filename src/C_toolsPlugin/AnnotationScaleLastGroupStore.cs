using System.IO;

namespace C_toolsPlugin;

/// <summary>记录 F_DE 标注比例分组上次选择，避免与 V_YYY 标注样式分组记忆互相覆盖。</summary>
internal static class AnnotationScaleLastGroupStore
{
    private const int FileVersion = 1;
    private const string FileName = "F_DE_annotation_scale_last_group.json";

    private static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static string? TryGetPrefix()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxedCamelCase,
                "读取 F_DE 标注比例分组记忆",
                "解析 F_DE 标注比例分组记忆",
                C_toolsDiagnostics.LogNonFatal,
                out Dto? dto))
            return null;

        var p = dto?.Prefix?.Trim();
        return string.IsNullOrEmpty(p) ? null : p;
    }

    internal static void Save(string prefix)
    {
        var p = (prefix ?? "").Trim();
        if (p.Length == 0)
            return;

        var dto = new Dto { Version = FileVersion, Prefix = p };
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            dto,
            JsonOptionsCache.WriteIndentedCamelCase,
            "写入 F_DE 标注比例分组记忆",
            C_toolsDiagnostics.LogNonFatal);
    }

    private sealed class Dto
    {
        public int Version { get; set; }
        public string? Prefix { get; set; }
    }
}
