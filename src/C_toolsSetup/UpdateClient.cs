using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace C_toolsSetup;

internal sealed record UpdateCheckResult(
    string CurrentVersion,
    UpdateManifest Manifest,
    Uri ManifestUri,
    Uri BundleZipUri,
    Uri? SetupUri,
    bool IsNewer);

internal static class UpdateClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    internal static async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var manifestUri = ResolveManifestLocation(manifestUrl);

        await using var manifestStream = await OpenReadAsync(manifestUri, cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
            manifestStream,
            s_jsonOptions,
            cancellationToken);

        if (manifest == null)
            throw new InvalidDataException("更新清单为空。");

        ValidateManifest(manifest);

        var bundleZipUri = ResolveManifestUri(manifestUri, manifest.BundleZipUrl, "bundleZipUrl");
        var setupUri = string.IsNullOrWhiteSpace(manifest.SetupUrl)
            ? null
            : ResolveManifestUri(manifestUri, manifest.SetupUrl, "setupUrl");
        var isNewer = ProductVersionComparer.Compare(manifest.Version, currentVersion) > 0;

        return new UpdateCheckResult(
            ProductVersionComparer.NormalizeForDisplay(currentVersion),
            manifest,
            manifestUri,
            bundleZipUri,
            setupUri,
            isNewer);
    }

    internal static async Task<string> DownloadBundleZipAsync(
        UpdateCheckResult update,
        string downloadDirectory,
        Action<SetupProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadDirectory);

        var fileName = GetResourceFileName(update.BundleZipUri);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fileName = BundleInstall.PublishedPluginBundleDirectoryName2024 + ".zip";

        var targetPath = Path.Combine(downloadDirectory, fileName);
        if (update.BundleZipUri.IsFile)
        {
            await CopyLocalFileAsync(
                GetLocalFilePath(update.BundleZipUri),
                targetPath,
                progress,
                cancellationToken);
            return targetPath;
        }

        using var response = await s_httpClient.GetAsync(update.BundleZipUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);

        var buffer = new byte[128 * 1024];
        long copiedBytes = 0;
        int read;
        progress?.Invoke(new SetupProgressUpdate(0, "正在下载更新包..."));
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copiedBytes += read;

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Round(copiedBytes * 100d / totalBytes.Value, MidpointRounding.AwayFromZero);
                progress?.Invoke(new SetupProgressUpdate(Math.Clamp(percent, 0, 100), "正在下载更新包..."));
            }
        }

        progress?.Invoke(new SetupProgressUpdate(100, "更新包下载完成。"));
        return targetPath;
    }

    internal static void ValidateDownloadedBundle(string zipPath, UpdateManifest manifest)
    {
        var expectedHash = GetExpectedHash(manifest);
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            var actualHash = ComputeSha256(zipPath);
            if (!string.Equals(NormalizeHash(expectedHash), actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件校验失败，更新包可能不完整或已被替换。");
        }

        var layoutError = BundleInstall.ValidateBundleZipLayout(zipPath);
        if (!string.IsNullOrWhiteSpace(layoutError))
            throw new InvalidDataException(layoutError);

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("更新包为空。");
        }
        catch (InvalidDataException)
        {
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    private static Uri ResolveManifestLocation(string manifestLocation)
    {
        var value = (manifestLocation ?? "").Trim();
        if (value.Length == 0)
            throw new InvalidOperationException("更新地址为空。");

        if (LooksLikeLocalPath(value))
            return new Uri(Path.GetFullPath(value));

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("更新地址不是有效的网络地址或本地文件路径。");

        if (!IsSupportedResourceUri(uri))
            throw new InvalidOperationException("更新地址只支持 http、https、file 或本地共享路径。");

        return uri;
    }

    private static async Task<Stream> OpenReadAsync(Uri resourceUri, CancellationToken cancellationToken)
    {
        if (resourceUri.IsFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return File.OpenRead(GetLocalFilePath(resourceUri));
        }

        return await s_httpClient.GetStreamAsync(resourceUri, cancellationToken);
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("更新清单缺少 version。");
        if (string.IsNullOrWhiteSpace(manifest.BundleZipUrl))
            throw new InvalidDataException("更新清单缺少 bundleZipUrl。");
        if (!string.IsNullOrWhiteSpace(manifest.Cad) &&
            !string.Equals(manifest.Cad.Trim(), "2024", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("服务器更新包不是 AutoCAD 2024 版本。");
        }
    }

    private static Uri ResolveManifestUri(Uri manifestUri, string value, string propertyName)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0)
            throw new InvalidDataException($"更新清单中的 {propertyName} 为空。");

        if (LooksLikeLocalPath(value))
            return new Uri(Path.GetFullPath(value));

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!IsSupportedResourceUri(absolute))
                throw new InvalidDataException($"更新清单中的 {propertyName} 只支持 http、https、file 或本地共享路径。");

            return absolute;
        }

        if (manifestUri.IsFile)
        {
            var manifestPath = GetLocalFilePath(manifestUri);
            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(manifestDirectory))
                throw new InvalidDataException($"无法根据本地更新清单解析 {propertyName}。");

            var relativePath = value
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            return new Uri(Path.GetFullPath(Path.Combine(manifestDirectory, relativePath)));
        }

        if (!Uri.TryCreate(manifestUri, value, out var relative))
            throw new InvalidDataException($"更新清单中的 {propertyName} 不是有效 URL。");

        return relative;
    }

    private static async Task CopyLocalFileAsync(
        string sourcePath,
        string targetPath,
        Action<SetupProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("本地或共享目录中的更新包不存在。", sourcePath);

        var totalBytes = new FileInfo(sourcePath).Length;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            useAsync: true);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);

        var buffer = new byte[128 * 1024];
        long copiedBytes = 0;
        int read;
        progress?.Invoke(new SetupProgressUpdate(0, "正在复制更新包..."));
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copiedBytes += read;

            if (totalBytes > 0)
            {
                var percent = (int)Math.Round(copiedBytes * 100d / totalBytes, MidpointRounding.AwayFromZero);
                progress?.Invoke(new SetupProgressUpdate(Math.Clamp(percent, 0, 100), "正在复制更新包..."));
            }
        }

        progress?.Invoke(new SetupProgressUpdate(100, "更新包复制完成。"));
    }

    private static bool IsSupportedResourceUri(Uri uri)
    {
        return uri.IsFile ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLocalPath(string value)
    {
        if (value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return value.Length >= 3 &&
               char.IsLetter(value[0]) &&
               value[1] == ':' &&
               IsDirectorySeparator(value[2]);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == '\\' || value == '/';
    }

    private static string GetResourceFileName(Uri uri)
    {
        return uri.IsFile
            ? Path.GetFileName(GetLocalFilePath(uri))
            : Path.GetFileName(uri.LocalPath);
    }

    private static string GetLocalFilePath(Uri uri)
    {
        return uri.LocalPath;
    }

    private static string GetExpectedHash(UpdateManifest manifest)
    {
        return !string.IsNullOrWhiteSpace(manifest.BundleZipSha256)
            ? manifest.BundleZipSha256
            : manifest.Sha256;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeHash(string value)
    {
        return value
            .Trim()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
