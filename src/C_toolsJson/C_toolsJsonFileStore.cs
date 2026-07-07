using System.Text;
using System.Text.Json;
using C_toolsShared;

namespace C_toolsJson;

public static class C_toolsJsonFileStore
{
    public static bool TryRead<T>(
        string path,
        JsonSerializerOptions options,
        string readOperationName,
        string parseOperationName,
        Action<string, Exception> logNonFatal,
        out T? value)
    {
        var json = ExceptionHelper.SafeExecuteWithLogger(
            () => File.Exists(path) ? File.ReadAllText(path) : null,
            readOperationName,
            logNonFatal,
            (string?)null);

        if (json == null)
        {
            value = default;
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, options);
            return true;
        }
        catch (JsonException ex)
        {
            logNonFatal(parseOperationName, ex);
        }
        catch (NotSupportedException ex)
        {
            logNonFatal($"{parseOperationName}（不支持的 JSON 类型）", ex);
        }

        value = default;
        return false;
    }

    public static bool TryWrite<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        string writeOperationName,
        Action<string, Exception> logNonFatal,
        string? serializeOperationName = null,
        bool rethrowOnFailure = false,
        Encoding? encoding = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, options);
            return TryWriteAllText(
                path,
                json,
                encoding,
                writeOperationName,
                logNonFatal,
                rethrowOnFailure);
        }
        catch (InvalidOperationException ex)
        {
            logNonFatal(serializeOperationName ?? $"{writeOperationName}（序列化）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
        catch (NotSupportedException ex)
        {
            logNonFatal(serializeOperationName ?? $"{writeOperationName}（序列化）", ex);
            if (rethrowOnFailure)
                throw;
            return false;
        }
    }

    private static bool TryWriteAllText(
        string path,
        string content,
        Encoding? encoding,
        string operationName,
        Action<string, Exception> logNonFatal,
        bool rethrowOnFailure)
    {
        if (!TryEnsureParentDirectory(path, operationName, logNonFatal, rethrowOnFailure))
            return false;

        return ExceptionHelper.SafeExecuteWithLogger(
            () =>
            {
                var tmp = path + ".tmp";
                if (encoding == null)
                    File.WriteAllText(tmp, content);
                else
                    File.WriteAllText(tmp, content, encoding);
                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null);
                else
                    File.Move(tmp, path);
            },
            operationName,
            logNonFatal,
            rethrowOnFailure);
    }

    private static bool TryEnsureParentDirectory(
        string path,
        string operationName,
        Action<string, Exception> logNonFatal,
        bool rethrowOnFailure)
    {
        return ExceptionHelper.SafeExecuteWithLogger(
            () =>
            {
                var directoryPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directoryPath))
                    Directory.CreateDirectory(directoryPath);
            },
            operationName,
            logNonFatal,
            rethrowOnFailure);
    }
}