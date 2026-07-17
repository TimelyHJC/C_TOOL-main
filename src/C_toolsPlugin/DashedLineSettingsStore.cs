using System.IO;
using System.Text.Json;

namespace C_toolsPlugin;

internal static class DashedLineSettingsStore
{
    private const int FileVersion = 2;
    private const string FileName = "ctool_dashed_line.json";
    private const double DefaultLinetypeScale = 1.0;
    private const double DefaultGlobalLinetypeScale = 1.0;
    private const double MinLinetypeScale = 0.0001;
    private const double MaxLinetypeScale = 1000000.0;

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static DashedLineSettingsDto LoadOrDefault()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxed,
                "读取 F_XG 线型设置",
                "解析 F_XG 线型设置",
                C_toolsDiagnostics.LogNonFatal,
                out DashedLineSettingsDto? dto))
        {
            return DefaultDto();
        }

        return Normalize(dto);
    }

    internal static void Save(DashedLineSettingsDto dto)
    {
        var normalized = Normalize(dto);
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            normalized,
            JsonOptionsCache.WriteIndented,
            "写入 F_XG 线型设置",
            C_toolsDiagnostics.LogNonFatal,
            rethrowOnFailure: true);
    }

    internal static DashedLineSettingsDto Normalize(DashedLineSettingsDto? dto)
    {
        dto ??= DefaultDto();
        dto.Version = FileVersion;
        dto.LinetypeName = NormalizeLinetypeName(dto.LinetypeName);
        dto.LinetypeScale = NormalizeLinetypeScale(dto.LinetypeScale);
        dto.GlobalLinetypeScale = NormalizeLinetypeScale(dto.GlobalLinetypeScale, DefaultGlobalLinetypeScale);
        dto.ColorMode = NormalizeColorMode(dto.ColorMode);
        dto.ColorIndex = NormalizeColorIndex(dto.ColorMode, dto.ColorIndex);
        dto.TargetLayerName = (dto.TargetLayerName ?? "").Trim();
        return dto;
    }

    private static DashedLineSettingsDto DefaultDto() =>
        new()
        {
            Version = FileVersion,
            LinetypeName = LinetypeNames.Dashed,
            LinetypeScale = DefaultLinetypeScale,
            UsePaperSpaceUnitsForScaling = false,
            GlobalLinetypeScale = DefaultGlobalLinetypeScale,
            ColorMode = DashedLineColorModes.Keep,
            ColorIndex = null,
            TargetLayerName = ""
        };

    private static string NormalizeLinetypeName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? LinetypeNames.Dashed : trimmed;
    }

    private static double NormalizeLinetypeScale(double value) =>
        NormalizeLinetypeScale(value, DefaultLinetypeScale);

    private static double NormalizeLinetypeScale(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return fallback;

        if (value < MinLinetypeScale)
            return MinLinetypeScale;

        if (value > MaxLinetypeScale)
            return MaxLinetypeScale;

        return value;
    }

    private static string NormalizeColorMode(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.Equals(trimmed, DashedLineColorModes.ByLayer, StringComparison.OrdinalIgnoreCase))
            return DashedLineColorModes.ByLayer;
        if (string.Equals(trimmed, DashedLineColorModes.ByBlock, StringComparison.OrdinalIgnoreCase))
            return DashedLineColorModes.ByBlock;
        if (string.Equals(trimmed, DashedLineColorModes.Aci, StringComparison.OrdinalIgnoreCase))
            return DashedLineColorModes.Aci;
        return DashedLineColorModes.Keep;
    }

    private static int? NormalizeColorIndex(string colorMode, int? value)
    {
        if (!string.Equals(colorMode, DashedLineColorModes.Aci, StringComparison.Ordinal))
            return null;

        if (!value.HasValue)
            return 7;

        return value.Value is >= 1 and <= 255
            ? value.Value
            : 7;
    }
}
