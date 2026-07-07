using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsPlugin;

/// <summary>从图纸中拾取的 <see cref="Hatch"/> 图案/比例/角度，序列化进 <c>layer_shortcuts.json</c> 的 <c>hatchStyle</c>。</summary>
internal sealed class HatchStyleSnapshot
{
    [JsonPropertyName("patternName")]
    public string PatternName { get; set; } = C_toolsConstants.DefaultHatchPattern;

    [JsonPropertyName("scale")]
    public double Scale { get; set; } = C_toolsConstants.DefaultHatchScale;

    /// <summary>角度（度），与界面一致；写入 <c>HPANG</c> 时换算弧度。</summary>
    [JsonPropertyName("angleDegrees")]
    public double AngleDegrees { get; set; }

    public static HatchStyleSnapshot FromHatch(Hatch h)
    {
        var name = (h.PatternName ?? "").Trim();
        if (name.Length == 0)
            name = "SOLID";

        var scale = h.PatternScale;
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = C_toolsConstants.DefaultHatchScale;

        var deg = h.PatternAngle * (180.0 / Math.PI);
        return new HatchStyleSnapshot { PatternName = name, Scale = scale, AngleDegrees = deg };
    }

    public static HatchStyleSnapshot Defaults() => new();

    public static HatchStyleSnapshot FromLegacyStrings(string? pattern, string? scaleText, string? angleText)
    {
        var p = string.IsNullOrWhiteSpace(pattern) ? C_toolsConstants.DefaultHatchPattern : pattern!.Trim();
        var scale = TryParseDouble(scaleText, C_toolsConstants.DefaultHatchScale);
        if (scale <= 0)
            scale = C_toolsConstants.DefaultHatchScale;
        var deg = TryParseDouble(angleText, 0.0);
        return new HatchStyleSnapshot { PatternName = p, Scale = scale, AngleDegrees = deg };
    }

    private static double TryParseDouble(string? text, double fallback)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return fallback;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptionsCache.WriteIndented);

    public static HatchStyleSnapshot? TryParseJson(string? json)
    {
        var s = (json ?? "").Trim();
        if (s.Length == 0)
            return null;
        try
        {
            return JsonSerializer.Deserialize<HatchStyleSnapshot>(s, JsonOptionsCache.ReadRelaxed);
        }
        catch
        {
            return null;
        }
    }

    public string FormatDisplay()
    {
        var scaleStr = Scale.ToString(CultureInfo.InvariantCulture);
        var angStr = AngleDegrees.ToString(CultureInfo.InvariantCulture);
        return $"{PatternName} · {scaleStr} · {angStr}°";
    }
}
