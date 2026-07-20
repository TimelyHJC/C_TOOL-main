using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.PlottingServices;
using C_toolsShared;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsQqqPlugin;

internal sealed class QqqFrameRangeDisplayResult
{
    public string LayoutName { get; set; } = "";
    public string PrimaryFrameName { get; set; } = "";
    public int DisplayedCount { get; set; }
    public int DeferredCount { get; set; }
    public int HighlightedCount { get; set; }
    public int UnresolvedCount { get; set; }
}

internal static class QqqPlotService
{
    internal const string FileNameTemplateDrawing = "文件名";
    internal const string FileNameTemplateLayout = "布局名";
    internal const string DefaultPageSetupName = "A3GOODME";

    private static readonly string[] DrawingNameTadLabelKeywords =
    {
        "水路图",
        "方案",
        "施工图"
    };

    private const string PaperAutoMatch = "自动匹配";
    private const string ScaleFit = "布满图纸";
    private const string CombinedLayoutFallbackName = "合并图纸";
    private const string BackgroundPlotSystemVariableName = "BACKGROUNDPLOT";
    private const string TileModeSystemVariableName = "TILEMODE";
    private const string CurrentTabSystemVariableName = "CTAB";
    private const string ModelLayoutName = "Model";

    private static readonly StringComparer TextComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly object RangeOverlaySync = new();
    private static readonly List<Entity> ActiveRangeOverlayEntities = new();
    private static readonly AcadColor RangeOrderFillColor = AcadColor.FromRgb(30, 119, 180);
    private static readonly AcadColor RangeOrderTextColor = AcadColor.FromRgb(246, 248, 255);
    private static readonly Dictionary<string, (double Width, double Height)> IsoPaperSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ISO A0"] = (1189d, 841d),
        ["ISO A1"] = (841d, 594d),
        ["ISO A2"] = (594d, 420d),
        ["ISO A3"] = (420d, 297d),
        ["ISO A4"] = (297d, 210d),
        ["A0"] = (1189d, 841d),
        ["A1"] = (841d, 594d),
        ["A2"] = (594d, 420d),
        ["A3"] = (420d, 297d),
        ["A4"] = (297d, 210d)
    };

    internal static string GetSuggestedOutputFolder(Document? doc)
    {
        try
        {
            var docName = doc?.Name ?? "";
            var dir = Path.GetDirectoryName(docName);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }

        return ResolveFallbackOutputFolder();
    }

    internal static string GetSuggestedOutputFolder(string? documentName, string? fallbackFolder)
    {
        try
        {
            var dir = Path.GetDirectoryName(documentName ?? "");
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }

        if (!string.IsNullOrWhiteSpace(fallbackFolder) && Directory.Exists(fallbackFolder))
            return fallbackFolder!;

        return ResolveFallbackOutputFolder();
    }

    private static string ResolveFallbackOutputFolder()
    {
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(myDocuments))
            return myDocuments;

        return C_toolsPaths.UserEditableFolder ?? "";
    }

    internal static bool IsLikelyFilePlotter(string? printerName)
    {
        var value = (printerName ?? "").Trim();
        if (value.Length == 0)
            return false;

        return value.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("dwf", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("pc3", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static bool IsLikelyPdfPlotter(string? printerName)
    {
        var value = (printerName ?? "").Trim();
        return value.Length > 0 &&
               value.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static IReadOnlyList<QqqPlotFrameInfo> SortFrames(IEnumerable<QqqPlotFrameInfo> frames, string? sortRule)
    {
        var list = frames
            .Where(static x => x != null)
            .ToList();
        if (list.Count <= 1)
            return list;

        var layoutSortOrders = TryReadLayoutSortOrders(list);
        return (sortRule ?? "").Trim() switch
        {
            PrintSaveService.PlotOrderLayoutTopToBottomLeftToRight => SortFramesByLayoutRows(list, layoutSortOrders),
            PrintSaveService.PlotOrderLayoutLeftToRightTopToBottom => SortFramesByLayoutColumns(list, layoutSortOrders),
            PrintSaveService.PlotOrderLayerThenName => list
                .OrderBy(x => x.LayerName, TextComparer)
                .ThenBy(x => GetLayoutSortOrder(x.LayoutName, layoutSortOrders))
                .ThenBy(x => x.LayoutName, TextComparer)
                .ThenBy(x => x.FrameName, TextComparer)
                .ThenBy(x => x.AddedOrder)
                .ToList(),
            PrintSaveService.PlotOrderTypeThenLayoutThenName => list
                .OrderBy(x => x.FrameType, TextComparer)
                .ThenBy(x => GetLayoutSortOrder(x.LayoutName, layoutSortOrders))
                .ThenBy(x => x.LayoutName, TextComparer)
                .ThenBy(x => x.FrameName, TextComparer)
                .ThenBy(x => x.AddedOrder)
                .ToList(),
            PrintSaveService.PlotOrderAreaDescending => list
                .OrderByDescending(x => x.Area)
                .ThenBy(x => GetLayoutSortOrder(x.LayoutName, layoutSortOrders))
                .ThenBy(x => x.LayoutName, TextComparer)
                .ThenBy(x => x.FrameName, TextComparer)
                .ThenBy(x => x.AddedOrder)
                .ToList(),
            _ => list.OrderBy(x => x.AddedOrder).ToList()
        };
    }

    private static IReadOnlyList<QqqPlotFrameInfo> SortFramesByLayoutRows(
        IReadOnlyList<QqqPlotFrameInfo> frames,
        IReadOnlyDictionary<string, int> layoutSortOrders)
    {
        return frames
            .GroupBy(static x => (x.LayoutName ?? "").Trim(), TextComparer)
            .OrderBy(x => GetLayoutSortOrder(x.Key, layoutSortOrders))
            .ThenBy(x => x.Key, TextComparer)
            .SelectMany(static x => SortFramesWithinRows(x.ToList()))
            .ToList();
    }

    private static IReadOnlyList<QqqPlotFrameInfo> SortFramesByLayoutColumns(
        IReadOnlyList<QqqPlotFrameInfo> frames,
        IReadOnlyDictionary<string, int> layoutSortOrders)
    {
        return frames
            .GroupBy(static x => (x.LayoutName ?? "").Trim(), TextComparer)
            .OrderBy(x => GetLayoutSortOrder(x.Key, layoutSortOrders))
            .ThenBy(x => x.Key, TextComparer)
            .SelectMany(static x => SortFramesWithinColumns(x.ToList()))
            .ToList();
    }

    private static IReadOnlyList<QqqPlotFrameInfo> SortFramesWithinRows(IReadOnlyList<QqqPlotFrameInfo> frames)
    {
        if (frames.Count <= 1)
            return frames.ToList();

        var tolerance = GetSpatialGroupingTolerance(frames, useHorizontalAxis: false);
        var rows = new List<SpatialFrameGroup>();
        foreach (var frame in frames
                     .OrderByDescending(GetFrameTop)
                     .ThenBy(GetFrameLeft)
                     .ThenBy(x => x.AddedOrder))
        {
            AddFrameToSpatialGroup(rows, frame, GetFrameTop(frame), tolerance);
        }

        return rows
            .OrderByDescending(static x => x.ReferenceValue)
            .SelectMany(static x => x.Frames
                .OrderBy(GetFrameLeft)
                .ThenByDescending(GetFrameTop)
                .ThenBy(f => f.AddedOrder))
            .ToList();
    }

    private static IReadOnlyList<QqqPlotFrameInfo> SortFramesWithinColumns(IReadOnlyList<QqqPlotFrameInfo> frames)
    {
        if (frames.Count <= 1)
            return frames.ToList();

        var tolerance = GetSpatialGroupingTolerance(frames, useHorizontalAxis: true);
        var columns = new List<SpatialFrameGroup>();
        foreach (var frame in frames
                     .OrderBy(GetFrameLeft)
                     .ThenByDescending(GetFrameTop)
                     .ThenBy(x => x.AddedOrder))
        {
            AddFrameToSpatialGroup(columns, frame, GetFrameLeft(frame), tolerance);
        }

        return columns
            .OrderBy(static x => x.ReferenceValue)
            .SelectMany(static x => x.Frames
                .OrderByDescending(GetFrameTop)
                .ThenBy(GetFrameLeft)
                .ThenBy(f => f.AddedOrder))
            .ToList();
    }

    private static void AddFrameToSpatialGroup(
        List<SpatialFrameGroup> groups,
        QqqPlotFrameInfo frame,
        double axisValue,
        double tolerance)
    {
        SpatialFrameGroup? matchedGroup = null;
        var matchedDistance = double.MaxValue;
        foreach (var group in groups)
        {
            var distance = Math.Abs(axisValue - group.ReferenceValue);
            if (distance > tolerance || distance >= matchedDistance)
                continue;

            matchedGroup = group;
            matchedDistance = distance;
        }

        if (matchedGroup == null)
        {
            matchedGroup = new SpatialFrameGroup(axisValue);
            groups.Add(matchedGroup);
        }

        matchedGroup.Add(frame, axisValue);
    }

    private static double GetSpatialGroupingTolerance(IReadOnlyList<QqqPlotFrameInfo> frames, bool useHorizontalAxis)
    {
        var dimensions = frames
            .Select(frame => useHorizontalAxis ? GetFrameLongDimension(frame) : GetFrameShortDimension(frame))
            .Where(static x => x > 1e-6)
            .OrderBy(static x => x)
            .ToList();
        if (dimensions.Count == 0)
            return useHorizontalAxis ? 80d : 60d;

        var median = dimensions[dimensions.Count / 2];
        var maxTolerance = useHorizontalAxis ? 160d : 120d;
        return Math.Max(24d, Math.Min(maxTolerance, median * 0.22d));
    }

    private static IReadOnlyDictionary<string, int> TryReadLayoutSortOrders(IReadOnlyList<QqqPlotFrameInfo> frames)
    {
        if (frames.Count == 0)
            return new Dictionary<string, int>(TextComparer);

        var layoutNames = frames
            .Select(static x => (x.LayoutName ?? "").Trim())
            .Where(static x => x.Length > 0)
            .ToHashSet(TextComparer);
        if (layoutNames.Count == 0)
            return new Dictionary<string, int>(TextComparer);

        var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (document == null)
            return new Dictionary<string, int>(TextComparer);

        try
        {
            return CadDatabaseScope.Read(
                document,
                (_, transaction) =>
                {
                    var result = new Dictionary<string, int>(TextComparer);
                    var layoutDictionary = CadDatabaseScope.OpenAs<DBDictionary>(transaction, document.Database.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry entry in layoutDictionary)
                    {
                        if (!layoutNames.Contains(entry.Key) ||
                            transaction.GetObject(entry.Value, OpenMode.ForRead, false) is not Layout layout)
                        {
                            continue;
                        }

                        result[layout.LayoutName] = layout.TabOrder;
                    }

                    return (IReadOnlyDictionary<string, int>)result;
                },
                requireDocumentLock: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (AcadRuntimeException)
        {
        }
        catch (ArgumentException)
        {
        }

        return new Dictionary<string, int>(TextComparer);
    }

    private static int GetLayoutSortOrder(string? layoutName, IReadOnlyDictionary<string, int> layoutSortOrders)
    {
        var normalized = (layoutName ?? "").Trim();
        return normalized.Length > 0 && layoutSortOrders.TryGetValue(normalized, out var order)
            ? order
            : int.MaxValue;
    }

    private static double GetFrameShortDimension(QqqPlotFrameInfo frame)
    {
        var width = GetFrameWidth(frame);
        var height = GetFrameHeight(frame);
        return Math.Max(1d, Math.Min(width, height));
    }

    private static double GetFrameLongDimension(QqqPlotFrameInfo frame)
    {
        var width = GetFrameWidth(frame);
        var height = GetFrameHeight(frame);
        return Math.Max(1d, Math.Max(width, height));
    }

    private static double GetFrameWidth(QqqPlotFrameInfo frame)
    {
        return TryGetFrameExtents(frame, out var extents)
            ? Math.Abs(extents.MaxPoint.X - extents.MinPoint.X)
            : Math.Abs(frame.Width);
    }

    private static double GetFrameHeight(QqqPlotFrameInfo frame)
    {
        return TryGetFrameExtents(frame, out var extents)
            ? Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y)
            : Math.Abs(frame.Height);
    }

    private static double GetFrameLeft(QqqPlotFrameInfo frame)
    {
        return TryGetFrameExtents(frame, out var extents)
            ? extents.MinPoint.X
            : frame.CenterX - (frame.Width * 0.5d);
    }

    private static double GetFrameRight(QqqPlotFrameInfo frame)
    {
        return TryGetFrameExtents(frame, out var extents)
            ? extents.MaxPoint.X
            : frame.CenterX + (frame.Width * 0.5d);
    }

    private static double GetFrameTop(QqqPlotFrameInfo frame)
    {
        return TryGetFrameExtents(frame, out var extents)
            ? extents.MaxPoint.Y
            : frame.CenterY + (frame.Height * 0.5d);
    }

    private static double GetFrameBottom(QqqPlotFrameInfo frame)
    {
        return TryGetFrameExtents(frame, out var extents)
            ? extents.MinPoint.Y
            : frame.CenterY - (frame.Height * 0.5d);
    }

    private static bool TryGetFrameExtents(QqqPlotFrameInfo frame, out Extents3d extents)
    {
        extents = frame.WcsExtents;
        var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
        var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
        if (width > 1e-6 || height > 1e-6)
            return true;

        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        extents = new Extents3d(
            new Point3d(frame.CenterX - (frame.Width * 0.5d), frame.CenterY - (frame.Height * 0.5d), 0d),
            new Point3d(frame.CenterX + (frame.Width * 0.5d), frame.CenterY + (frame.Height * 0.5d), 0d));
        return true;
    }

    internal static string BuildPreviewOutputPath(
        Document? doc,
        QqqPlotFrameInfo frame,
        int plotIndex,
        QqqPlotOptions options)
    {
        if (!options.PlotToFile)
            return "（发送到打印机）";

        var outputFolder = (options.OutputFolder ?? "").Trim();
        if (outputFolder.Length == 0)
            outputFolder = GetSuggestedOutputFolder(doc);

        return BuildOutputFilePath(doc?.Name, frame, outputFolder, plotIndex, options, CreateOutputFileNameContext(options));
    }

    internal static string BuildPreviewOutputPath(
        string? documentName,
        string? suggestedOutputFolder,
        QqqPlotFrameInfo frame,
        int plotIndex,
        QqqPlotOptions options)
    {
        if (!options.PlotToFile)
            return "（发送到打印机）";

        var outputFolder = (options.OutputFolder ?? "").Trim();
        if (outputFolder.Length == 0)
            outputFolder = GetSuggestedOutputFolder(documentName, suggestedOutputFolder);

        return BuildOutputFilePath(documentName, frame, outputFolder, plotIndex, options, CreateOutputFileNameContext(options));
    }

    internal static string BuildPreviewCombinedOutputPath(
        string? documentName,
        string? suggestedOutputFolder,
        IReadOnlyList<QqqPlotFrameInfo> frames,
        QqqPlotOptions options)
    {
        var outputFolder = (options.OutputFolder ?? "").Trim();
        if (outputFolder.Length == 0)
            outputFolder = GetSuggestedOutputFolder(documentName, suggestedOutputFolder);

        return BuildCombinedOutputFilePath(documentName, frames, outputFolder, options);
    }

    internal static bool TryBuildCadPreviewPageFileDto(
        QqqPlotOptions options,
        out PrintSavePageFileDto dto,
        out IReadOnlyList<string> infoMessages,
        out string errorMessage)
    {
        dto = new PrintSavePageFileDto();
        infoMessages = Array.Empty<string>();
        errorMessage = "";

        var prepared = PreparePlotOptions(options, PlotDeviceRequirement.Pdf);
        infoMessages = prepared.InfoMessages;
        if (prepared.ErrorMessage.Length > 0)
        {
            errorMessage = prepared.ErrorMessage;
            return false;
        }

        dto = new PrintSavePageFileDto
        {
            PageSetupName = "",
            PrinterName = prepared.Options.PrinterName,
            CanonicalMediaName = NormalizePreviewMediaName(prepared.Options.MediaName),
            StyleSheet = prepared.Options.StyleSheet,
            CenterPlot = prepared.Options.CenterPlot,
            OffsetX = prepared.Options.OffsetX,
            OffsetY = prepared.Options.OffsetY,
            FitToPaper = prepared.Options.FitToPaper,
            ScaleText = string.IsNullOrWhiteSpace(prepared.Options.ScaleText)
                ? PrintSaveService.ScaleFitText
                : prepared.Options.ScaleText.Trim(),
            ScaleLineweights = prepared.Options.ScaleLineweights,
            AutoMatchOrientation = prepared.Options.AutoRotate,
            Landscape = prepared.Options.Landscape,
            UpsideDown = prepared.Options.UpsideDown
        };
        return true;
    }

    internal static void PrepareFrameForNativePreview(Document doc, QqqPlotFrameInfo frame)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        using var documentLock = doc.LockDocument();
        SwitchToTargetLayout(doc, NormalizeLayoutName(frame.LayoutName));
        FocusCurrentViewOnFrame(doc.Editor, frame.WcsExtents);
    }

    internal static bool TryPrepareFrameForNativePreview(
        Document doc,
        QqqPlotFrameInfo frame,
        QqqPlotOptions options,
        out IReadOnlyList<string> infoMessages,
        out string errorMessage)
    {
        infoMessages = Array.Empty<string>();
        errorMessage = "";

        if (doc == null)
        {
            errorMessage = "当前没有活动图纸。";
            return false;
        }

        if (frame == null)
        {
            errorMessage = "未找到可预览的图纸。";
            return false;
        }

        var prepared = PreparePlotOptions(options, PlotDeviceRequirement.Pdf);
        infoMessages = prepared.InfoMessages;
        if (prepared.ErrorMessage.Length > 0)
        {
            errorMessage = prepared.ErrorMessage;
            return false;
        }

        try
        {
            using var documentLock = doc.LockDocument();
            var targetLayoutName = NormalizeLayoutName(frame.LayoutName);
            SwitchToTargetLayout(doc, targetLayoutName);
            FocusCurrentViewOnFrame(doc.Editor, frame.WcsExtents);

            using (var transaction = doc.Database.TransactionManager.StartTransaction())
            {
                var layoutId = LayoutManager.Current.GetLayoutId(targetLayoutName);
                var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForWrite);
                using var settings = BuildPlotSettings(transaction, layout, frame, prepared.Options, doc.Editor);
                layout.CopyFrom(settings);
                transaction.Commit();
            }
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            C_toolsDiagnostics.LogNonFatal("V_QQQ 准备图纸预览失败", ex);
            return false;
        }
    }

    internal static bool TryShowFrameRanges(
        Document doc,
        IReadOnlyList<QqqPlotFrameInfo> frames,
        out QqqFrameRangeDisplayResult result,
        out string errorMessage)
    {
        result = new QqqFrameRangeDisplayResult();
        errorMessage = "";

        if (doc == null)
        {
            errorMessage = "当前没有活动图纸。";
            return false;
        }

        var orderedFrames = frames
            .Where(static x => x != null)
            .ToList();
        if (orderedFrames.Count == 0)
        {
            errorMessage = "未找到可显示的打印范围。";
            return false;
        }

        var preferredLayout = ResolvePreferredRangeLayout(orderedFrames);
        var visibleFrames = orderedFrames
            .Select((frame, index) => new QqqFrameOrderDisplayItem(frame, index + 1))
            .Where(x => string.Equals(x.Frame.LayoutName, preferredLayout, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (visibleFrames.Count == 0)
        {
            errorMessage = "未找到当前可显示的打印范围。";
            return false;
        }

        try
        {
            using var documentLock = doc.LockDocument();
            SwitchToTargetLayout(doc, preferredLayout);
            FocusCurrentViewOnFrame(doc.Editor, CombineFrameExtents(visibleFrames.Select(static x => x.Frame).ToList()));

            ShowFrameRangeOrderOverlay(doc.Database, visibleFrames);

            var highlightIds = ResolveFrameObjectIds(
                doc.Database,
                visibleFrames.Select(static x => x.Frame).ToList(),
                preferredLayout);
            doc.Editor.SetImpliedSelection(highlightIds);
            doc.Editor.Regen();

            result = new QqqFrameRangeDisplayResult
            {
                LayoutName = preferredLayout,
                PrimaryFrameName = visibleFrames[0].Frame.FrameName,
                DisplayedCount = visibleFrames.Count,
                DeferredCount = Math.Max(0, orderedFrames.Count - visibleFrames.Count),
                HighlightedCount = highlightIds.Length,
                UnresolvedCount = Math.Max(0, visibleFrames.Count - highlightIds.Length)
            };
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            C_toolsDiagnostics.LogNonFatal("V_QQQ 显示打印范围失败", ex);
            return false;
        }
    }

    internal static void ClearFrameRangeDisplay()
    {
        lock (RangeOverlaySync)
        {
            if (ActiveRangeOverlayEntities.Count == 0)
                return;

            var transientManager = TransientManager.CurrentTransientManager;
            var viewportNumbers = new IntegerCollection();
            foreach (var entity in ActiveRangeOverlayEntities)
            {
                try
                {
                    transientManager.EraseTransient(entity, viewportNumbers);
                }
                catch (Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("V_QQQ 清理打印范围顺序标记失败", ex);
                }
                finally
                {
                    entity.Dispose();
                }
            }

            ActiveRangeOverlayEntities.Clear();
        }
    }
    internal static QqqPlotRunResult PlotFrames(
        Document doc,
        IReadOnlyList<QqqPlotFrameInfo> frames,
        QqqPlotOptions options,
        Action<int, string, string>? progressCallback = null)
    {
        var result = new QqqPlotRunResult
        {
            TotalCount = frames.Count
        };

        if (frames.Count == 0)
            return result;

        var effectiveOptions = PreparePlotOptions(options, options.PlotToFile ? PlotDeviceRequirement.File : PlotDeviceRequirement.Any);
        result.InfoMessages.AddRange(effectiveOptions.InfoMessages);
        if (effectiveOptions.ErrorMessage.Length > 0)
        {
            result.FailureCount = frames.Count;
            result.ErrorMessages.Add(effectiveOptions.ErrorMessage);
            return result;
        }

        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            result.FailureCount = frames.Count;
            result.ErrorMessages.Add("AutoCAD 当前正在打印，请等待当前任务结束后重试。");
            return result;
        }

        var outputFolder = (effectiveOptions.Options.OutputFolder ?? "").Trim();
        if (effectiveOptions.Options.PlotToFile)
        {
            if (outputFolder.Length == 0)
                outputFolder = GetSuggestedOutputFolder(doc);
            Directory.CreateDirectory(outputFolder);
        }

        var originalLayout = LayoutManager.Current.CurrentLayout;
        var plotState = CaptureForegroundPlotState();
        var fileNameContext = effectiveOptions.Options.PlotToFile
            ? CreateOutputFileNameContext(effectiveOptions.Options)
            : null;
        using var documentLock = doc.LockDocument();
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                try
                {
                    progressCallback?.Invoke(i, "打印中…", "");
                    var outputPath = PlotSingleFrame(doc, frame, effectiveOptions.Options, outputFolder, i + 1, fileNameContext);
                    result.SuccessCount++;
                    progressCallback?.Invoke(i, "打印完成", outputPath);
                }
                catch (Exception ex)
                {
                    result.FailureCount++;
                    result.ErrorMessages.Add($"{frame.FrameName}：{ex.Message}");
                    progressCallback?.Invoke(i, "失败：" + ex.Message, "");
                }
            }
        }
        finally
        {
            RestoreForegroundPlotState(plotState);
            TryRestoreTargetLayout(doc, originalLayout);
        }

        return result;
    }

    internal static QqqPlotRunResult PlotFramesToCombinedPdf(
        Document doc,
        IReadOnlyList<QqqPlotFrameInfo> frames,
        QqqPlotOptions options,
        Action<int, string, string>? progressCallback = null)
    {
        var result = new QqqPlotRunResult
        {
            TotalCount = frames.Count
        };

        if (frames.Count == 0)
            return result;

        var effectiveOptions = PreparePlotOptions(options, PlotDeviceRequirement.Pdf);
        result.InfoMessages.AddRange(effectiveOptions.InfoMessages);
        if (effectiveOptions.ErrorMessage.Length > 0)
        {
            result.FailureCount = frames.Count;
            result.ErrorMessages.Add(effectiveOptions.ErrorMessage);
            return result;
        }

        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            result.FailureCount = frames.Count;
            result.ErrorMessages.Add("AutoCAD 当前正在打印，请等待当前任务结束后重试。");
            return result;
        }

        var outputFolder = (effectiveOptions.Options.OutputFolder ?? "").Trim();
        if (outputFolder.Length == 0)
            outputFolder = GetSuggestedOutputFolder(doc);

        Directory.CreateDirectory(outputFolder);
        result.OutputFolder = outputFolder;

        var combinedOutputFile = BuildCombinedOutputFilePath(doc.Name, frames, outputFolder, effectiveOptions.Options);
        var tempFolder = Path.Combine(Path.GetTempPath(), "C_tools_V_QQQ", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        var plottedFiles = new List<string>();
        var tempOptions = CreateSingleSheetPdfOptions(effectiveOptions.Options, tempFolder);
        var tempFileNameContext = CreateOutputFileNameContext(tempOptions);

        var originalLayout = LayoutManager.Current.CurrentLayout;
        var plotState = CaptureForegroundPlotState();
        using var documentLock = doc.LockDocument();
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                try
                {
                    progressCallback?.Invoke(i, "打印中…", "");
                    var plottedFile = PlotSingleFrame(doc, frame, tempOptions, tempFolder, i + 1, tempFileNameContext);
                    plottedFiles.Add(plottedFile);
                    result.SuccessCount++;
                    progressCallback?.Invoke(i, "已生成临时 PDF", plottedFile);
                }
                catch (Exception ex)
                {
                    result.FailureCount++;
                    result.ErrorMessages.Add($"{frame.FrameName}：{ex.Message}");
                    progressCallback?.Invoke(i, "失败：" + ex.Message, "");
                }
            }

            if (plottedFiles.Count > 0)
            {
                try
                {
                    MergePdfFiles(plottedFiles, combinedOutputFile);
                    result.OutputFile = combinedOutputFile;
                    progressCallback?.Invoke(plottedFiles.Count - 1, "合并完成", combinedOutputFile);
                }
                catch (Exception ex)
                {
                    result.MergeErrorMessage = ex.Message;
                    result.ErrorMessages.Add("合并 PDF 失败：" + ex.Message);
                    TryDeleteFile(combinedOutputFile);

                    var fallback = PreserveSingleSheetOutputs(plottedFiles, combinedOutputFile, outputFolder);
                    if (fallback.SavedCount > 0)
                    {
                        result.FallbackOutputFolder = fallback.FolderPath;
                        result.FallbackOutputFileCount = fallback.SavedCount;
                        result.InfoMessages.Add($"合并失败时已保留 {fallback.SavedCount} 张单页 PDF。");
                        progressCallback?.Invoke(plottedFiles.Count - 1, "合并失败，已保留单页 PDF", fallback.FolderPath);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(fallback.ErrorMessage))
                            result.ErrorMessages.Add(fallback.ErrorMessage);
                        progressCallback?.Invoke(plottedFiles.Count - 1, "合并失败", "");
                    }
                }
            }
        }
        finally
        {
            RestoreForegroundPlotState(plotState);
            TryRestoreTargetLayout(doc, originalLayout);

            TryDeleteTempFolder(tempFolder);
        }

        return result;
    }

    internal static string PlotFrame(
        Document doc,
        QqqPlotFrameInfo frame,
        QqqPlotOptions options,
        int plotIndex)
    {
        var effectiveOptions = PreparePlotOptions(options, options.PlotToFile ? PlotDeviceRequirement.File : PlotDeviceRequirement.Any);
        if (effectiveOptions.ErrorMessage.Length > 0)
            throw new InvalidOperationException(effectiveOptions.ErrorMessage);

        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new InvalidOperationException("AutoCAD 当前正在打印，请等待当前任务结束后重试。");

        var outputFolder = (effectiveOptions.Options.OutputFolder ?? "").Trim();
        if (effectiveOptions.Options.PlotToFile)
        {
            if (outputFolder.Length == 0)
                outputFolder = GetSuggestedOutputFolder(doc);
            Directory.CreateDirectory(outputFolder);
        }

        var originalLayout = LayoutManager.Current.CurrentLayout;
        var plotState = CaptureForegroundPlotState();
        using var documentLock = doc.LockDocument();
        try
        {
            return PlotSingleFrame(
                doc,
                frame,
                effectiveOptions.Options,
                outputFolder,
                plotIndex,
                effectiveOptions.Options.PlotToFile ? CreateOutputFileNameContext(effectiveOptions.Options) : null);
        }
        finally
        {
            RestoreForegroundPlotState(plotState);
            TryRestoreTargetLayout(doc, originalLayout);
        }
    }

    private static string PlotSingleFrame(
        Document doc,
        QqqPlotFrameInfo frame,
        QqqPlotOptions options,
        string outputFolder,
        int plotIndex,
        OutputFileNameContext? fileNameContext = null)
    {
        var targetLayoutName = NormalizeLayoutName(frame.LayoutName);
        SwitchToTargetLayout(doc, targetLayoutName);

        PlotInfo plotInfo;
        using (var transaction = doc.Database.TransactionManager.StartTransaction())
        {
            var layoutId = LayoutManager.Current.GetLayoutId(targetLayoutName);
            var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
            var settings = BuildPlotSettings(transaction, layout, frame, options, doc.Editor);
            plotInfo = BuildValidatedPlotInfo(layout, settings);
            transaction.Commit();
        }

        var plotToFile = options.PlotToFile;
        var outputPath = plotToFile
            ? BuildOutputFilePath(doc.Name, frame, outputFolder, plotIndex, options, fileNameContext ?? CreateOutputFileNameContext(options))
            : "（发送到打印机）";

        using var plotEngine = PlotFactory.CreatePublishEngine();
        using var progressDialog = new PlotProgressDialog(false, 1, true);

        ConfigureProgressDialog(progressDialog, frame);

        plotEngine.BeginPlot(progressDialog, null);
        plotEngine.BeginDocument(
            plotInfo,
            Path.GetFileName(doc.Name),
            null,
            1,
            plotToFile,
            plotToFile ? outputPath : string.Empty);
        progressDialog.OnBeginSheet();
        progressDialog.LowerSheetProgressRange = 0;
        progressDialog.UpperSheetProgressRange = 100;
        progressDialog.SheetProgressPos = 0;

        var pageInfo = new PlotPageInfo();
        plotEngine.BeginPage(pageInfo, plotInfo, true, null);
        plotEngine.BeginGenerateGraphics(null);
        plotEngine.EndGenerateGraphics(null);
        plotEngine.EndPage(null);

        progressDialog.SheetProgressPos = 100;
        progressDialog.OnEndSheet();
        plotEngine.EndDocument(null);
        progressDialog.PlotProgressPos = 100;
        progressDialog.OnEndPlot();
        plotEngine.EndPlot(null);

        return outputPath;
    }

    private static void ConfigureProgressDialog(PlotProgressDialog progressDialog, QqqPlotFrameInfo frame)
    {
        progressDialog.set_PlotMsgString(PlotMessageIndex.DialogTitle, "C_TOOL V_QQQ 批量打印");
        progressDialog.set_PlotMsgString(PlotMessageIndex.SheetName, frame.FrameName);
        progressDialog.set_PlotMsgString(PlotMessageIndex.SheetNameToolTip, frame.FrameName);
        progressDialog.set_PlotMsgString(PlotMessageIndex.Status, "正在打印图框…");
        progressDialog.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, "当前图框");
        progressDialog.set_PlotMsgString(PlotMessageIndex.SheetSetProgressCaption, "批量打印");
        progressDialog.IsVisible = false;
        progressDialog.OnBeginPlot();
        progressDialog.LowerPlotProgressRange = 0;
        progressDialog.UpperPlotProgressRange = 100;
        progressDialog.PlotProgressPos = 0;
    }

    private static void SwitchToTargetLayout(Document doc, string layoutName)
    {
        var normalizedLayoutName = NormalizeLayoutName(layoutName);
        if (IsModelLayoutName(normalizedLayoutName))
        {
            SwitchToModelLayout(doc);
            return;
        }

        SwitchToPaperLayout(doc, normalizedLayoutName);
    }

    private static void SwitchToModelLayout(Document doc)
    {
        var layoutManager = LayoutManager.Current;
        EnsureLayoutExists(layoutManager, ModelLayoutName);

        if (!doc.Database.TileMode)
            TrySetSystemVariableOrLog(TileModeSystemVariableName, 1, "V_QQQ 切换到模型页失败");

        SetCurrentLayout(layoutManager, ModelLayoutName);
        TrySwitchEditorToModelSpace(doc);
    }

    private static void SwitchToPaperLayout(Document doc, string layoutName)
    {
        var layoutManager = LayoutManager.Current;
        EnsureLayoutExists(layoutManager, layoutName);

        if (doc.Database.TileMode)
            TrySetSystemVariableOrLog(TileModeSystemVariableName, 0, "V_QQQ 切换到布局页失败");

        SetCurrentLayout(layoutManager, layoutName);
        TrySwitchEditorToPaperSpace(doc);
    }

    private static string NormalizeLayoutName(string? layoutName)
    {
        var value = (layoutName ?? "").Trim();
        return IsModelLayoutName(value) ? ModelLayoutName : value;
    }

    private static bool IsModelLayoutName(string? layoutName)
    {
        var value = (layoutName ?? "").Trim();
        return string.Equals(value, ModelLayoutName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "模型", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureLayoutExists(LayoutManager layoutManager, string layoutName)
    {
        if (!layoutManager.LayoutExists(layoutName))
            throw new InvalidOperationException($"未找到布局或空间“{layoutName}”。");
    }

    private static void SetCurrentLayout(LayoutManager layoutManager, string layoutName)
    {
        if (string.Equals(layoutManager.CurrentLayout, layoutName, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            layoutManager.CurrentLayout = layoutName;
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or AcadRuntimeException or ArgumentException)
        {
            C_toolsDiagnostics.LogNonFatal($"V_QQQ 使用 LayoutManager 切换到“{layoutName}”失败，尝试 CTAB 兼容方式", ex);
        }

        if (!CadSystemVariableService.TrySetValue(CurrentTabSystemVariableName, layoutName, out var errorMessage))
            throw new InvalidOperationException($"切换到“{layoutName}”失败：{errorMessage}");
    }

    private static void TrySetSystemVariableOrLog(string name, object value, string logMessage)
    {
        if (!CadSystemVariableService.TrySetValue(name, value, out var errorMessage))
            C_toolsDiagnostics.LogNonFatal($"{logMessage}（{name}={value}）：{errorMessage}");
    }

    private static void TrySwitchEditorToModelSpace(Document doc)
    {
        try
        {
            doc.Editor.SwitchToModelSpace();
        }
        catch (InvalidOperationException)
        {
        }
        catch (AcadRuntimeException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private static void TrySwitchEditorToPaperSpace(Document doc)
    {
        try
        {
            doc.Editor.SwitchToPaperSpace();
        }
        catch (InvalidOperationException)
        {
        }
        catch (AcadRuntimeException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private static void TryRestoreTargetLayout(Document doc, string? originalLayout)
    {
        var normalizedLayout = NormalizeLayoutName(originalLayout);
        if (normalizedLayout.Length == 0)
            return;

        try
        {
            if (!string.Equals(LayoutManager.Current.CurrentLayout, normalizedLayout, StringComparison.OrdinalIgnoreCase))
                SwitchToTargetLayout(doc, normalizedLayout);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 恢复原布局失败", ex);
        }
    }

    private static PlotInfo BuildValidatedPlotInfo(Layout layout, PlotSettings settings)
    {
        var plotInfo = new PlotInfo
        {
            Layout = layout.ObjectId,
            OverrideSettings = settings
        };

        using var validator = new PlotInfoValidator
        {
            MediaMatchingPolicy = MatchingPolicy.MatchEnabled
        };
        validator.Validate(plotInfo);
        return plotInfo;
    }

    private static PlotSettings BuildPlotSettings(
        Transaction transaction,
        Layout layout,
        QqqPlotFrameInfo frame,
        QqqPlotOptions options,
        Editor editor)
    {
        var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);

        var validator = PlotSettingsValidator.Current;
        validator.RefreshLists(settings);

        var resolvedMediaName = "";
        var applyExplicitPlotOptions = false;
        if (TryApplyNamedPageSetup(transaction, layout.Database, options.PageSetupName, settings))
        {
            validator.RefreshLists(settings);
            EnsureValidPageSetup(layout, settings, options.PageSetupName);
            resolvedMediaName = settings.CanonicalMediaName ?? "";
        }
        else if (!options.UseCadPlotSettings)
        {
            applyExplicitPlotOptions = true;
            validator.SetPlotConfigurationName(settings, options.PrinterName.Trim(), null);
            validator.RefreshLists(settings);

            resolvedMediaName = ResolveCanonicalMediaName(settings, validator, frame, options);
            if (resolvedMediaName.Length > 0)
            {
                validator.SetCanonicalMediaName(settings, resolvedMediaName);
                validator.RefreshLists(settings);
            }

            var styleSheet = (options.StyleSheet ?? "").Trim();
            if (styleSheet.Length > 0)
            {
                validator.SetCurrentStyleSheet(settings, styleSheet);
                settings.PlotPlotStyles = true;
            }
        }
        else
        {
            EnsureValidCadPlotSettings(layout, settings);
            resolvedMediaName = settings.CanonicalMediaName ?? "";
        }

        // AutoCAD validates centering against the active plot type/window.
        // Apply the window first, especially for model-space window plots.
        ApplyPlotWindowArea(settings, validator, layout, editor, frame.WcsExtents);

        if (applyExplicitPlotOptions)
        {
            validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
            ApplyScaleSettings(settings, validator, options);
            validator.SetPlotCentered(settings, options.CenterPlot);
            if (!options.CenterPlot)
                validator.SetPlotOrigin(settings, new Point2d(options.OffsetX, options.OffsetY));
            validator.SetPlotRotation(settings, DetermineRotation(frame, options, resolvedMediaName));

            settings.ShowPlotStyles = false;
            settings.ScaleLineweights = options.ScaleLineweights;
            settings.PlotTransparency = false;
            settings.PlotHidden = false;
            settings.PlotViewportBorders = false;
        }

        return settings;
    }

    private static void ApplyPlotWindowArea(
        PlotSettings settings,
        PlotSettingsValidator validator,
        Layout layout,
        Editor editor,
        Extents3d extents)
    {
        try
        {
            validator.SetPlotWindowArea(settings, ConvertWindowToDcs(editor, extents));
        }
        catch (Exception ex) when (layout.ModelType && IsPlotWindowCompatibilityException(ex))
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 模型空间 DCS 打印窗口不可用，尝试模型坐标窗口兼容方式", ex);
            validator.SetPlotWindowArea(settings, ConvertExtentsToWindow(extents));
        }

        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
    }

    private static bool TryApplyNamedPageSetup(
        Transaction transaction,
        Database database,
        string? pageSetupName,
        PlotSettings targetSettings)
    {
        var resolvedName = (pageSetupName ?? "").Trim();
        if (resolvedName.Length == 0 || database.PlotSettingsDictionaryId.IsNull)
            return false;

        if (transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead, false) is not DBDictionary dictionary ||
            !dictionary.Contains(resolvedName))
        {
            return false;
        }

        var plotSettingsId = dictionary.GetAt(resolvedName);
        if (transaction.GetObject(plotSettingsId, OpenMode.ForRead, false) is not PlotSettings pageSetup)
            return false;

        targetSettings.CopyFrom(pageSetup);
        return true;
    }

    private static void EnsureValidPageSetup(Layout layout, PlotSettings settings, string? pageSetupName)
    {
        var printerName = (settings.PlotConfigurationName ?? "").Trim();
        if (!PrintSaveService.IsNoPlotDeviceName(printerName))
            return;

        var name = string.IsNullOrWhiteSpace(pageSetupName) ? "指定页面设置" : pageSetupName!.Trim();
        throw new InvalidOperationException($"布局“{layout.LayoutName}”中的页面设置“{name}”未配置有效打印机。");
    }

    private static void EnsureValidCadPlotSettings(Layout layout, PlotSettings settings)
    {
        var printerName = (settings.PlotConfigurationName ?? "").Trim();
        if (!PrintSaveService.IsNoPlotDeviceName(printerName))
            return;

        throw new InvalidOperationException($"布局“{layout.LayoutName}”未设置有效打印机，请先在 CAD 打印设置中配置。");
    }

    private static Extents2d ConvertWindowToDcs(Editor editor, Extents3d extents)
    {
        using var view = editor.GetCurrentView();
        var transform = Matrix3d.PlaneToWorld(view.ViewDirection);
        transform = Matrix3d.Displacement(view.Target - Point3d.Origin) * transform;
        transform = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * transform;
        transform = transform.Inverse();

        var transformed = new Extents3d(extents.MinPoint, extents.MaxPoint);
        transformed.TransformBy(transform);

        return new Extents2d(
            transformed.MinPoint.X,
            transformed.MinPoint.Y,
            transformed.MaxPoint.X,
            transformed.MaxPoint.Y);
    }

    private static Extents2d ConvertExtentsToWindow(Extents3d extents)
    {
        return new Extents2d(
            Math.Min(extents.MinPoint.X, extents.MaxPoint.X),
            Math.Min(extents.MinPoint.Y, extents.MaxPoint.Y),
            Math.Max(extents.MinPoint.X, extents.MaxPoint.X),
            Math.Max(extents.MinPoint.Y, extents.MaxPoint.Y));
    }

    private static bool IsPlotWindowCompatibilityException(Exception ex)
    {
        return ex is InvalidOperationException or AcadRuntimeException or ArgumentException;
    }

    private static PlotRotation DetermineRotation(QqqPlotFrameInfo frame, QqqPlotOptions options, string? mediaName)
    {
        if (!options.AutoRotate)
            return ResolveFixedRotation(options);

        var frameLandscape = frame.Width >= frame.Height;
        if (TryParseMediaSize(mediaName, out var mediaWidth, out var mediaHeight))
        {
            var mediaLandscape = mediaWidth >= mediaHeight;
            return ApplyUpsideDown(
                frameLandscape == mediaLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090,
                options.UpsideDown);
        }

        return ApplyUpsideDown(frameLandscape ? PlotRotation.Degrees090 : PlotRotation.Degrees000, options.UpsideDown);
    }

    private static PlotRotation ResolveFixedRotation(QqqPlotOptions options)
    {
        if (options.Landscape)
            return options.UpsideDown ? PlotRotation.Degrees270 : PlotRotation.Degrees090;

        return options.UpsideDown ? PlotRotation.Degrees180 : PlotRotation.Degrees000;
    }

    private static PlotRotation ApplyUpsideDown(PlotRotation rotation, bool upsideDown)
    {
        if (!upsideDown)
            return rotation;

        return rotation switch
        {
            PlotRotation.Degrees000 => PlotRotation.Degrees180,
            PlotRotation.Degrees090 => PlotRotation.Degrees270,
            PlotRotation.Degrees180 => PlotRotation.Degrees000,
            PlotRotation.Degrees270 => PlotRotation.Degrees090,
            _ => rotation
        };
    }

    private static string NormalizePreviewMediaName(string? mediaName)
    {
        var value = (mediaName ?? "").Trim();
        return value.Length == 0 ? PrintSaveService.MediaAutoMatchText : value;
    }

    private static string ResolveCanonicalMediaName(
        PlotSettings settings,
        PlotSettingsValidator validator,
        QqqPlotFrameInfo frame,
        QqqPlotOptions options)
    {
        var requestedPaper = GetRequestedPaperName(frame, options);
        if (requestedPaper.Length == 0)
            return "";

        var mediaNames = ReadNonEmptyValues(validator.GetCanonicalMediaNameList(settings));
        if (mediaNames.Count == 0)
            return "";

        return TryFindMatchingMediaName(mediaNames, requestedPaper);
    }

    private static string GetRequestedPaperName(QqqPlotFrameInfo frame, QqqPlotOptions options)
    {
        var requestedPaper = (options.MediaName ?? "").Trim();
        if (options.AdaptPaper || requestedPaper.Length == 0 || string.Equals(requestedPaper, PaperAutoMatch, StringComparison.OrdinalIgnoreCase))
        {
            var guessedPaper = (frame.PaperSize ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(guessedPaper) &&
                !string.Equals(guessedPaper, PaperAutoMatch, StringComparison.OrdinalIgnoreCase))
            {
                return guessedPaper;
            }

            return "";
        }

        return requestedPaper;
    }

    private static string TryFindMatchingMediaName(IReadOnlyList<string> mediaNames, string requestedPaper)
    {
        var normalizedRequest = NormalizePaperRequest(requestedPaper);
        foreach (var mediaName in mediaNames)
        {
            if (mediaName.IndexOf(requestedPaper, StringComparison.OrdinalIgnoreCase) >= 0 ||
                mediaName.IndexOf(normalizedRequest, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return mediaName;
            }
        }

        if (!TryGetIsoPaperSize(requestedPaper, out var width, out var height))
            return "";

        foreach (var mediaName in mediaNames)
        {
            if (!TryParseMediaSize(mediaName, out var mediaWidth, out var mediaHeight))
                continue;

            if (IsCloseMediaSize(mediaWidth, mediaHeight, width, height))
                return mediaName;
        }

        return "";
    }

    private static string NormalizePaperRequest(string requestedPaper)
    {
        var text = (requestedPaper ?? "").Trim();
        if (text.StartsWith("ISO ", StringComparison.OrdinalIgnoreCase))
            return text.Substring(4).Trim();

        return text;
    }

    private static bool TryGetIsoPaperSize(string requestedPaper, out double width, out double height)
    {
        width = 0;
        height = 0;
        return IsoPaperSizes.TryGetValue((requestedPaper ?? "").Trim(), out var paperSize) &&
               AssignPaperSize(paperSize, out width, out height);
    }

    private static bool AssignPaperSize((double Width, double Height) paperSize, out double width, out double height)
    {
        width = paperSize.Width;
        height = paperSize.Height;
        return width > 0 && height > 0;
    }

    private static bool IsCloseMediaSize(double leftWidth, double leftHeight, double rightWidth, double rightHeight)
    {
        return AreSizesClose(leftWidth, rightWidth) && AreSizesClose(leftHeight, rightHeight) ||
               AreSizesClose(leftWidth, rightHeight) && AreSizesClose(leftHeight, rightWidth);
    }

    private static bool AreSizesClose(double left, double right)
    {
        return Math.Abs(left - right) <= 2.0;
    }

    private static void ApplyScaleSettings(
        PlotSettings settings,
        PlotSettingsValidator validator,
        QqqPlotOptions options)
    {
        var scaleText = (options.ScaleText ?? "").Trim();
        if (options.FitToPaper ||
            scaleText.Length == 0 ||
            string.Equals(scaleText, ScaleFit, StringComparison.OrdinalIgnoreCase))
        {
            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            return;
        }

        if (TryResolveStandardScale(scaleText, out var standardScale))
        {
            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, standardScale);
            return;
        }

        if (TryParseScale(scaleText, out var numerator, out var denominator))
        {
            validator.SetUseStandardScale(settings, false);
            validator.SetCustomPrintScale(settings, new CustomScale(numerator, denominator));
            return;
        }

        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.StdScale1To1);
    }

    private static bool TryResolveStandardScale(string scaleText, out StdScaleType standardScale)
    {
        standardScale = StdScaleType.StdScale1To1;
        switch ((scaleText ?? "").Trim())
        {
            case "1:1":
                standardScale = StdScaleType.StdScale1To1;
                return true;
            case "1:2":
                standardScale = StdScaleType.StdScale1To2;
                return true;
            case "1:50":
                standardScale = StdScaleType.StdScale1To50;
                return true;
            case "1:100":
                standardScale = StdScaleType.StdScale1To100;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseScale(string scaleText, out double numerator, out double denominator)
    {
        numerator = 1d;
        denominator = 1d;

        var text = (scaleText ?? "").Trim();
        if (text.Length == 0)
            return false;

        var separators = new[] { ':', '/', '：' };
        var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numerator))
            return false;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out denominator))
            return false;

        return numerator > 0 && denominator > 0;
    }

    private static bool TryParseMediaSize(string? mediaName, out double width, out double height)
    {
        width = 0;
        height = 0;
        var value = (mediaName ?? "").Trim();
        if (value.Length == 0)
            return false;

        var matches = Regex.Matches(value, @"\d+(?:\.\d+)?");
        if (matches.Count < 2)
            return false;

        var widthIndex = matches.Count - 2;
        var heightIndex = matches.Count - 1;

        if (!double.TryParse(matches[widthIndex].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            return false;
        if (!double.TryParse(matches[heightIndex].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height))
            return false;

        return width > 0 && height > 0;
    }

    private static QqqPlotOptions CreateSingleSheetPdfOptions(QqqPlotOptions options, string outputFolder)
    {
        return new QqqPlotOptions
        {
            UseCadPlotSettings = options.UseCadPlotSettings,
            PrinterName = options.PrinterName,
            PageSetupName = options.PageSetupName,
            MediaName = options.MediaName,
            StyleSheet = options.StyleSheet,
            ScaleText = options.ScaleText,
            FileNameTemplate = "{index:000}_{layout}_{frame}",
            TadLabelName = options.TadLabelName,
            AutoRotate = options.AutoRotate,
            CenterPlot = options.CenterPlot,
            OffsetX = options.OffsetX,
            OffsetY = options.OffsetY,
            FitToPaper = options.FitToPaper,
            AdaptPaper = options.AdaptPaper,
            ScaleLineweights = options.ScaleLineweights,
            Landscape = options.Landscape,
            UpsideDown = options.UpsideDown,
            PlotToFile = true,
            OutputFolder = outputFolder
        };
    }

    private static string BuildCombinedOutputFilePath(
        string? documentName,
        IReadOnlyList<QqqPlotFrameInfo> frames,
        string outputFolder,
        QqqPlotOptions options)
    {
        var drawingName = Path.GetFileNameWithoutExtension(documentName ?? "");
        if (string.IsNullOrWhiteSpace(drawingName))
            drawingName = "Drawing";

        var ext = ".pdf";
        var resolvedTemplate = ResolveBuiltInFileNameTemplate(options.FileNameTemplate);
        var layoutName = ResolveCombinedLayoutName(frames);
        var baseName = resolvedTemplate switch
        {
            "{layout}" => layoutName,
            _ => ResolveDefaultDrawingOutputName(drawingName, options.TadLabelName)
        };

        baseName = SanitizeFileName(baseName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = drawingName;

        var basePath = Path.Combine(outputFolder, baseName + ext);
        if (!File.Exists(basePath))
            return basePath;

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(outputFolder, $"{baseName}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return basePath;
    }

    private static string BuildOutputFilePath(
        string? documentName,
        QqqPlotFrameInfo frame,
        string outputFolder,
        int plotIndex,
        QqqPlotOptions options,
        OutputFileNameContext? fileNameContext = null)
    {
        var drawingName = Path.GetFileNameWithoutExtension(documentName ?? "");
        if (string.IsNullOrWhiteSpace(drawingName))
            drawingName = "Drawing";

        fileNameContext ??= CreateOutputFileNameContext(options);
        var ext = fileNameContext.Extension;
        var defaultDrawingName = ResolveDefaultDrawingOutputName(drawingName, options.TadLabelName);
        var baseName = fileNameContext.Renderer.Render(defaultDrawingName, frame, plotIndex, ext);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = $"{drawingName}_{SanitizeFileName(frame.LayoutName)}_{plotIndex:000}_{SanitizeFileName(frame.FrameName)}";

        baseName = SanitizeFileName(baseName);
        if (!baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            baseName += ext;

        var basePath = Path.Combine(outputFolder, baseName);
        if (!File.Exists(basePath))
            return basePath;

        var stem = Path.GetFileNameWithoutExtension(baseName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(outputFolder, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return basePath;
    }

    private static string RenderFileNameTemplate(
        string? template,
        string drawingName,
        QqqPlotFrameInfo frame,
        int plotIndex,
        string extension)
    {
        return QqqFileNameTemplateRenderer.Create(template).Render(drawingName, frame, plotIndex, extension);
    }

    private static OutputFileNameContext CreateOutputFileNameContext(QqqPlotOptions options)
    {
        return new OutputFileNameContext(
            QqqFileNameTemplateRenderer.Create(options.FileNameTemplate),
            GuessOutputExtension(options.PrinterName));
    }

    private static string ResolveBuiltInFileNameTemplate(string? template)
    {
        var value = (template ?? "").Trim();
        if (string.Equals(value, FileNameTemplateLayout, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "{layout}", StringComparison.OrdinalIgnoreCase))
        {
            return "{layout}";
        }

        return "{drawing}";
    }

    private static string ResolveDefaultDrawingOutputName(string drawingName, string? tadLabelName)
    {
        var label = (tadLabelName ?? "").Trim();
        if (drawingName.Length == 0 || label.Length == 0)
            return drawingName;

        var result = drawingName;
        foreach (var keyword in DrawingNameTadLabelKeywords)
        {
            result = Regex.Replace(
                result,
                Regex.Escape(keyword),
                label,
                RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string ResolveCombinedLayoutName(IReadOnlyList<QqqPlotFrameInfo> frames)
    {
        var layoutNames = frames
            .Select(static x => (x.LayoutName ?? "").Trim())
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return layoutNames.Count switch
        {
            0 => CombinedLayoutFallbackName,
            1 => layoutNames[0],
            _ => CombinedLayoutFallbackName
        };
    }

    private static string GuessOutputExtension(string? printerName)
    {
        var value = (printerName ?? "").Trim();
        if (value.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0)
            return ".pdf";
        if (value.IndexOf("dwf", StringComparison.OrdinalIgnoreCase) >= 0)
            return ".dwf";
        if (value.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0)
            return ".png";
        if (value.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0)
            return ".jpg";
        return ".plt";
    }

    private static string SanitizeFileName(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";

        foreach (var ch in Path.GetInvalidFileNameChars())
            text = text.Replace(ch, '_');

        return text;
    }

    private static void MergePdfFiles(IReadOnlyList<string> inputFiles, string outputFile)
    {
        if (inputFiles.Count == 0)
            throw new InvalidOperationException("没有可合并的 PDF 文件。");

        using var outputDocument = new PdfDocument();
        foreach (var inputFile in inputFiles)
        {
            using var inputDocument = PdfReader.Open(inputFile, PdfDocumentOpenMode.Import);
            for (var i = 0; i < inputDocument.PageCount; i++)
                outputDocument.AddPage(inputDocument.Pages[i]);
        }

        outputDocument.Save(outputFile);
    }

    private static PreservedPlotFilesResult PreserveSingleSheetOutputs(
        IReadOnlyList<string> inputFiles,
        string combinedOutputFile,
        string outputFolder)
    {
        var result = new PreservedPlotFilesResult();
        if (inputFiles.Count == 0)
            return result;

        try
        {
            var folderPath = BuildSingleSheetFallbackFolderPath(combinedOutputFile, outputFolder);
            Directory.CreateDirectory(folderPath);

            var savedCount = 0;
            foreach (var inputFile in inputFiles)
            {
                if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
                    continue;

                var fileName = Path.GetFileName(inputFile);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var targetPath = BuildUniqueFilePath(Path.Combine(folderPath, fileName));
                File.Copy(inputFile, targetPath, overwrite: false);
                savedCount++;
            }

            if (savedCount > 0)
            {
                result.FolderPath = folderPath;
                result.SavedCount = savedCount;
            }
            else
            {
                TryDeleteEmptyDirectory(folderPath);
                result.ErrorMessage = "合并 PDF 失败，且未能保留单页 PDF。";
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 保留单页 PDF 失败", ex);
            result.ErrorMessage = "合并 PDF 失败，且保留单页 PDF 时出错：" + ex.Message;
        }

        return result;
    }

    private static string BuildSingleSheetFallbackFolderPath(string combinedOutputFile, string outputFolder)
    {
        var baseName = Path.GetFileNameWithoutExtension(combinedOutputFile);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "单页PDF";

        baseName = SanitizeFileName(baseName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "单页PDF";

        var folderName = $"{baseName}_单页PDF";
        var folderPath = Path.Combine(outputFolder, folderName);
        if (!Directory.Exists(folderPath))
            return folderPath;

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(outputFolder, $"{folderName}_{i}");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return folderPath;
    }

    private static string BuildUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? "";
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{fileNameWithoutExtension}_{i}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteTempFolder(string tempFolder)
    {
        try
        {
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void FocusCurrentViewOnFrame(Editor editor, Extents3d extents)
    {
        var dcsWindow = ConvertWindowToDcs(editor, extents);
        var width = Math.Abs(dcsWindow.MaxPoint.X - dcsWindow.MinPoint.X);
        var height = Math.Abs(dcsWindow.MaxPoint.Y - dcsWindow.MinPoint.Y);
        if (width <= 1e-6 || height <= 1e-6)
            throw new InvalidOperationException("图框范围无效，无法定位到预览图纸。");

        using var view = editor.GetCurrentView();
        view.CenterPoint = new Point2d(
            (dcsWindow.MinPoint.X + dcsWindow.MaxPoint.X) * 0.5,
            (dcsWindow.MinPoint.Y + dcsWindow.MaxPoint.Y) * 0.5);
        view.Width = Math.Max(width * 1.08, 1d);
        view.Height = Math.Max(height * 1.08, 1d);
        editor.SetCurrentView(view);
    }

    private static string ResolvePreferredRangeLayout(IReadOnlyList<QqqPlotFrameInfo> frames)
    {
        try
        {
            var currentLayout = LayoutManager.Current.CurrentLayout ?? "";
            if (!string.IsNullOrWhiteSpace(currentLayout))
            {
                var currentLayoutFrame = frames.FirstOrDefault(
                    x => string.Equals(x.LayoutName, currentLayout, StringComparison.OrdinalIgnoreCase));
                if (currentLayoutFrame != null)
                    return currentLayoutFrame.LayoutName;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (AcadRuntimeException)
        {
        }
        catch (ArgumentException)
        {
        }

        return frames[0].LayoutName;
    }

    private static Extents3d CombineFrameExtents(IReadOnlyList<QqqPlotFrameInfo> frames)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("图框范围无效，无法显示打印范围。");

        var combined = frames[0].WcsExtents;
        for (var i = 1; i < frames.Count; i++)
            combined.AddExtents(frames[i].WcsExtents);

        return combined;
    }

    private static void ShowFrameRangeOrderOverlay(
        Database database,
        IReadOnlyList<QqqFrameOrderDisplayItem> frames)
    {
        ClearFrameRangeDisplay();
        if (frames.Count == 0)
            return;

        lock (RangeOverlaySync)
        {
            var transientManager = TransientManager.CurrentTransientManager;
            var viewportNumbers = new IntegerCollection();
            foreach (var item in frames)
            {
                var marker = CreateFrameOrderMarker(database, item.Frame, item.OrderIndex);
                try
                {
                    transientManager.AddTransient(marker, TransientDrawingMode.Main, 128, viewportNumbers);
                    ActiveRangeOverlayEntities.Add(marker);
                }
                catch
                {
                    marker.Dispose();
                    throw;
                }
            }
        }
    }

    private static ObjectId[] ResolveFrameObjectIds(
        Database database,
        IReadOnlyList<QqqPlotFrameInfo> frames,
        string layoutName)
    {
        if (frames.Count == 0)
            return Array.Empty<ObjectId>();

        var ids = new List<ObjectId>();
        var uniqueIds = new HashSet<ObjectId>();

        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var frame in frames)
        {
            if (!TryGetObjectIdFromHandle(database, frame.HandleText, out var objectId) ||
                objectId.IsNull ||
                !uniqueIds.Add(objectId))
            {
                continue;
            }

            if (transaction.GetObject(objectId, OpenMode.ForRead, false) is not Entity entity)
                continue;

            if (!string.Equals(GetEntityLayoutName(entity, transaction), layoutName, StringComparison.OrdinalIgnoreCase))
                continue;

            ids.Add(objectId);
        }

        return ids.ToArray();
    }

    private static MText CreateFrameOrderMarker(
        Database database,
        QqqPlotFrameInfo frame,
        int orderIndex)
    {
        var labelText = orderIndex.ToString(CultureInfo.InvariantCulture);
        var minDimension = Math.Max(1d, Math.Min(frame.Width, frame.Height));
        var textHeight = Math.Max(8d, Math.Min(minDimension * 0.12d, 80d));
        var labelWidth = Math.Max(textHeight * 1.9d, labelText.Length * textHeight * 0.95d);
        var centerPoint = new Point3d(
            (frame.WcsExtents.MinPoint.X + frame.WcsExtents.MaxPoint.X) * 0.5d,
            (frame.WcsExtents.MinPoint.Y + frame.WcsExtents.MaxPoint.Y) * 0.5d,
            frame.WcsExtents.MaxPoint.Z);

        return new MText
        {
            Contents = labelText,
            Location = centerPoint,
            Attachment = AttachmentPoint.MiddleCenter,
            TextHeight = textHeight,
            Width = labelWidth,
            TextStyleId = database.Textstyle,
            Layer = "0",
            Normal = Vector3d.ZAxis,
            Rotation = 0d,
            BackgroundFill = true,
            UseBackgroundColor = false,
            BackgroundScaleFactor = 1.12,
            BackgroundFillColor = RangeOrderFillColor,
            Color = RangeOrderTextColor
        };
    }

    private static bool TryGetObjectIdFromHandle(Database database, string? handleText, out ObjectId objectId)
    {
        objectId = ObjectId.Null;
        var text = (handleText ?? "").Trim();
        if (text.Length == 0)
            return false;

        if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handleValue))
            return false;

        try
        {
            objectId = database.GetObjectId(false, new Handle(handleValue), 0);
            return !objectId.IsNull;
        }
        catch (AcadRuntimeException)
        {
            objectId = ObjectId.Null;
            return false;
        }
        catch (InvalidOperationException)
        {
            objectId = ObjectId.Null;
            return false;
        }
        catch (ArgumentException)
        {
            objectId = ObjectId.Null;
            return false;
        }
    }

    private static string GetEntityLayoutName(Entity entity, Transaction transaction)
    {
        try
        {
            if (transaction.GetObject(entity.OwnerId, OpenMode.ForRead, false) is BlockTableRecord owner &&
                owner.IsLayout &&
                !owner.LayoutId.IsNull &&
                transaction.GetObject(owner.LayoutId, OpenMode.ForRead, false) is Layout layout)
            {
                return layout.LayoutName;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (AcadRuntimeException)
        {
        }
        catch (ArgumentException)
        {
        }

        return "";
    }

    private static List<string> ReadNonEmptyValues(System.Collections.Specialized.StringCollection values)
    {
        var result = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        return result;
    }

    private sealed class QqqFrameOrderDisplayItem
    {
        internal QqqFrameOrderDisplayItem(QqqPlotFrameInfo frame, int orderIndex)
        {
            Frame = frame;
            OrderIndex = orderIndex;
        }

        internal QqqPlotFrameInfo Frame { get; }

        internal int OrderIndex { get; }
    }

    private sealed class SpatialFrameGroup
    {
        private int _count;

        internal SpatialFrameGroup(double referenceValue)
        {
            ReferenceValue = referenceValue;
        }

        internal double ReferenceValue { get; private set; }

        internal List<QqqPlotFrameInfo> Frames { get; } = new();

        internal void Add(QqqPlotFrameInfo frame, double axisValue)
        {
            Frames.Add(frame);
            ReferenceValue = ((ReferenceValue * _count) + axisValue) / (_count + 1);
            _count++;
        }
    }

    private static PlotPreparationResult PreparePlotOptions(
        QqqPlotOptions options,
        PlotDeviceRequirement deviceRequirement)
    {
        var result = new PlotPreparationResult
        {
            Options = ClonePlotOptions(options)
        };

        if (result.Options.UseCadPlotSettings || !string.IsNullOrWhiteSpace(result.Options.PageSetupName))
            return result;

        try
        {
            var validator = PlotSettingsValidator.Current;
            RefreshPlotLists(validator);

            var availableDevices = ReadNonEmptyValues(validator.GetPlotDeviceList());
            if (availableDevices.Count == 0)
            {
                result.ErrorMessage = "当前系统未找到可用打印设备。";
                return result;
            }

            var printerName = ResolvePrinterName(
                result.Options.PrinterName,
                availableDevices,
                deviceRequirement,
                result.InfoMessages,
                out var printerError);
            if (printerError.Length > 0)
            {
                result.ErrorMessage = printerError;
                return result;
            }

            result.Options.PrinterName = printerName;
            result.Options.StyleSheet = ResolveStyleSheetName(result.Options.StyleSheet, validator, result.InfoMessages);
            result.Options.MediaName = ResolveMediaName(result.Options.MediaName, printerName, validator, result.InfoMessages);
            if (string.Equals(result.Options.MediaName, PrintSaveService.MediaAutoMatchText, StringComparison.OrdinalIgnoreCase))
                result.Options.AdaptPaper = true;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_QQQ 读取打印设备配置失败", ex);
            result.ErrorMessage = "读取打印机或纸张配置失败，请先检查 V_YYY“打印与保存”设置。";
        }

        return result;
    }

    private static QqqPlotOptions ClonePlotOptions(QqqPlotOptions options)
    {
        return new QqqPlotOptions
        {
            UseCadPlotSettings = options.UseCadPlotSettings,
            PageSetupName = options.PageSetupName,
            PrinterName = options.PrinterName,
            MediaName = options.MediaName,
            StyleSheet = options.StyleSheet,
            ScaleText = options.ScaleText,
            FileNameTemplate = options.FileNameTemplate,
            TadLabelName = options.TadLabelName,
            AutoRotate = options.AutoRotate,
            CenterPlot = options.CenterPlot,
            OffsetX = options.OffsetX,
            OffsetY = options.OffsetY,
            FitToPaper = options.FitToPaper,
            AdaptPaper = options.AdaptPaper,
            ScaleLineweights = options.ScaleLineweights,
            Landscape = options.Landscape,
            UpsideDown = options.UpsideDown,
            SortRule = options.SortRule,
            PlotToFile = options.PlotToFile,
            OutputFolder = options.OutputFolder
        };
    }

    private static void RefreshPlotLists(PlotSettingsValidator validator)
    {
        using var settings = new PlotSettings(false);
        validator.RefreshLists(settings);
    }

    private static string ResolvePrinterName(
        string? requestedPrinter,
        IReadOnlyList<string> availableDevices,
        PlotDeviceRequirement deviceRequirement,
        List<string> infoMessages,
        out string errorMessage)
    {
        errorMessage = "";

        var normalizedRequest = (requestedPrinter ?? "").Trim();
        var matchedRequest = FindMatchingValue(availableDevices, normalizedRequest);
        if (!string.IsNullOrWhiteSpace(matchedRequest) &&
            IsPrinterAccepted(matchedRequest, deviceRequirement))
        {
            return matchedRequest;
        }

        var fallbackPrinter = FindPreferredPrinter(availableDevices, deviceRequirement);
        if (fallbackPrinter.Length == 0)
        {
            errorMessage = deviceRequirement switch
            {
                PlotDeviceRequirement.Pdf => "未找到可用 PDF 打印机，请先在 V_YYY“打印与保存”中选择有效 PDF 输出设备。",
                PlotDeviceRequirement.File => "未找到可用文件打印机，请先在 V_YYY“打印与保存”中选择有效输出设备。",
                _ => UIMessages.Errors.InvalidPrinter
            };
            return "";
        }

        if (!string.IsNullOrWhiteSpace(matchedRequest))
        {
            var reason = deviceRequirement switch
            {
                PlotDeviceRequirement.Pdf => "不是 PDF 输出设备",
                PlotDeviceRequirement.File => "不是文件输出设备",
                _ => "当前不可用"
            };
            infoMessages.Add($"打印机“{matchedRequest}”{reason}，已自动切换为“{fallbackPrinter}”。");
        }
        else if (!PrintSaveService.IsNoPlotDeviceName(normalizedRequest))
        {
            infoMessages.Add($"打印机“{normalizedRequest}”不可用，已自动切换为“{fallbackPrinter}”。");
        }

        return fallbackPrinter;
    }

    private static bool IsPrinterAccepted(string printerName, PlotDeviceRequirement requirement)
    {
        return requirement switch
        {
            PlotDeviceRequirement.Pdf => IsLikelyPdfPlotter(printerName),
            PlotDeviceRequirement.File => IsLikelyFilePlotter(printerName),
            _ => !PrintSaveService.IsNoPlotDeviceName(printerName)
        };
    }

    private static string FindPreferredPrinter(IReadOnlyList<string> availableDevices, PlotDeviceRequirement requirement)
    {
        if (availableDevices.Count == 0)
            return "";

        var defaultPdfPrinter = FindMatchingValue(availableDevices, PrintSaveService.DefaultPrinterName);
        if (!string.IsNullOrWhiteSpace(defaultPdfPrinter) &&
            IsPrinterAccepted(defaultPdfPrinter, requirement))
        {
            return defaultPdfPrinter;
        }

        foreach (var device in availableDevices)
        {
            if (IsPrinterAccepted(device, requirement))
                return device;
        }

        return "";
    }

    private static string ResolveStyleSheetName(
        string? requestedStyleSheet,
        PlotSettingsValidator validator,
        List<string> infoMessages)
    {
        var styleSheet = (requestedStyleSheet ?? "").Trim();
        if (styleSheet.Length == 0)
            return "";

        RefreshPlotLists(validator);
        var availableStyles = ReadNonEmptyValues(validator.GetPlotStyleSheetList());
        var matchedStyle = FindMatchingValue(availableStyles, styleSheet);
        if (!string.IsNullOrWhiteSpace(matchedStyle))
            return matchedStyle;

        infoMessages.Add($"打印样式“{styleSheet}”不可用，已沿用当前布局样式。");
        return "";
    }

    private static string ResolveMediaName(
        string? requestedMediaName,
        string printerName,
        PlotSettingsValidator validator,
        List<string> infoMessages)
    {
        var mediaName = (requestedMediaName ?? "").Trim();
        if (mediaName.Length == 0 ||
            string.Equals(mediaName, PrintSaveService.MediaAutoMatchText, StringComparison.OrdinalIgnoreCase))
        {
            return PrintSaveService.MediaAutoMatchText;
        }

        using var settings = new PlotSettings(false);
        validator.SetPlotConfigurationName(settings, printerName, null);
        validator.RefreshLists(settings);

        var availableMediaNames = ReadNonEmptyValues(validator.GetCanonicalMediaNameList(settings));
        var matchedMediaName = FindMatchingValue(availableMediaNames, mediaName);
        if (!string.IsNullOrWhiteSpace(matchedMediaName))
            return matchedMediaName;

        var normalizedMediaName = NormalizePaperRequest(mediaName);
        matchedMediaName = availableMediaNames.FirstOrDefault(x =>
            x.IndexOf(mediaName, StringComparison.OrdinalIgnoreCase) >= 0 ||
            x.IndexOf(normalizedMediaName, StringComparison.OrdinalIgnoreCase) >= 0) ?? "";
        if (matchedMediaName.Length > 0)
            return matchedMediaName;

        infoMessages.Add($"纸张“{mediaName}”不适用于“{printerName}”，已改为自动匹配。");
        return PrintSaveService.MediaAutoMatchText;
    }

    private static string FindMatchingValue(IReadOnlyList<string> values, string requestedValue)
    {
        if (requestedValue.Length == 0)
            return "";

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (string.Equals(value, requestedValue, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return "";
    }

    private static CadSystemVariableSnapshot CaptureForegroundPlotState()
    {
        var snapshot = CadSystemVariableService.Capture(BackgroundPlotSystemVariableName);
        if (CadSystemVariableService.TryGetInt32(BackgroundPlotSystemVariableName, out var currentValue) &&
            currentValue != 0)
        {
            CadSystemVariableService.TrySetValue(BackgroundPlotSystemVariableName, 0, out _);
        }

        return snapshot;
    }

    private static void RestoreForegroundPlotState(CadSystemVariableSnapshot snapshot)
    {
        snapshot.TryRestoreAll();
    }

    private enum PlotDeviceRequirement
    {
        Any,
        File,
        Pdf
    }

    private sealed class PlotPreparationResult
    {
        internal QqqPlotOptions Options { get; set; } = new();
        internal List<string> InfoMessages { get; } = new();
        internal string ErrorMessage { get; set; } = "";
    }

    private sealed class PreservedPlotFilesResult
    {
        internal string FolderPath { get; set; } = "";
        internal int SavedCount { get; set; }
        internal string ErrorMessage { get; set; } = "";
    }

    private sealed class OutputFileNameContext
    {
        public OutputFileNameContext(QqqFileNameTemplateRenderer renderer, string extension)
        {
            Renderer = renderer;
            Extension = string.IsNullOrWhiteSpace(extension) ? ".plt" : extension;
        }

        public QqqFileNameTemplateRenderer Renderer { get; }
        public string Extension { get; }
    }

    private sealed class QqqFileNameTemplateRenderer
    {
        private static readonly Regex TokenRegex = new(
            @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(:(?<fmt>[^}]+))?\}",
            RegexOptions.Compiled);

        private readonly IReadOnlyList<IFileNameTemplatePart> _parts;

        private QqqFileNameTemplateRenderer(IReadOnlyList<IFileNameTemplatePart> parts)
        {
            _parts = parts;
        }

        internal static QqqFileNameTemplateRenderer Create(string? template)
        {
            var source = string.IsNullOrWhiteSpace(template)
                ? "{drawing}_{layout}_{index:000}_{frame}"
                : template?.Trim() ?? "";
            var parts = new List<IFileNameTemplatePart>();
            var lastIndex = 0;
            foreach (Match match in TokenRegex.Matches(source))
            {
                if (match.Index > lastIndex)
                    parts.Add(new LiteralTemplatePart(source.Substring(lastIndex, match.Index - lastIndex)));

                parts.Add(new TokenTemplatePart(
                    match.Value,
                    match.Groups["name"].Value,
                    match.Groups["fmt"].Success ? match.Groups["fmt"].Value : null));
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < source.Length)
                parts.Add(new LiteralTemplatePart(source.Substring(lastIndex)));

            return new QqqFileNameTemplateRenderer(parts);
        }

        internal string Render(
            string drawingName,
            QqqPlotFrameInfo frame,
            int plotIndex,
            string extension)
        {
            var context = new FileNameTemplateContext(drawingName, frame, plotIndex, extension, DateTime.Now);
            var sb = new StringBuilder();
            foreach (var part in _parts)
                part.AppendTo(sb, context);
            return sb.ToString();
        }
    }

    private readonly struct FileNameTemplateContext
    {
        public FileNameTemplateContext(
            string drawingName,
            QqqPlotFrameInfo frame,
            int plotIndex,
            string extension,
            DateTime now)
        {
            DrawingName = drawingName;
            Frame = frame;
            PlotIndex = plotIndex;
            Extension = extension;
            Now = now;
        }

        public string DrawingName { get; }
        public QqqPlotFrameInfo Frame { get; }
        public int PlotIndex { get; }
        public string Extension { get; }
        public DateTime Now { get; }
    }

    private interface IFileNameTemplatePart
    {
        void AppendTo(StringBuilder sb, FileNameTemplateContext context);
    }

    private sealed class LiteralTemplatePart : IFileNameTemplatePart
    {
        private readonly string _value;

        public LiteralTemplatePart(string value)
        {
            _value = value;
        }

        public void AppendTo(StringBuilder sb, FileNameTemplateContext context)
        {
            sb.Append(_value);
        }
    }

    private sealed class TokenTemplatePart : IFileNameTemplatePart
    {
        private readonly string _source;
        private readonly string _token;
        private readonly string? _format;

        public TokenTemplatePart(string source, string token, string? format)
        {
            _source = source;
            _token = token.ToLowerInvariant();
            _format = format;
        }

        public void AppendTo(StringBuilder sb, FileNameTemplateContext context)
        {
            var value = ResolveValue(context);
            if (value == null)
            {
                sb.Append(_source);
                return;
            }

            if (value is IFormattable formattable)
                sb.Append(formattable.ToString(_format, CultureInfo.InvariantCulture) ?? "");
            else
                sb.Append(value);
        }

        private object? ResolveValue(FileNameTemplateContext context)
        {
            var frame = context.Frame;
            return _token switch
            {
                "drawing" => context.DrawingName,
                "layout" => frame.LayoutName,
                "space" => frame.SpaceName,
                "frame" => frame.FrameName,
                "layer" => frame.LayerName,
                "type" => frame.FrameType,
                "handle" => frame.HandleText,
                "index" => context.PlotIndex,
                "width" => frame.Width,
                "height" => frame.Height,
                "centerx" => frame.CenterX,
                "centery" => frame.CenterY,
                "area" => frame.Area,
                "date" => context.Now,
                "time" => context.Now,
                "datetime" => context.Now,
                "ext" => context.Extension,
                _ => null
            };
        }
    }
}
