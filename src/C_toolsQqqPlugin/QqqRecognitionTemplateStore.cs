using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using C_toolsShared;

namespace C_toolsQqqPlugin;

internal sealed class QqqRecognitionTemplateEntry
{
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public bool IsSelected { get; set; }
}

internal sealed class QqqRecognitionTemplateModeState
{
    public string ModeKey { get; set; } = "";
    public List<QqqRecognitionTemplateEntry> Templates { get; set; } = new();
}

internal sealed class QqqRecognitionTemplateStoreState
{
    public int Version { get; set; } = 1;
    public string DocumentPath { get; set; } = "";
    public string LastModeKey { get; set; } = QqqRecognitionTemplateStore.ModeBlock;
    public List<QqqRecognitionTemplateModeState> Modes { get; set; } = new();

    internal IReadOnlyList<QqqRecognitionTemplateEntry> GetTemplates(string? modeKey)
    {
        var key = QqqRecognitionTemplateStore.NormalizeModeKey(modeKey);
        return Modes.FirstOrDefault(x => string.Equals(x.ModeKey, key, StringComparison.OrdinalIgnoreCase))
                   ?.Templates
                   ?.Select(static x => new QqqRecognitionTemplateEntry
                   {
                       Name = x.Name,
                       SizeText = x.SizeText,
                       IsSelected = x.IsSelected
                   })
                   .ToList() ??
               new List<QqqRecognitionTemplateEntry>();
    }

