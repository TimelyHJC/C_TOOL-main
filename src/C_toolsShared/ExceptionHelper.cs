using System.IO;

namespace C_toolsShared;

/// <summary>
/// 统一的异常处理包装器，用于文件和路径操作。
/// 减少 catch 块重复代码，确保日志一致性。
/// </summary>
public static class ExceptionHelper
{
    /// <summary>
    /// 安全执行文件/路径操作，自动捕获并记录常见异常。
    /// </summary>
    /// <typeparam name="T">返回值类型</typeparam>
    /// <param name="action">要执行的操作</param>
    /// <param name="operationName">操作名称（用于日志）</param>
    /// <param name="defaultValue">失败时返回的默认值</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>操作结果或默认值</returns>
    public static T SafeExecute<T>(
        Func<T> action,
        string operationName,
        T defaultValue,
        bool rethrowOnFailure = false)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（路径参数）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（路径过长）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（路径格式）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（权限）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName, ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
    }

    /// <summary>
    /// 安全执行文件/路径操作（无返回值版本）。
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="operationName">操作名称（用于日志）</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>是否成功执行</returns>
    public static bool SafeExecute(
        Action action,
        string operationName,
        bool rethrowOnFailure = false)
    {
        try
        {
            action();
            return true;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（路径参数）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（路径过长）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（路径格式）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}（权限）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName, ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
    }

    /// <summary>
    /// 安全执行操作，使用自定义日志函数。
    /// </summary>
    /// <typeparam name="T">返回值类型</typeparam>
    /// <param name="action">要执行的操作</param>
    /// <param name="operationName">操作名称（用于日志）</param>
    /// <param name="logNonFatal">自定义日志函数</param>
    /// <param name="defaultValue">失败时返回的默认值</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>操作结果或默认值</returns>
    public static T SafeExecuteWithLogger<T>(
        Func<T> action,
        string operationName,
        Action<string, Exception> logNonFatal,
        T defaultValue,
        bool rethrowOnFailure = false)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            logNonFatal($"{operationName}（路径参数）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (PathTooLongException ex)
        {
            logNonFatal($"{operationName}（路径过长）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (NotSupportedException ex)
        {
            logNonFatal($"{operationName}（路径格式）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (UnauthorizedAccessException ex)
        {
            logNonFatal($"{operationName}（权限）", ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
        catch (IOException ex)
        {
            logNonFatal(operationName, ex);
            if (rethrowOnFailure)
                throw;
            return defaultValue;
        }
    }

    /// <summary>
    /// 安全执行操作（无返回值版本），使用自定义日志函数。
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="operationName">操作名称（用于日志）</param>
    /// <param name="logNonFatal">自定义日志函数</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>是否成功执行</returns>
    public static bool SafeExecuteWithLogger(
        Action action,
        string operationName,
        Action<string, Exception> logNonFatal,
        bool rethrowOnFailure = false)
    {
        try
        {
            action();
            return true;
        }
        catch (ArgumentException ex)
        {
            logNonFatal($"{operationName}（路径参数）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (PathTooLongException ex)
        {
            logNonFatal($"{operationName}（路径过长）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (NotSupportedException ex)
        {
            logNonFatal($"{operationName}（路径格式）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logNonFatal($"{operationName}（权限）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (IOException ex)
        {
            logNonFatal(operationName, ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
    }
}