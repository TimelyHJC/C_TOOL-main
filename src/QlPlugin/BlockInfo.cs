namespace QlPlugin;

/// <summary>
/// 图块信息，用于在列表中显示
/// </summary>
public class BlockInfo
{
    /// <summary>
    /// 图块名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 图块内实体数量（直接包含的实体数量）
    /// </summary>
    public int EntityCount { get; set; }

    /// <summary>
    /// 图块引用数量（在图纸中被使用的次数）
    /// </summary>
    public int ReferenceCount { get; set; }

    /// <summary>
    /// 是否为匿名块（动态块等）
    /// </summary>
    public bool IsAnonymous { get; set; }

    /// <summary>
    /// 估算大小（实体数 × 引用数），用于排序
    /// </summary>
    public long EstimatedSize => EntityCount * Math.Max(1, ReferenceCount);

    /// <summary>
    /// 估算大小（MB），基于实体和引用的粗略估算
    /// </summary>
    public double EstimatedSizeMb => (EntityCount * Math.Max(1, ReferenceCount) * 512.0) / (1024 * 1024);
}
