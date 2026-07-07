using System.Text.Json;
using System.Text.Json.Serialization;

namespace C_toolsJson;

/// <summary>JSON 序列化选项缓存，避免重复创建。</summary>
public static class JsonOptionsCache
{
    /// <summary>用于写入的格式化选项（缩进、CamelCase、忽略 null）。</summary>
    public static readonly JsonSerializerOptions WriteIndentedCamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>用于读取的宽松选项（CamelCase、忽略大小写、允许注释、允许尾随逗号）。</summary>
    public static readonly JsonSerializerOptions ReadRelaxedCamelCase = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>紧凑写入选项（CamelCase、无缩进）。</summary>
    public static readonly JsonSerializerOptions WriteCompactCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>用于写入的格式化选项（保持属性名原样，忽略 null）。</summary>
    public static readonly JsonSerializerOptions WriteIndented = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>用于读取的宽松选项（保持属性名原样，忽略大小写、允许注释、允许尾随逗号）。</summary>
    public static readonly JsonSerializerOptions ReadRelaxed = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>紧凑写入选项（保持属性名原样）。</summary>
    public static readonly JsonSerializerOptions WriteCompact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
