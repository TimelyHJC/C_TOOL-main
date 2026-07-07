using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C_toolsShared;

/// <summary>持久化 <see cref="MLeaderToolSettingsDto"/> 至用户配置目录。</summary>
public static class MLeaderToolSettingsStore
{
    private const int FileVersion = 4;
    private const string FileName = "ctool_mleader_tool.json";

    private const double MinTextHeight = 0.01;
    private const double MaxTextHeight = 1000.0;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    public static MLeaderToolSettingsDto LoadOrDefault()
    {
        var json = C_toolsTextFileStore.TryReadAllText(FilePath, "读取插件引线参数");
        if (json == null)
            return DefaultDto();

        MLeaderToolSettingsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MLeaderToolSettingsDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析插件引线参数", ex);
            return DefaultDto();
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析插件引线参数（不支持的 JSON 类型）", ex);
            return DefaultDto();
        }

        if (dto == null)
            return DefaultDto();

        dto.Version = FileVersion;
        dto.LayoutPresetId = (dto.LayoutPresetId ?? "").Trim();
        dto.TextHeight = NormalizeTextHeightForStorage(dto.TextHeight);
        dto.ArrowSize = ClampArrowSize(dto.ArrowSize);
        dto.LeaderArrowColor = NormalizeColorToken(dto.LeaderArrowColor);
        dto.TextColor = NormalizeColorToken(dto.TextColor);
        dto.ArrowBlockName = dto.ArrowBlockName?.Trim() ?? "";
        dto.TextStyleName = dto.TextStyleName?.Trim() ?? "";
        dto.LeftTextAttachmentType = NormalizeAttachmentEnumName(dto.LeftTextAttachmentType);
        dto.RightTextAttachmentType = NormalizeAttachmentEnumName(dto.RightTextAttachmentType);
        dto.TextAttachmentDirection = NormalizeAttachmentDirectionEnumName(dto.TextAttachmentDirection);
        dto.MaxLeaderSegmentsPoints = ClampMaxLeaderSegmentsPoints(dto.MaxLeaderSegmentsPoints);
        dto.DoglegLength = ClampDoglegLength(dto.DoglegLength);
        dto.LandingGap = ClampLandingGap(dto.LandingGap);
        return dto;
    }

    public static void Save(MLeaderToolSettingsDto dto)
    {
        dto.Version = FileVersion;
        dto.LayoutPresetId = (dto.LayoutPresetId ?? "").Trim();
        dto.TextHeight = NormalizeTextHeightForStorage(dto.TextHeight);
        dto.ArrowSize = ClampArrowSize(dto.ArrowSize);
        dto.LeaderArrowColor = NormalizeColorToken(dto.LeaderArrowColor);
        dto.TextColor = NormalizeColorToken(dto.TextColor);
        dto.LeftTextAttachmentType = NormalizeAttachmentEnumName(dto.LeftTextAttachmentType);
        dto.RightTextAttachmentType = NormalizeAttachmentEnumName(dto.RightTextAttachmentType);
        dto.TextAttachmentDirection = NormalizeAttachmentDirectionEnumName(dto.TextAttachmentDirection);
        dto.MaxLeaderSegmentsPoints = ClampMaxLeaderSegmentsPoints(dto.MaxLeaderSegmentsPoints);
        dto.DoglegLength = ClampDoglegLength(dto.DoglegLength);
        dto.LandingGap = ClampLandingGap(dto.LandingGap);

        try
        {
            var json = JsonSerializer.Serialize(dto, WriteOptions);
            WriteAllTextAtomic(FilePath, json);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入插件引线参数（序列化）", ex);
            throw;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入插件引线参数（路径参数）", ex);
            throw;
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入插件引线参数（路径过长）", ex);
            throw;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入插件引线参数（不支持）", ex);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入插件引线参数（权限）", ex);
            throw;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入插件引线参数", ex);
            throw;
        }
    }

    /// <summary>插件 JSON 中字高 &gt; 0 时返回钳制后的覆盖值；≤0 表示沿用当前 <see cref="MLeaderStyle"/>（CMLEADERSTYLE）。</summary>
    public static bool TryGetTextHeightOverride(MLeaderToolSettingsDto dto, out double clamped)
    {
        clamped = 0;
        if (dto.TextHeight <= 1e-9)
            return false;
        clamped = ClampPositiveTextHeight(dto.TextHeight);
        return true;
    }

    private static MLeaderToolSettingsDto DefaultDto() =>
        new()
        {
            Version = FileVersion,
            LayoutPresetId = "",
            LeaderArrowColor = "",
            ArrowBlockName = "",
            ArrowSize = 0,
            TextHeight = 0,
            TextStyleName = "",
            TextColor = "",
            LeftTextAttachmentType = "",
            RightTextAttachmentType = "",
            TextAttachmentDirection = "",
            MaxLeaderSegmentsPoints = 0,
            EnableLanding = null,
            EnableDogleg = null,
            DoglegLength = 0,
            LandingGap = 0
        };

    /// <summary>空字符串表示不覆盖 CAD 样式；非空则原样保留（可写 BYLAYER 等）。</summary>
    private static string NormalizeColorToken(string? s) => (s ?? "").Trim();

    private static double ClampArrowSize(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            return 0;
        if (v > 1e6)
            return 1e6;
        return v;
    }

    /// <summary>空字符串表示不覆盖 CAD 样式的连接位置。</summary>
    private static string NormalizeAttachmentEnumName(string? s) => (s ?? "").Trim();

    /// <summary>空字符串表示不覆盖 CAD 样式的连接方向。</summary>
    private static string NormalizeAttachmentDirectionEnumName(string? s) => (s ?? "").Trim();

    private static int ClampMaxLeaderSegmentsPoints(int v)
    {
        if (v <= 0)
            return 0;
        if (v < 2)
            return 2;
        if (v > 99)
            return 99;
        return v;
    }

    private static double ClampLandingGap(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            return 0;
        if (v > 1e6)
            return 1e6;
        return v;
    }

    private static double ClampDoglegLength(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            return 0;
        if (v > 1e6)
            return 1e6;
        return v;
    }

    /// <summary>0 表示沿用样式字高；正数钳制到合法范围。</summary>
    private static double NormalizeTextHeightForStorage(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
            return 0;
        if (v < MinTextHeight)
            return MinTextHeight;
        if (v > MaxTextHeight)
            return MaxTextHeight;
        return v;
    }

    private static double ClampPositiveTextHeight(double h)
    {
        if (double.IsNaN(h) || double.IsInfinity(h))
            return MinTextHeight;
        if (h < MinTextHeight)
            return MinTextHeight;
        if (h > MaxTextHeight)
            return MaxTextHeight;
        return h;
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }
}
