using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;

namespace C_toolsQqqPlugin;

[XmlRoot("QqqSavedSheetSets")]
public sealed class QqqSavedSheetSetDocumentDto
{
    public string DocumentPath { get; set; } = "";

    [XmlArray("SheetSets")]
    [XmlArrayItem("SheetSet")]
    public List<QqqSavedSheetSetDto> SheetSets { get; set; } = new();
}

public sealed class QqqSavedSheetSetDto
{
    [XmlAttribute("name")]
    public string Name { get; set; } = "";

    public bool HasDddPanelListsSnapshot { get; set; }

    public string DddPanelListsSnapshot { get; set; } = "";

    public bool HasDddTextEditHistorySnapshot { get; set; }

    public string DddTextEditHistorySnapshot { get; set; } = "";

    [XmlArray("Frames")]
    [XmlArrayItem("Frame")]
    public List<QqqSavedSheetFrameDto> Frames { get; set; } = new();
}

public sealed class QqqSavedSheetFrameDto
{
    public int AddedOrder { get; set; }
    public string Key { get; set; } = "";
    public string LayoutName { get; set; } = "";
    public string SpaceName { get; set; } = "";
    public string FrameType { get; set; } = "";
    public string FrameName { get; set; } = "";
    public string LayerName { get; set; } = "";
    public string BlockName { get; set; } = "";
    public string RecognitionSource { get; set; } = "";
    public string HandleText { get; set; } = "";
    public double Width { get; set; }
    public double Height { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MinZ { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public double MaxZ { get; set; }
    public bool IsSelected { get; set; }
    public string PaperSize { get; set; } = "";
    public string PlotScale { get; set; } = "";
}

internal sealed class QqqSavedSheetSetDefinition
{
    internal string Name { get; set; } = "";
    internal List<FrameInfo> Frames { get; set; } = new();
    internal bool HasDddPanelListsSnapshot { get; set; }
    internal string DddPanelListsSnapshot { get; set; } = "";
    internal bool HasDddTextEditHistorySnapshot { get; set; }
    internal string DddTextEditHistorySnapshot { get; set; } = "";

    internal bool HasAttachedTextSnapshots =>
        HasDddPanelListsSnapshot || HasDddTextEditHistorySnapshot;
}

internal static class QqqSavedSheetSetStore
{
    private const string FolderName = "QqqSavedSheetSets";
    private const string GlobalFileName = "global_tad_tags.xml";
    private const string WorkingFolderName = "QqqWorkingSheetSets";
    private const string FileExtension = ".xml";
    private const string DefaultSheetSetName = "当前";
    private const string WorkingSetName = "Current";
    private const string DddPanelListsFileName = "ddd_panel_lists.json";
    private const string DddTextEditHistoryFileName = "ddd_text_edit_history.json";
    private static readonly XmlSerializer SheetSetSerializer = new(typeof(QqqSavedSheetSetDocumentDto));

    internal static IReadOnlyList<QqqSavedSheetSetDefinition> Load(string documentPath)
    {
        var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
        var globalDefinitions = LoadDefinitionsFromFile(GetGlobalFilePath(), "读取 V_QQQ 全局 TAD 标签");
        var documentDefinitions = normalizedDocumentPath.Length == 0
            ? new List<QqqSavedSheetSetDefinition>()
            : LoadDefinitionsFromFile(GetFilePath(normalizedDocumentPath), $"读取 V_QQQ TAD 标签 {normalizedDocumentPath}");

        return MergeGlobalAndDocumentDefinitions(globalDefinitions, documentDefinitions);
    }

    internal static bool Save(string documentPath, IEnumerable<QqqSavedSheetSetDefinition> definitions)
    {
        var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
        var normalizedDefinitions = NormalizeDefinitions(definitions);
        var globalDefinitions = normalizedDefinitions
            .Select(static x => new QqqSavedSheetSetDefinition
            {
                Name = x.Name
            })
            .ToList();

        var globalSaved = SaveDefinitionsToFile(
            GetGlobalFilePath(),
            "",
            globalDefinitions,
            "写入 V_QQQ 全局 TAD 标签",
            "V_QQQ 保存全局 TAD 标签失败");
        if (!globalSaved || normalizedDocumentPath.Length == 0)
            return globalSaved;

        return SaveDefinitionsToFile(
            GetFilePath(normalizedDocumentPath),
            normalizedDocumentPath,
            normalizedDefinitions,
            $"写入 V_QQQ TAD 标签 {normalizedDocumentPath}",
            "V_QQQ 保存 TAD 标签失败");
    }

    internal static IReadOnlyList<FrameInfo> LoadWorkingSet(string documentPath)
    {
        var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
        if (normalizedDocumentPath.Length == 0)
            return Array.Empty<FrameInfo>();

        var path = GetWorkingSetFilePath(normalizedDocumentPath);
        var text = C_toolsTextFileStore.TryReadAllText(path, $"读取 V_QQQ 当前图纸列表 {normalizedDocumentPath}");
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<FrameInfo>();

        try
        {
            using var reader = new StringReader(text);
            if (SheetSetSerializer.Deserialize(reader) is not QqqSavedSheetSetDocumentDto documentDto)
                return Array.Empty<FrameInfo>();

            var workingSet = documentDto.SheetSets
                .FirstOrDefault(static x => string.Equals(x.Name, WorkingSetName, StringComparison.OrdinalIgnoreCase));

            return workingSet == null
                ? Array.Empty<FrameInfo>()
                : workingSet.Frames.Select(CreateFrameInfo).ToList();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 读取当前图纸列表失败", ex);
            return Array.Empty<FrameInfo>();
        }
    }

    internal static bool SaveWorkingSet(string documentPath, IEnumerable<FrameInfo> frames)
    {
        var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
        if (normalizedDocumentPath.Length == 0)
            return false;

        var frameList = frames
            .Where(static x => x != null)
            .Select(CloneFrameInfo)
            .ToList();
        var path = GetWorkingSetFilePath(normalizedDocumentPath);

        if (frameList.Count == 0)
            return C_toolsTextFileStore.TryDeleteFile(path, $"清空 V_QQQ 当前图纸列表 {normalizedDocumentPath}");

        try
        {
            var documentDto = new QqqSavedSheetSetDocumentDto
            {
                DocumentPath = normalizedDocumentPath,
                SheetSets =
                {
                    new QqqSavedSheetSetDto
                    {
                        Name = WorkingSetName,
                        Frames = frameList.Select(CreateFrameDto).ToList()
                    }
                }
            };

            using var writer = new Utf8StringWriter();
            SheetSetSerializer.Serialize(writer, documentDto);
            return C_toolsTextFileStore.TryWriteAllText(
                path,
                writer.ToString(),
                Encoding.UTF8,
                $"写入 V_QQQ 当前图纸列表 {normalizedDocumentPath}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 保存当前图纸列表失败", ex);
            return false;
        }
    }

    internal static void CaptureTextSnapshots(QqqSavedSheetSetDefinition definition)
    {
        if (definition == null)
            return;

        definition.HasDddPanelListsSnapshot = true;
        definition.DddPanelListsSnapshot = ReadSnapshotFile(
            DddPanelListsFileName,
            "读取 DD 窗口记录文字快照失败");

        definition.HasDddTextEditHistorySnapshot = true;
        definition.DddTextEditHistorySnapshot = ReadSnapshotFile(
            DddTextEditHistoryFileName,
            "读取 F_ED 历史文字快照失败");
    }

    internal static bool RestoreTextSnapshots(QqqSavedSheetSetDefinition definition)
    {
        if (definition == null)
            return true;

        var ok = true;
        if (definition.HasDddPanelListsSnapshot)
        {
            ok &= WriteSnapshotFile(
                DddPanelListsFileName,
                definition.DddPanelListsSnapshot,
                "恢复 DD 窗口记录文字快照失败");
        }

        if (definition.HasDddTextEditHistorySnapshot)
        {
            ok &= WriteSnapshotFile(
                DddTextEditHistoryFileName,
                definition.DddTextEditHistorySnapshot,
                "恢复 F_ED 历史文字快照失败");
        }

        return ok;
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

    private static string GetGlobalFilePath()
    {
        return Path.Combine(C_toolsPaths.UserConfigFolder, FolderName, GlobalFileName);
    }

    private static string GetWorkingSetFilePath(string documentPath)
    {
        var fileName = BuildFileName(documentPath);
        return Path.Combine(C_toolsPaths.UserConfigFolder, WorkingFolderName, fileName);
    }

    private static string GetSnapshotFilePath(string fileName)
    {
        return Path.Combine(C_toolsPaths.UserConfigFolder, fileName);
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

    private static QqqSavedSheetFrameDto CreateFrameDto(FrameInfo frame)
    {
        return new QqqSavedSheetFrameDto
        {
            AddedOrder = frame.AddedOrder,
            Key = frame.Key,
            LayoutName = frame.LayoutName,
            SpaceName = frame.SpaceName,
            FrameType = frame.FrameType,
            FrameName = frame.FrameName,
            LayerName = frame.LayerName,
            BlockName = frame.BlockName,
            RecognitionSource = frame.RecognitionSource,
            HandleText = frame.HandleText,
            Width = frame.Width,
            Height = frame.Height,
            CenterX = frame.CenterX,
            CenterY = frame.CenterY,
            MinX = frame.WcsExtents.MinPoint.X,
            MinY = frame.WcsExtents.MinPoint.Y,
            MinZ = frame.WcsExtents.MinPoint.Z,
            MaxX = frame.WcsExtents.MaxPoint.X,
            MaxY = frame.WcsExtents.MaxPoint.Y,
            MaxZ = frame.WcsExtents.MaxPoint.Z,
            IsSelected = frame.IsSelected,
            PaperSize = frame.PaperSize,
            PlotScale = frame.PlotScale
        };
    }

    private static FrameInfo CreateFrameInfo(QqqSavedSheetFrameDto dto)
    {
        return new FrameInfo
        {
            AddedOrder = dto.AddedOrder,
            Key = dto.Key ?? "",
            LayoutName = dto.LayoutName ?? "",
            SpaceName = dto.SpaceName ?? "",
            FrameType = dto.FrameType ?? "",
            FrameName = dto.FrameName ?? "",
            LayerName = dto.LayerName ?? "",
            BlockName = dto.BlockName ?? "",
            RecognitionSource = dto.RecognitionSource ?? "",
            HandleText = dto.HandleText ?? "",
            Width = dto.Width,
            Height = dto.Height,
            CenterX = dto.CenterX,
            CenterY = dto.CenterY,
            WcsExtents = new Extents3d(
                new Point3d(dto.MinX, dto.MinY, dto.MinZ),
                new Point3d(dto.MaxX, dto.MaxY, dto.MaxZ)),
            IsSelected = dto.IsSelected,
            PaperSize = string.IsNullOrWhiteSpace(dto.PaperSize) ? "自动匹配" : dto.PaperSize,
            PlotScale = string.IsNullOrWhiteSpace(dto.PlotScale) ? "自定义" : dto.PlotScale,
            Status = "待打印",
            OutputFile = ""
        };
    }

    private static List<QqqSavedSheetSetDefinition> LoadDefinitionsFromFile(string path, string operationName)
    {
        var text = C_toolsTextFileStore.TryReadAllText(path, operationName);
        if (string.IsNullOrWhiteSpace(text))
            return new List<QqqSavedSheetSetDefinition>();

        try
        {
            using var reader = new StringReader(text);
            if (SheetSetSerializer.Deserialize(reader) is not QqqSavedSheetSetDocumentDto documentDto)
                return new List<QqqSavedSheetSetDefinition>();

            return documentDto.SheetSets
                .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(static x => new QqqSavedSheetSetDefinition
                {
                    Name = x.Name.Trim(),
                    HasDddPanelListsSnapshot = x.HasDddPanelListsSnapshot,
                    DddPanelListsSnapshot = x.DddPanelListsSnapshot ?? "",
                    HasDddTextEditHistorySnapshot = x.HasDddTextEditHistorySnapshot,
                    DddTextEditHistorySnapshot = x.DddTextEditHistorySnapshot ?? "",
                    Frames = x.Frames.Select(CreateFrameInfo).ToList()
                })
                .ToList();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName + "失败", ex);
            return new List<QqqSavedSheetSetDefinition>();
        }
    }

    private static List<QqqSavedSheetSetDefinition> MergeGlobalAndDocumentDefinitions(
        IReadOnlyList<QqqSavedSheetSetDefinition> globalDefinitions,
        IReadOnlyList<QqqSavedSheetSetDefinition> documentDefinitions)
    {
        var result = new List<QqqSavedSheetSetDefinition>();
        if (globalDefinitions.Count == 0)
        {
            result.Add(new QqqSavedSheetSetDefinition { Name = DefaultSheetSetName });
        }
        else
        {
            result.AddRange(globalDefinitions.Select(CloneDefinitionWithoutFrames));
        }

        foreach (var documentDefinition in documentDefinitions.Where(static x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            var existing = result.FirstOrDefault(x => string.Equals(x.Name, documentDefinition.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                result.Add(CloneDefinition(documentDefinition));
                continue;
            }

            existing.HasDddPanelListsSnapshot = documentDefinition.HasDddPanelListsSnapshot;
            existing.DddPanelListsSnapshot = documentDefinition.DddPanelListsSnapshot;
            existing.HasDddTextEditHistorySnapshot = documentDefinition.HasDddTextEditHistorySnapshot;
            existing.DddTextEditHistorySnapshot = documentDefinition.DddTextEditHistorySnapshot;
            existing.Frames = documentDefinition.Frames.Select(CloneFrameInfo).ToList();
        }

        return NormalizeDefinitions(result);
    }

    private static List<QqqSavedSheetSetDefinition> NormalizeDefinitions(IEnumerable<QqqSavedSheetSetDefinition> definitions)
    {
        var result = new List<QqqSavedSheetSetDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Where(static x => x != null && !string.IsNullOrWhiteSpace(x.Name)))
        {
            var name = definition.Name.Trim();
            if (!seen.Add(name))
                continue;

            result.Add(new QqqSavedSheetSetDefinition
            {
                Name = name,
                HasDddPanelListsSnapshot = definition.HasDddPanelListsSnapshot,
                DddPanelListsSnapshot = definition.DddPanelListsSnapshot ?? "",
                HasDddTextEditHistorySnapshot = definition.HasDddTextEditHistorySnapshot,
                DddTextEditHistorySnapshot = definition.DddTextEditHistorySnapshot ?? "",
                Frames = definition.Frames.Select(CloneFrameInfo).ToList()
            });
        }

        if (result.Count == 0)
            result.Add(new QqqSavedSheetSetDefinition { Name = DefaultSheetSetName });

        return result;
    }

    private static bool SaveDefinitionsToFile(
        string path,
        string documentPath,
        IEnumerable<QqqSavedSheetSetDefinition> definitions,
        string operationName,
        string logMessage)
    {
        try
        {
            var documentDto = new QqqSavedSheetSetDocumentDto
            {
                DocumentPath = documentPath,
                SheetSets = definitions
                    .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
                    .Select(static x => new QqqSavedSheetSetDto
                    {
                        Name = x.Name.Trim(),
                        HasDddPanelListsSnapshot = x.HasDddPanelListsSnapshot,
                        DddPanelListsSnapshot = x.DddPanelListsSnapshot ?? "",
                        HasDddTextEditHistorySnapshot = x.HasDddTextEditHistorySnapshot,
                        DddTextEditHistorySnapshot = x.DddTextEditHistorySnapshot ?? "",
                        Frames = x.Frames.Select(CreateFrameDto).ToList()
                    })
                    .ToList()
            };

            using var writer = new Utf8StringWriter();
            SheetSetSerializer.Serialize(writer, documentDto);
            return C_toolsTextFileStore.TryWriteAllText(
                path,
                writer.ToString(),
                Encoding.UTF8,
                operationName);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(logMessage, ex);
            return false;
        }
    }

    private static QqqSavedSheetSetDefinition CloneDefinition(QqqSavedSheetSetDefinition source)
    {
        return new QqqSavedSheetSetDefinition
        {
            Name = source.Name,
            HasDddPanelListsSnapshot = source.HasDddPanelListsSnapshot,
            DddPanelListsSnapshot = source.DddPanelListsSnapshot,
            HasDddTextEditHistorySnapshot = source.HasDddTextEditHistorySnapshot,
            DddTextEditHistorySnapshot = source.DddTextEditHistorySnapshot,
            Frames = source.Frames.Select(CloneFrameInfo).ToList()
        };
    }

    private static QqqSavedSheetSetDefinition CloneDefinitionWithoutFrames(QqqSavedSheetSetDefinition source)
    {
        return new QqqSavedSheetSetDefinition
        {
            Name = source.Name
        };
    }

    private static FrameInfo CloneFrameInfo(FrameInfo source)
    {
        return new FrameInfo
        {
            AddedOrder = source.AddedOrder,
            Key = source.Key,
            LayoutName = source.LayoutName,
            SpaceName = source.SpaceName,
            FrameType = source.FrameType,
            FrameName = source.FrameName,
            LayerName = source.LayerName,
            BlockName = source.BlockName,
            RecognitionSource = source.RecognitionSource,
            HandleText = source.HandleText,
            Width = source.Width,
            Height = source.Height,
            CenterX = source.CenterX,
            CenterY = source.CenterY,
            WcsExtents = source.WcsExtents,
            IsSelected = source.IsSelected,
            PaperSize = source.PaperSize,
            PlotScale = source.PlotScale,
            Status = source.Status,
            OutputFile = source.OutputFile
        };
    }

    private static string ReadSnapshotFile(string fileName, string operationName)
    {
        return C_toolsTextFileStore.TryReadAllText(GetSnapshotFilePath(fileName), operationName) ?? "";
    }

    private static bool WriteSnapshotFile(string fileName, string? content, string operationName)
    {
        var path = GetSnapshotFilePath(fileName);
        if (string.IsNullOrWhiteSpace(content))
            return C_toolsTextFileStore.TryDeleteFile(path, operationName);

        return C_toolsTextFileStore.TryWriteAllText(path, content ?? "", operationName);
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