    internal void SetTemplates(string? modeKey, IEnumerable<QqqRecognitionTemplateEntry>? templates)
    {
        var key = QqqRecognitionTemplateStore.NormalizeModeKey(modeKey);
        var normalizedTemplates = NormalizeTemplates(templates).ToList();
        var existing = Modes.FirstOrDefault(x => string.Equals(x.ModeKey, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new QqqRecognitionTemplateModeState { ModeKey = key };
            Modes.Add(existing);
        }

        existing.Templates = normalizedTemplates;
    }

    internal void Normalize()
    {
        LastModeKey = QqqRecognitionTemplateStore.NormalizeModeKey(LastModeKey);

        Modes = Modes
            .Where(static x => x != null)
            .Select(x => new QqqRecognitionTemplateModeState
            {
                ModeKey = QqqRecognitionTemplateStore.NormalizeModeKey(x.ModeKey),
                Templates = NormalizeTemplates(x.Templates).ToList()
            })
            .GroupBy(static x => x.ModeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static x => x.First())
            .OrderBy(static x => x.ModeKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<QqqRecognitionTemplateEntry> NormalizeTemplates(IEnumerable<QqqRecognitionTemplateEntry>? templates)
    {
        return (templates ?? Enumerable.Empty<QqqRecognitionTemplateEntry>())
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(static x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static x =>
            {
                var first = x.First();
                return new QqqRecognitionTemplateEntry
                {
                    Name = first.Name.Trim(),
                    SizeText = (first.SizeText ?? "").Trim(),
                    IsSelected = first.IsSelected
                };
            })
            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase);
    }
}

internal static class QqqRecognitionTemplateStore
{
    internal const string ModeBlock = "Block";
    internal const string ModeLayer = "Layer";
    internal const string ModeViewport = "Viewport";

    private const int FileVersion = 1;
    private const string FolderName = "QqqRecognitionTemplates";
    private const string FileExtension = ".json";
    private static readonly object CacheSyncRoot = new();
    private static RecognitionTemplateCacheEntry? _cache;

    internal static QqqRecognitionTemplateStoreState Load(string? documentPath)
    {
        var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
        if (normalizedDocumentPath.Length == 0)
            return new QqqRecognitionTemplateStoreState();

        var path = GetFilePath(normalizedDocumentPath);
        lock (CacheSyncRoot)
        {
            var fileState = RecognitionTemplateFileState.Create(path);
            if (_cache != null && _cache.Matches(normalizedDocumentPath, fileState))
                return CloneState(_cache.State);

            if (!C_toolsJsonFileStore.TryRead(
                    path,
                    JsonOptionsCache.ReadRelaxedCamelCase,
                    $"读取 V_QQQ 图框识别项 {normalizedDocumentPath}",
                    $"解析 V_QQQ 图框识别项 {normalizedDocumentPath}",
                    C_toolsDiagnostics.LogNonFatal,
                    out QqqRecognitionTemplateStoreState? state) ||
                state == null)
            {
                state = new QqqRecognitionTemplateStoreState { DocumentPath = normalizedDocumentPath };
            }

            state.DocumentPath = normalizedDocumentPath;
            state.Normalize();

            _cache = new RecognitionTemplateCacheEntry(
                normalizedDocumentPath,
                RecognitionTemplateFileState.Create(path),
                CloneState(state),
                CreateStateSignature(state));
            return state;
        }
    }

    internal static bool Save(string? documentPath, QqqRecognitionTemplateStoreState? state)
    {
        var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
        if (normalizedDocumentPath.Length == 0 || state == null)
            return false;

        lock (CacheSyncRoot)
        {
            try
            {
                state.Version = FileVersion;
                state.DocumentPath = normalizedDocumentPath;
                state.Normalize();

                var path = GetFilePath(normalizedDocumentPath);
                var signature = CreateStateSignature(state);
                var fileState = RecognitionTemplateFileState.Create(path);
                if (_cache != null &&
                    _cache.Matches(normalizedDocumentPath, fileState) &&
                    string.Equals(_cache.Signature, signature, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!C_toolsJsonFileStore.TryWrite(
                        path,
                        state,
                        JsonOptionsCache.WriteIndentedCamelCase,
                        $"写入 V_QQQ 图框识别项 {normalizedDocumentPath}",
                        C_toolsDiagnostics.LogNonFatal))
                {
                    return false;
                }

                _cache = new RecognitionTemplateCacheEntry(
                    normalizedDocumentPath,
                    RecognitionTemplateFileState.Create(path),
                    CloneState(state),
                    signature);
                return true;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("V_QQQ 保存图框识别项失败", ex);
                return false;
            }
        }
    }

    internal static string NormalizeModeKey(string? modeKey)
    {
        var key = (modeKey ?? "").Trim();
        if (string.Equals(key, ModeLayer, StringComparison.OrdinalIgnoreCase))
            return ModeLayer;
        if (string.Equals(key, ModeViewport, StringComparison.OrdinalIgnoreCase))
            return ModeViewport;
        return ModeBlock;
    }

    private static string NormalizeDocumentPath(string? documentPath)
    {
        var value = (documentPath ?? "").Trim();
        if (value.Length == 0)
            return "";

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    private static string GetFilePath(string documentPath)
    {
        var fileName = BuildFileName(documentPath);
        return Path.Combine(C_toolsPaths.UserConfigFolder, FolderName, fileName);
    }

    private static string BuildFileName(string documentPath)
    {
        var drawingName = Path.GetFileNameWithoutExtension(documentPath);
        if (string.IsNullOrWhiteSpace(drawingName))
            drawingName = "Drawing";

        drawingName = SanitizeFileName(drawingName.Trim());
        return $"{drawingName}_{ComputeStableHash(documentPath)}{FileExtension}";
    }

    private static string ComputeStableHash(string value)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = sha256.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash.Take(8))
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }

    private static QqqRecognitionTemplateStoreState CloneState(QqqRecognitionTemplateStoreState source)
    {
        return new QqqRecognitionTemplateStoreState
        {
            Version = source.Version,
            DocumentPath = source.DocumentPath,
            LastModeKey = source.LastModeKey,
            Modes = source.Modes
                .Where(static x => x != null)
                .Select(static x => new QqqRecognitionTemplateModeState
                {
                    ModeKey = x.ModeKey,
                    Templates = (x.Templates ?? new List<QqqRecognitionTemplateEntry>())
                        .Where(static template => template != null)
                        .Select(static template => new QqqRecognitionTemplateEntry
                        {
                            Name = template.Name,
                            SizeText = template.SizeText,
                            IsSelected = template.IsSelected
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static string CreateStateSignature(QqqRecognitionTemplateStoreState state)
    {
        try
        {
            return JsonSerializer.Serialize(state, JsonOptionsCache.WriteIndentedCamelCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 生成图框识别项缓存签名失败", ex);
            return "";
        }
    }

    private sealed class RecognitionTemplateCacheEntry
    {
        public RecognitionTemplateCacheEntry(
            string documentPath,
            RecognitionTemplateFileState fileState,
            QqqRecognitionTemplateStoreState state,
            string signature)
        {
            DocumentPath = documentPath;
            FileState = fileState;
            State = state;
            Signature = signature;
        }

        public string DocumentPath { get; }
        public RecognitionTemplateFileState FileState { get; }
        public QqqRecognitionTemplateStoreState State { get; }
        public string Signature { get; }

        public bool Matches(string documentPath, RecognitionTemplateFileState fileState)
        {
            return string.Equals(DocumentPath, documentPath, StringComparison.OrdinalIgnoreCase) &&
                   FileState.Equals(fileState);
        }
    }

    private readonly struct RecognitionTemplateFileState : IEquatable<RecognitionTemplateFileState>
    {
        private RecognitionTemplateFileState(string path, bool exists, DateTime lastWriteTimeUtc, long length)
        {
            Path = path;
            Exists = exists;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Length = length;
        }

        private string Path { get; }
        private bool Exists { get; }
        private DateTime LastWriteTimeUtc { get; }
        private long Length { get; }

        public static RecognitionTemplateFileState Create(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return new RecognitionTemplateFileState(path, exists: false, DateTime.MinValue, length: 0);

                return new RecognitionTemplateFileState(path, exists: true, info.LastWriteTimeUtc, info.Length);
            }
            catch
            {
                return new RecognitionTemplateFileState(path, exists: File.Exists(path), DateTime.MinValue, length: 0);
            }
        }

        public bool Equals(RecognitionTemplateFileState other)
        {
            return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
                   Exists == other.Exists &&
                   LastWriteTimeUtc == other.LastWriteTimeUtc &&
                   Length == other.Length;
        }

        public override bool Equals(object? obj)
        {
            return obj is RecognitionTemplateFileState other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Path);
                hash = (hash * 397) ^ Exists.GetHashCode();
                hash = (hash * 397) ^ LastWriteTimeUtc.GetHashCode();
                hash = (hash * 397) ^ Length.GetHashCode();
                return hash;
            }
        }
    }
}
