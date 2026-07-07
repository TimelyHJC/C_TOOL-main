using System.IO;
using System.Text;

namespace C_toolsShared;

/// <summary>
/// 文本文件存储工具类，提供安全的文件读写操作。
/// 所有操作都包含异常处理，失败时记录日志而非抛出异常。
/// </summary>
public static class C_toolsTextFileStore
{
    /// <summary>
    /// 尝试读取文本文件的全部内容。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="operationName">操作名称（用于日志记录）</param>
    /// <returns>文件内容；如果文件不存在或读取失败则返回 null</returns>
    public static string? TryReadAllText(string path, string operationName)
    {
        return ExceptionHelper.SafeExecute(
            () => File.Exists(path) ? File.ReadAllText(path) : null,
            operationName,
            (string?)null);
    }

    /// <summary>
    /// 尝试将文本内容写入文件。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="content">要写入的内容</param>
    /// <param name="operationName">操作名称（用于日志记录）</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>如果写入成功则返回 true</returns>
    public static bool TryWriteAllText(
        string path,
        string content,
        string operationName,
        bool rethrowOnFailure = false)
    {
        return TryWriteAllText(path, content, encoding: null, operationName, rethrowOnFailure);
    }

    /// <summary>
    /// 尝试将文本内容写入文件，使用指定的编码。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="content">要写入的内容</param>
    /// <param name="encoding">文本编码；如果为 null 则使用默认编码</param>
    /// <param name="operationName">操作名称（用于日志记录）</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>如果写入成功则返回 true</returns>
    public static bool TryWriteAllText(
        string path,
        string content,
        Encoding? encoding,
        string operationName,
        bool rethrowOnFailure = false)
    {
        if (!TryEnsureParentDirectory(path, operationName, rethrowOnFailure))
            return false;

        return ExceptionHelper.SafeExecute(
            () => WriteAllText(path, content, encoding),
            operationName,
            rethrowOnFailure);
    }

    /// <summary>
    /// 尝试删除文件。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="operationName">操作名称（用于日志记录）</param>
    /// <param name="rethrowOnFailure">是否在失败时重新抛出异常</param>
    /// <returns>如果删除成功或文件不存在则返回 true</returns>
    public static bool TryDeleteFile(string path, string operationName, bool rethrowOnFailure = false)
    {
        return ExceptionHelper.SafeExecute(
            () =>
            {
                if (File.Exists(path))
                    File.Delete(path);
            },
            operationName,
            rethrowOnFailure);
    }

    private static bool TryEnsureParentDirectory(string path, string operationName, bool rethrowOnFailure)
    {
        return ExceptionHelper.SafeExecute(
            () =>
            {
                C_toolsPaths.EnsureFolders();
                var directoryPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directoryPath))
                    Directory.CreateDirectory(directoryPath);
            },
            operationName,
            rethrowOnFailure);
    }

    private static void WriteAllText(string path, string content, Encoding? encoding)
    {
        if (encoding == null)
            File.WriteAllText(path, content);
        else
            File.WriteAllText(path, content, encoding);
    }
}
