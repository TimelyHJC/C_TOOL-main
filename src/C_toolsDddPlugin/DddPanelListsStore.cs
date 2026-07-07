using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using C_toolsShared;

namespace C_toolsDddPlugin;

/// <summary>文字标注浮窗三列表持久化：写入 <see cref="C_toolsPaths.UserConfigFolder"/> 插件自动数据目录。</summary>
internal static class DddPanelListsStore
{
    private static readonly object FileIoLock = new();

    internal const string FileName = "ddd_panel_lists.json";

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal const int SchemaVersion = 2;

    internal sealed class Document
    {
        public int SchemaVersion { get; set; } = DddPanelListsStore.SchemaVersion;

        public List<DddRemarkRow> Remarks { get; set; } = new();

        public List<DddPropRow> Props { get; set; } = new();

        public List<DddMaterialRow> Materials { get; set; } = new();

        public bool InsertWithLeader { get; set; } = true;
    }

    /// <summary>仅应在浮窗构造时调用一次：从 User 目录读入历史；之后以内存为准，不再读盘覆盖编辑。</summary>
    internal static void TryLoadInto(
        ObservableCollection<DddRemarkRow> remarks,
        ObservableCollection<DddPropRow> props,
        ObservableCollection<DddMaterialRow> materials,
        out bool insertWithLeader)
    {
        insertWithLeader = true;
        lock (FileIoLock)
        {
            var json = C_toolsTextFileStore.TryReadAllText(FilePath, "读取 ddd_panel_lists.json 失败");
            if (json == null)
                return;

            try
            {
                var doc = JsonSerializer.Deserialize<Document>(json, JsonOptionsCache.ReadRelaxed);
                if (doc == null)
                    return;

                insertWithLeader = doc.InsertWithLeader;
                ReplaceCollection(remarks, doc.Remarks);
                ReplaceCollection(props, doc.Props);
                ReplaceCollection(materials, doc.Materials);
            }
            catch (JsonException ex)
            {
                C_toolsDiagnostics.LogNonFatal("解析 ddd_panel_lists.json 失败", ex);
            }
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, List<T>? source) where T : class
    {
        while (target.Count > 0)
            target.RemoveAt(0);
        if (source == null)
            return;
        foreach (var item in source)
        {
            if (item is null)
                continue;
            target.Add(item);
        }
    }

    internal static void Save(
        IReadOnlyList<DddRemarkRow> remarks,
        IReadOnlyList<DddPropRow> props,
        IReadOnlyList<DddMaterialRow> materials,
        bool insertWithLeader)
    {
        lock (FileIoLock)
        {
            var doc = new Document
            {
                SchemaVersion = SchemaVersion,
                Remarks = remarks.Where(static r => r != null).ToList(),
                Props = props.Where(static r => r != null).ToList(),
                Materials = materials.Where(static r => r != null).ToList(),
                InsertWithLeader = insertWithLeader
            };
            var json = JsonSerializer.Serialize(doc, JsonOptionsCache.WriteIndented);
            C_toolsTextFileStore.TryWriteAllText(FilePath, json, "保存 ddd_panel_lists.json 失败");
        }
    }
}
