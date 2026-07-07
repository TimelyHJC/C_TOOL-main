using System;
using System.Diagnostics;
using System.IO;

namespace C_toolsShared;

/// <summary>
/// 非致命诊断工具类，提供日志记录和性能追踪功能。
/// 写入 <see cref="Debug"/> 与 <see cref="Trace"/>，便于附加调试器或收集跟踪输出。
/// </summary>
public static class C_toolsDiagnostics
{
    private const string FdfDebugLogFileName = "C_tools_fdf_debug.log";
    private static readonly object FdfDebugLogSyncRoot = new();

    /// <summary>
    /// 获取 FDF 调试日志文件的完整路径。
    /// </summary>
    public static string FdfDebugLogPath => Path.Combine(Path.GetTempPath(), FdfDebugLogFileName);

    /// <summary>
    /// 记录非致命诊断消息。
    /// </summary>
    /// <param name="message">诊断消息</param>
    /// <param name="ex">可选的异常对象</param>
    public static void LogNonFatal(string message, Exception? ex = null)
    {
        var line = ex == null
            ? $"[C_TOOL] {message}"
            : $"[C_TOOL] {message}: {ex.GetType().Name}: {ex.Message}";
        Debug.WriteLine(line);
        Trace.WriteLine(line);
        TryAppendFdfDebugLog(line);
        if (ex == null)
            return;
        Debug.WriteLine(ex.ToString());
        Trace.WriteLine(ex.ToString());
        TryAppendFdfDebugLog(ex.ToString());
    }

    /// <summary>
    /// 记录性能追踪信息。
    /// </summary>
    /// <param name="phase">阶段名称（如"命令表载入"）</param>
    /// <param name="elapsedMs">耗时（毫秒）</param>
    /// <param name="detail">可选的详细信息（如行数、字节数等）</param>
    public static void LogPerf(string phase, long elapsedMs, string? detail = null)
    {
        var line = string.IsNullOrEmpty(detail)
            ? $"[C_TOOL][perf] {phase}: {elapsedMs} ms"
            : $"[C_TOOL][perf] {phase}: {elapsedMs} ms ({detail})";
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }

    private static void TryAppendFdfDebugLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line) ||
            (line.IndexOf("F_DF 调试", StringComparison.Ordinal) < 0 &&
             line.IndexOf("F_DA 调试", StringComparison.Ordinal) < 0))
        {
            return;
        }

        var timestampedLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";

        try
        {
            lock (FdfDebugLogSyncRoot)
            {
                File.AppendAllText(FdfDebugLogPath, timestampedLine);
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[C_TOOL] 写入 F_DF 调试日志失败（IO）: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[C_TOOL] 写入 F_DF 调试日志失败（权限）: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"[C_TOOL] 写入 F_DF 调试日志失败（参数）: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"[C_TOOL] 写入 F_DF 调试日志失败（不支持）: {ex.Message}");
        }
    }
}
