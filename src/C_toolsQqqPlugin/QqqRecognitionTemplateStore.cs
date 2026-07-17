using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private const string GlobalFileName = "GlobalTemplates.json";
    private const string LegacyFolderName = "QqqRecognitionTemplates";
    private static readonly object CacheSyncRoot = new();
    private static RecognitionTemplateCacheEntry? _cache;

    internal static QqqRecognitionTemplateStoreState Load()
    {
        var path = GetFilePath();
        lock (CacheSyncRoot)
        {
            var fileState = RecognitionTemplateFileState.Create(path);
            if (_cache != null && _cache.Matches(fileState))
                return CloneState(_cache.State);

            if (!C_toolsJsonFileStore.TryRead(
                    path,
                    JsonOptionsCache.ReadRelaxedCamelCase,
                    "读取 V_QQQ 全局图框识别项",
                    "解析 V_QQQ 全局图框识别项",
                    C_toolsDiagnostics.LogNonFatal,
                    out QqqRecognitionTemplateStoreState? state) ||
                state == null)
            {
                state = TryLoadLegacySharedState() ?? new QqqRecognitionTemplateStoreState();
            }

            state.DocumentPath = "";
            state.Normalize();

            _cache = new RecognitionTemplateCacheEntry(
                RecognitionTemplateFileState.Create(path),
                CloneState(state),
                CreateStateSignature(state));
            return state;
        }
    }

    internal static bool Save(QqqRecognitionTemplateStoreState? state)
    {
        if (state == null)
            return false;

        lock (CacheSyncRoot)
        {
            try
            {
                state.Version = FileVersion;
                state.DocumentPath = "";
                state.Normalize();

                var path = GetFilePath();
                var signature = CreateStateSignature(state);
                var fileState = RecognitionTemplateFileState.Create(path);
                if (fileState.Exists &&
                    _cache != null &&
                    _cache.Matches(fileState) &&
                    string.Equals(_cache.Signature, signature, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!C_toolsJsonFileStore.TryWrite(
                        path,
                        state,
                        JsonOptionsCache.WriteIndentedCamelCase,
                        "写入 V_QQQ 全局图框识别项",
                        C_toolsDiagnostics.LogNonFatal))
                {
                    return false;
                }

                _cache = new RecognitionTemplateCacheEntry(
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

    private static QqqRecognitionTemplateStoreState? TryLoadLegacySharedState()
    {
        var legacyFolder = Path.Combine(C_toolsPaths.UserConfigFolder, LegacyFolderName);
        try
        {
            if (!Directory.Exists(legacyFolder))
                return null;

            var merged = new QqqRecognitionTemplateStoreState();
            foreach (var legacyPath in Directory.EnumerateFiles(legacyFolder, "*.json").OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!C_toolsJsonFileStore.TryRead(
                        legacyPath,
                        JsonOptionsCache.ReadRelaxedCamelCase,
                        $"读取 V_QQQ 旧图框识别项 {legacyPath}",
                        $"解析 V_QQQ 旧图框识别项 {legacyPath}",
                        C_toolsDiagnostics.LogNonFatal,
                        out QqqRecognitionTemplateStoreState? legacyState) ||
                    legacyState == null)
                {
                    continue;
                }

                legacyState.Normalize();
                MergeState(merged, legacyState);
            }

            merged.Normalize();
            return merged.Modes.Count == 0 ? null : merged;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("迁移 V_QQQ 旧图框识别项失败", ex);
            return null;
        }
    }

    private static void MergeState(QqqRecognitionTemplateStoreState target, QqqRecognitionTemplateStoreState source)
    {
        target.LastModeKey = NormalizeModeKey(source.LastModeKey);
        foreach (var sourceMode in source.Modes.Where(static x => x != null))
        {
            var modeKey = NormalizeModeKey(sourceMode.ModeKey);
            var targetMode = target.Modes.FirstOrDefault(x => string.Equals(x.ModeKey, modeKey, StringComparison.OrdinalIgnoreCase));
            if (targetMode == null)
            {
                targetMode = new QqqRecognitionTemplateModeState { ModeKey = modeKey };
                target.Modes.Add(targetMode);
            }

            foreach (var template in sourceMode.Templates.Where(static x => x != null && !string.IsNullOrWhiteSpace(x.Name)))
            {
                var name = template.Name.Trim();
                var existing = targetMode.Templates.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    targetMode.Templates.Add(new QqqRecognitionTemplateEntry
                    {
                        Name = name,
                        SizeText = (template.SizeText ?? "").Trim(),
                        IsSelected = template.IsSelected
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(existing.SizeText))
                    existing.SizeText = (template.SizeText ?? "").Trim();
                existing.IsSelected |= template.IsSelected;
            }
        }
    }

    private static string GetFilePath() =>
        Path.Combine(C_toolsPaths.UserConfigFolder, GlobalFileName);

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
            RecognitionTemplateFileState fileState,
            QqqRecognitionTemplateStoreState state,
            string signature)
        {
            FileState = fileState;
            State = state;
            Signature = signature;
        }

        public RecognitionTemplateFileState FileState { get; }
        public QqqRecognitionTemplateStoreState State { get; }
        public string Signature { get; }

        public bool Matches(RecognitionTemplateFileState fileState)
        {
            return FileState.Equals(fileState);
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
        internal bool Exists { get; }
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
