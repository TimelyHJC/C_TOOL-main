namespace C_toolsShared;

/// <summary>
/// 为模型窗口提供持久化位置与尺寸的存储键。
/// </summary>
public interface IModelessWindowPlacement
{
    /// <summary>
    /// 当前窗口在用户配置目录中的唯一布局键。
    /// </summary>
    string PlacementKey { get; }
}
