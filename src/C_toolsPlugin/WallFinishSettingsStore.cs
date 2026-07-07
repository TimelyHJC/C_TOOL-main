using System.IO;
using System.Text.Json;

namespace C_toolsPlugin;

/// <summary>持久化 F_WCC 的完成面图层设置。</summary>
internal static class WallFinishSettingsStore
{
    private const int FileVersion = 2;
    private const string FileName = "ctool_wall_finish.json";
    private const double MinOffsetDistance = 0.01;
    private const double MaxOffsetDistance = 1000000.0;
    private const double DefaultOffsetDistance = 100.0;
    private const string QuickModeName = "Quick";
    private const string StandardModeName = "Standard";

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static WallFinishSettingsDto LoadOrDefault()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxed,
                "读取 F_WCC 完成面设置",
                "解析 F_WCC 完成面设置",
                C_toolsDiagnostics.LogNonFatal,
                out WallFinishSettingsDto? dto))
        {
            return DefaultDto();
        }

        return Normalize(dto);
    }

    internal static void Save(WallFinishSettingsDto dto)
    {
        var normalized = Normalize(dto);
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            normalized,
            JsonOptionsCache.WriteIndented,
            "写入 F_WCC 完成面设置",
            C_toolsDiagnostics.LogNonFatal,
            rethrowOnFailure: true);
    }

    private static WallFinishSettingsDto Normalize(WallFinishSettingsDto? dto)
    {
        dto ??= DefaultDto();
        dto.Version = FileVersion;
        dto.OffsetDistance = NormalizeOffsetDistance(dto.OffsetDistance);
        dto.ColorIndex = NormalizeColorIndex(dto.ColorIndex);
        dto.TargetLayerName = (dto.TargetLayerName ?? "").Trim();
        dto.Mode = NormalizeMode(dto.Mode);
        return dto;
    }

    private static WallFinishSettingsDto DefaultDto() =>
        new()
        {
            Version = FileVersion,
            OffsetDistance = DefaultOffsetDistance,
            ColorIndex = null,
            TargetLayerName = "",
            Mode = QuickModeName
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

    private static int? NormalizeColorIndex(int? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value is >= 1 and <= 255
            ? value.Value
            : null;
    }

    private static string NormalizeMode(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return string.Equals(trimmed, StandardModeName, StringComparison.OrdinalIgnoreCase)
            ? StandardModeName
            : QuickModeName;
    }
}
