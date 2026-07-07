using System.Threading.Tasks;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

/// <summary>
/// AutoCAD 插件应用基类，提供统一的生命周期管理和异常处理。
/// 实现 <see cref="IExtensionApplication"/> 接口，包括首次 Idle 初始化和全局异常兜底。
/// </summary>
/// <remarks>
/// <para>
/// 继承此类时，必须重写 <see cref="OnFirstIdleCore"/> 方法，
/// 可选重写 <see cref="OnInitializeCore"/> 和 <see cref="OnTerminateCore"/> 方法。
/// </para>
/// <para>
/// 此类自动注册以下异常处理器：
/// <list type="bullet">
///   <item><description>AppDomain.UnhandledException</description></item>
///   <item><description>TaskScheduler.UnobservedTaskException</description></item>
/// </list>
/// </para>
/// </remarks>
public abstract class CadPluginAppBase : IExtensionApplication
{
    /// <summary>
    /// 获取插件日志前缀，用于区分不同插件的日志输出。
    /// 默认为空字符串，子类可重写以添加自定义前缀。
    /// </summary>
    protected virtual string PluginLogPrefix => "";

    /// <summary>
    /// 插件初始化入口点，由 AutoCAD 在加载插件时调用。
    /// 注册全局异常处理器，并在首次 Idle 时执行核心初始化。
    /// </summary>
    public void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        OnInitializeCore();
        AcAp.Idle += OnFirstIdle;
    }

    /// <summary>
    /// 插件终止入口点，由 AutoCAD 在卸载插件时调用。
    /// 注销全局异常处理器，并执行清理操作。
    /// </summary>
    public void Terminate()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        OnTerminateCore();
    }

    /// <summary>
    /// 在 <see cref="Initialize"/> 中执行的核心初始化逻辑。
    /// 子类可重写以执行需要在 Idle 之前完成的初始化操作。
    /// </summary>
    protected virtual void OnInitializeCore()
    {
    }

    /// <summary>
    /// 在首次 Idle 事件中执行的核心初始化逻辑。
    /// 子类必须重写此方法以执行插件的初始化操作。
    /// </summary>
    protected abstract void OnFirstIdleCore();

    /// <summary>
    /// 在 <see cref="Terminate"/> 中执行的清理逻辑。
    /// 子类可重写以执行资源释放操作。
    /// </summary>
    protected virtual void OnTerminateCore()
    {
    }

    /// <summary>
    /// 为消息添加插件日志前缀。
    /// </summary>
    /// <param name="message">原始消息</param>
    /// <returns>添加前缀后的消息</returns>
    protected string ScopeMessage(string message)
    {
        var prefix = PluginLogPrefix.Trim();
        return prefix.Length == 0 ? message : $"{prefix} {message}";
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = ScopeMessage("AppDomain 未处理异常");
        if (e.ExceptionObject is System.Exception ex)
            C_toolsDiagnostics.LogNonFatal(message, ex);
        else
            C_toolsDiagnostics.LogNonFatal($"{message}（非 Exception）: {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        C_toolsDiagnostics.LogNonFatal(ScopeMessage("TaskScheduler 未观察到的任务异常"), e.Exception);
        e.SetObserved();
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        AcAp.Idle -= OnFirstIdle;
        try
        {
            OnFirstIdleCore();
            C_toolsStartupMessage.TryShowOnce();
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(ScopeMessage("首次 Idle 初始化失败"), ex);
        }
    }
}
