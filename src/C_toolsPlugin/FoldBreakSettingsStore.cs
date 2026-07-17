using System.IO;

namespace C_toolsPlugin;

internal static class FoldBreakSettingsStore
{
    private const int FileVersion = 1;
    private const string FileName = "ctool_fold_break.json";
    private const double MinRatioPart = 0.001;
    private const double MaxRatioPart = 1000000.0;

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static FoldBreakSettingsDto LoadOrDefault()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxed,
                "读取 F_DK 折空符号设置",
                "解析 F_DK 折空符号设置",
                C_toolsDiagnostics.LogNonFatal,
                out FoldBreakSettingsDto? dto))
            return DefaultDto();

        return Normalize(dto);
    }

    internal static void Save(FoldBreakSettingsDto dto)
    {
        var normalized = Normalize(dto);
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            normalized,
            JsonOptionsCache.WriteIndented,
            "写入 F_DK 折空符号设置",
            C_toolsDiagnostics.LogNonFatal,
            rethrowOnFailure: true);
    }

    internal static FoldBreakSettingsDto Normalize(FoldBreakSettingsDto? dto)
    {
        dto ??= DefaultDto();
        dto.Version = FileVersion;
        dto.HorizontalLeftPart = NormalizeRatioPart(dto.HorizontalLeftPart, 1.0);
        dto.HorizontalRightPart = NormalizeRatioPart(dto.HorizontalRightPart, 7.0);
        dto.VerticalTopPart = NormalizeRatioPart(dto.VerticalTopPart, 1.0);
        dto.VerticalBottomPart = NormalizeRatioPart(dto.VerticalBottomPart, 7.0);
        dto.ColorIndex = dto.ColorIndex is >= 1 and <= 255 ? dto.ColorIndex : 8;
        return dto;
    }

    private static FoldBreakSettingsDto DefaultDto() =>
        new()
        {
            Version = FileVersion,
            HorizontalLeftPart = 1.0,
            HorizontalRightPart = 7.0,
            VerticalTopPart = 1.0,
            VerticalBottomPart = 7.0,
            ColorIndex = 8
        };

    private static double NormalizeRatioPart(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            return fallback;

        if (value < MinRatioPart)
            return MinRatioPart;

        return value > MaxRatioPart ? MaxRatioPart : value;
    }
}
