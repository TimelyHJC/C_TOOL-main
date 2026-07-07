using System.IO;
using System.Linq;
using C_toolsJson;

namespace C_toolsDddPlugin;

internal static class DddTextEditHistoryStore
{
    private static readonly object FileIoLock = new();

    internal const string FileName = "ddd_text_edit_history.json";
    internal const int SchemaVersion = 1;
    internal const int MaxEntryCount = 200;

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static IReadOnlyList<DddTextEditHistoryRecord> Load()
    {
        lock (FileIoLock)
        {
            if (!C_toolsJsonFileStore.TryRead(
                    FilePath,
                    JsonOptionsCache.ReadRelaxed,
                    "读取 ddd_text_edit_history.json 失败",
                    "解析 ddd_text_edit_history.json 失败",
                    C_toolsDiagnostics.LogNonFatal,
                    out Document? document) ||
                document == null)
            {
                return Array.Empty<DddTextEditHistoryRecord>();
            }

            return Sanitize(document.Entries);
        }
    }

    internal static void Save(IReadOnlyList<DddTextEditHistoryRecord> entries)
    {
        lock (FileIoLock)
        {
            var document = new Document
            {
                SchemaVersion = SchemaVersion,
                Entries = Sanitize(entries)
            };

            C_toolsJsonFileStore.TryWrite(
                FilePath,
                document,
                JsonOptionsCache.WriteIndented,
                "保存 ddd_text_edit_history.json 失败",
                C_toolsDiagnostics.LogNonFatal);
        }
    }

    private static List<DddTextEditHistoryRecord> Sanitize(IEnumerable<DddTextEditHistoryRecord>? entries)
    {
        var pending = new Dictionary<string, DddTextEditHistoryRecord>(StringComparer.Ordinal);

        foreach (var entry in entries ?? Array.Empty<DddTextEditHistoryRecord>())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Text))
                continue;

            var time = entry.UpdatedAtUtc == default ? DateTime.UtcNow : entry.UpdatedAtUtc;
            if (pending.TryGetValue(entry.Text, out var existing))
            {
                if (time <= existing.UpdatedAtUtc)
                    continue;
            }

            pending[entry.Text] = new DddTextEditHistoryRecord
            {
                Text = entry.Text,
                UpdatedAtUtc = time
            };
        }

        return pending.Values
            .OrderByDescending(static entry => entry.UpdatedAtUtc)
            .Take(MaxEntryCount)
            .ToList();
    }

    internal sealed class Document
    {
        public int SchemaVersion { get; set; } = DddTextEditHistoryStore.SchemaVersion;

        public List<DddTextEditHistoryRecord> Entries { get; set; } = new();
    }
}

internal sealed class DddTextEditHistoryRecord
{
    public string Text { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
