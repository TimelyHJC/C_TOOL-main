using System.IO;

namespace C_toolsDddPlugin;

/// <summary>持久化 F_DQQ 外包总尺寸的向外偏移设置。</summary>
internal static class DddOuterDimensionSettingsStore
{
    private const int FileVersion = 1;
    private const string FileName = "ctool_ddd_outer_dimension.json";
    private const double MinOffsetDistance = 0.01;
    private const double MaxOffsetDistance = 1000000.0;
    private const double DefaultOffsetDistance = 360.0;

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static DddOuterDimensionSettingsDto LoadOrDefault()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxed,
                "读取 F_DQQ 外包尺寸设置",
                "解析 F_DQQ 外包尺寸设置",
                C_toolsDiagnostics.LogNonFatal,
                out DddOuterDimensionSettingsDto? dto))
        {
            return DefaultDto();
        }

        return Normalize(dto);
    }

    internal static void Save(DddOuterDimensionSettingsDto dto)
    {
        var normalized = Normalize(dto);
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            normalized,
            JsonOptionsCache.WriteIndented,
            "写入 F_DQQ 外包尺寸设置",
            C_toolsDiagnostics.LogNonFatal,
            rethrowOnFailure: true);
    }

    private static DddOuterDimensionSettingsDto Normalize(DddOuterDimensionSettingsDto? dto)
    {
        dto ??= DefaultDto();
        dto.Version = FileVersion;
        dto.OffsetDistance = NormalizeOffsetDistance(dto.OffsetDistance);
        return dto;
    }

    private static DddOuterDimensionSettingsDto DefaultDto() =>
        new()
        {
            Version = FileVersion,
            OffsetDistance = DefaultOffsetDistance
        };

    private static double NormalizeOffsetDistance(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return DefaultOffsetDistance;

        if (value < MinOffsetDistance)
            return MinOffsetDistance;

        if (value > MaxOffsetDistance)
            return MaxOffsetDistance;

        return value;
    }
}
