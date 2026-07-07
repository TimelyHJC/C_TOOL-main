namespace C_toolsShared;

/// <summary>
/// 供 <see cref="ModelessWindowHost{TWindow}"/> 调用的模型窗口生命周期钩子。
/// </summary>
public interface IModelessWindowHostAware
{
    /// <summary>
    /// 窗口即将由宿主显示时调用。
    /// </summary>
    void OnHostShowing();

    /// <summary>
    /// 窗口即将由宿主隐藏时调用。
    /// </summary>
    void OnHostHiding();
}
