using System.IO;
using System.Text.Json;

namespace C_toolsPlugin;

/// <summary>持久化 F_SQT 的墙体图层与宽度设置。</summary>
internal static class QuickWallSettingsStore
{
    private const int FileVersion = 5;
    private const string FileName = "ctool_quick_wall.json";
    private const double MinWidth = 0.01;
    private const double MaxWidth = 1000000.0;
    private const double DefaultPrimaryWidth = 240.0;

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static QuickWallSettingsDto LoadOrDefault()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxed,
                "读取 F_SQT 墙体设置",
                "解析 F_SQT 墙体设置",
                C_toolsDiagnostics.LogNonFatal,
                out QuickWallSettingsDto? dto))
            return DefaultDto();

        return Normalize(dto);
    }

    internal static void Save(QuickWallSettingsDto dto)
    {
        var normalized = Normalize(dto);

        C_toolsJsonFileStore.TryWrite(
            FilePath,
            normalized,
            JsonOptionsCache.WriteIndented,
            "写入 F_SQT 墙体设置",
            C_toolsDiagnostics.LogNonFatal,
            rethrowOnFailure: true);
    }

    private static QuickWallSettingsDto Normalize(QuickWallSettingsDto? dto)
    {
        dto ??= DefaultDto();
        var sourceVersion = dto.Version;
        dto.Version = FileVersion;
        dto.WallLayerName = (dto.WallLayerName ?? "").Trim();
        dto.ColorIndex = NormalizeColorIndex(dto.ColorIndex);
        dto.PrimaryWidth = NormalizePrimaryWidth(dto.PrimaryWidth);
        dto.SecondaryWidth = NormalizeSecondaryWidth(dto.SecondaryWidth);
        dto.UseSecondaryWidth = NormalizeUseSecondaryWidth(dto.UseSecondaryWidth, dto.SecondaryWidth, sourceVersion);
        return dto;
    }

    private static QuickWallSettingsDto DefaultDto() =>
        new()
        {
            Version = FileVersion,
            WallLayerName = "",
            ColorIndex = null,
            UseSecondaryWidth = false,
            PrimaryWidth = DefaultPrimaryWidth,
            SecondaryWidth = null
        };

    private static double NormalizePrimaryWidth(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return DefaultPrimaryWidth;

        if (value < MinWidth)
            return MinWidth;

        if (value > MaxWidth)
            return MaxWidth;

        return value;
    }

    private static double? NormalizeSecondaryWidth(double? value)
    {
        if (!value.HasValue)
            return null;

        var raw = value.Value;
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0)
            return null;

        if (raw < MinWidth)
            return MinWidth;

        if (raw > MaxWidth)
            return MaxWidth;

        return raw;
    }

    private static int? NormalizeColorIndex(int? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value is >= 1 and <= 255 ? value.Value : null;
    }

    private static bool NormalizeUseSecondaryWidth(bool useSecondaryWidth, double? secondaryWidth, int sourceVersion)
    {
        if (!secondaryWidth.HasValue)
            return false;

        if (sourceVersion < FileVersion)
            return true;

        return useSecondaryWidth;
    }
}
