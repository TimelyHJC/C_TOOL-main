using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using System.Windows;

namespace C_toolsPlugin;

/// <summary>
/// C_TOOL 主插件下与布局视口、标注比例相关的辅助命令。
/// </summary>
internal static class ViewportCommandService
{
    private const string AnnotationScaleCollectionName = "ACDB_ANNOTATIONSCALES";
    private const string ModelLayoutName = "Model";
    private const double MinimumViewportSpan = 1e-6;
    private static readonly Regex s_ratioPattern = new(
        @"\d+(?:\.\d+)?\s*:\s*\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_styleScalePattern = new(
        @"^.{1,2}\s*-\s*(?<denominator>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static void LockViewports() => SetViewportLocked(true);

    internal static void UnlockViewports() => SetViewportLocked(false);

    internal static void ReportCurrentViewportScale()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var db = doc.Database;
        if (db.TileMode)
        {
            ed.WriteMessage("\nC_TOOL：当前在模型空间，F_GV 仅支持布局视口。");
            return;
        }

        try
        {
            if (!TryResolveViewportIdForScaleReport(doc, out var viewportId, out var hintMessage, out _))
            {
                if (!string.IsNullOrWhiteSpace(hintMessage))
                    ed.WriteMessage("\nC_TOOL：" + hintMessage);
                return;
            }

            var scaleInfo = CadDatabaseScope.Read(
                doc,
                (_, tr) =>
                {
                    if (!CadDatabaseScope.TryOpenAs<Viewport>(tr, viewportId, OpenMode.ForRead, out var viewport) ||
                        viewport == null ||
                        viewport.Number == 1)
                    {
                        return (RatioText: "", ErrorMessage: "未找到有效的布局视口。");
                    }

                    var ratioText = FormatViewportScaleRatio(viewport.CustomScale);
                    return ratioText.Length == 0
                        ? (RatioText: "", ErrorMessage: "当前视口比例信息不可用。")
                        : (RatioText: ratioText, ErrorMessage: "");
                },
                requireDocumentLock: true);

            if (scaleInfo.ErrorMessage.Length > 0)
            {
                ed.WriteMessage("\nC_TOOL：" + scaleInfo.ErrorMessage);
                return;
            }

            var ratioText = scaleInfo.RatioText;
            ed.WriteMessage($"\n当前比例是 {ratioText}。");
            if (!TryPromptViewportScaleLabelText(ed, ratioText, out var labelText))
                return;

            TryInsertViewportScaleLabel(doc, db, viewportId, labelText, out var insertMessage);
            if (insertMessage.Length > 0)
                ed.WriteMessage("\nC_TOOL：" + insertMessage);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_GV 读取视口比例失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：F_GV 失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_GV 读取视口比例失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：F_GV 失败：{ex.Message}");
        }
    }

    internal static void CreateViewportWindowFromModelSelection()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var db = doc.Database;
        var originalLayoutName = GetCurrentLayoutName();
        var restoreOriginalLayoutOnCancel = !IsModelLayoutName(originalLayoutName);

        try
        {
            if (!TryResolveViewportWindowTargetLayout(doc, db, originalLayoutName, out var targetLayoutName, out var layoutMessage))
            {
                TryWriteViewportWindowMessage(doc, layoutMessage);
                return;
            }

            if (!TrySwitchCurrentLayout(ModelLayoutName, PluginCommandIds.ViewportWindow, out var switchToModelMessage))
            {
                TryWriteViewportWindowMessage(doc, switchToModelMessage);
                return;
            }

            if (!TryPromptViewportWindowSelection(ed, out var selection, out var selectionMessage))
            {
                RestoreViewportWindowLayoutIfNeeded(doc, targetLayoutName, restoreOriginalLayoutOnCancel);
                TryWriteViewportWindowMessage(doc, selectionMessage);
                return;
            }

            if (!TryPromptViewportWindowScale(doc, db, out var customScale, out var scaleDisplayText, out var scaleMessage))
            {
                RestoreViewportWindowLayoutIfNeeded(doc, targetLayoutName, restoreOriginalLayoutOnCancel);
                TryWriteViewportWindowMessage(doc, scaleMessage);
                return;
            }

            if (!TrySwitchCurrentLayout(targetLayoutName, PluginCommandIds.ViewportWindow, out var switchToLayoutMessage) ||
                !TryEnsurePaperSpace(doc, PluginCommandIds.ViewportWindow, out switchToLayoutMessage))
            {
                TryWriteViewportWindowMessage(doc, switchToLayoutMessage);
                return;
            }

            var pointResult = ed.GetPoint($"\nC_TOOL：在布局“{targetLayoutName}”中单击放置视口中心点：");
            if (pointResult.Status != PromptStatus.OK)
            {
                TryWriteViewportWindowMessage(doc, "F_VW 已取消。");
                return;
            }

            if (!TryCreateViewportWindow(doc, db, targetLayoutName, selection, customScale, pointResult.Value, scaleDisplayText, out var createMessage))
            {
                TryWriteViewportWindowMessage(doc, createMessage);
                return;
            }

            TryRegenAfterScaleOrStyleChange(doc, changed: true);
            TryWriteViewportWindowMessage(doc, createMessage);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_VW 创建视口失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：F_VW 失败：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_VW 创建视口失败（参数错误）", ex);
            ed.WriteMessage($"\nC_TOOL：F_VW 失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_VW 创建视口失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：F_VW 失败：{ex.Message}");
        }
    }

    internal static void PickAnnotationScale()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;

        try
        {
            var window = new AnnotationScaleWindow();
            _ = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DE 标注比例窗口失败（无效操作）", ex);
            TryWriteAnnotationScaleMessage(doc, "F_DE 打开失败：" + ex.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DE 标注比例窗口失败（CAD）", ex);
            TryWriteAnnotationScaleMessage(doc, "F_DE 打开失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_DE 标注比例窗口失败", ex);
            TryWriteAnnotationScaleMessage(doc, "F_DE 打开失败：" + ex.Message);
        }
    }

    internal static void CloseAnnotationScaleWindowIfAny()
    {
        // F_DE 已改为按需弹窗，不再维护持久浮窗实例。
    }

    internal static AnnotationScaleSnapshot ReadAnnotationScaleSnapshot(out string message)
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            message = "无活动图纸，无法读取标注比例。";
            return AnnotationScaleSnapshot.Empty;
        }

        var db = doc.Database;
        var currentScaleName = db.Cannoscale?.Name ?? "";
        var scales = GetAnnotationScales(db);
        if (scales.Count == 0)
        {
            message = "当前图纸未找到可用的标注比例。";
            return new AnnotationScaleSnapshot(
                Array.Empty<AnnotationScaleGroupInfo>(),
                Array.Empty<AnnotationScaleListItem>(),
                currentScaleName);
        }

        var dimStyleNames = new List<string>();
        try
        {
            var snapshotData = CadDatabaseScope.Read(
                doc,
                (database, tr) =>
                {
                    var dimStyles = ListDimStyleNames(tr, database);
                    var resolvedScaleName = currentScaleName;
                    if (!database.TileMode &&
                        TryResolveLayoutViewportIdForScaleChange(tr, doc, database, out var viewportId, out _) &&
                        CadDatabaseScope.TryOpenAs<Viewport>(tr, viewportId, OpenMode.ForRead, out var viewport) &&
                        viewport != null &&
                        viewport.Number != 1)
                    {
                        var ratioText = FormatViewportScaleRatio(viewport.CustomScale);
                        if (!string.IsNullOrWhiteSpace(ratioText))
                            resolvedScaleName = ratioText;
                    }

                    return (DimStyleNames: dimStyles, CurrentScaleName: resolvedScaleName);
                },
                requireDocumentLock: true);

            dimStyleNames = snapshotData.DimStyleNames;
            currentScaleName = snapshotData.CurrentScaleName;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 F_DE 标注样式分组失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 F_DE 标注样式分组失败（CAD）", ex);
        }

        var groups = AnnotationScaleGrouping.ListGroups(scales, dimStyleNames);
        message = $"已载入 {scales.Count} 个标注比例。";
        return new AnnotationScaleSnapshot(groups, scales, currentScaleName);
    }

    internal static bool TryApplyAnnotationScale(
        string requestedScale,
        string? selectedGroupPrefix,
        bool preferInnerDimStyle,
        out string appliedScaleName,
        out string message)
    {
        appliedScaleName = "";

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            message = "无活动图纸，无法修改标注比例。";
            return false;
        }

        var db = doc.Database;

        try
        {
            var scales = GetAnnotationScales(db);
            if (scales.Count == 0)
            {
                message = "当前图纸未找到可用的标注比例。";
                return false;
            }

            var selected = ResolveAnnotationScale(scales, requestedScale);
            if (selected == null)
            {
                message = $"未找到标注比例：{requestedScale.Trim()}。";
                return false;
            }

            appliedScaleName = selected.Name;
            var scaleChanged = false;
            var annotationScaleChanged = false;
            var dimStyleChanged = false;
            var matchedDimStyle = false;
            var dimStyleName = "";
            var isLayoutMode = !db.TileMode;
            var scaleSubject = isLayoutMode ? "当前视口比例" : "当前标注比例";

            using (doc.LockDocument())
            {
                if (isLayoutMode)
                {
                    if (!TryApplyLayoutViewportScale(
                            doc,
                            db,
                            selected,
                            out scaleChanged,
                            out var resolvedScaleDisplay,
                            out var scaleErrorMessage))
                    {
                        message = scaleErrorMessage;
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(resolvedScaleDisplay))
                        appliedScaleName = resolvedScaleDisplay;
                }
                else
                {
                    scaleChanged = false;
                }

                var currentAnnotationScaleName = db.Cannoscale?.Name ?? "";
                annotationScaleChanged = !string.Equals(selected.Name, currentAnnotationScaleName, StringComparison.OrdinalIgnoreCase);
                if (annotationScaleChanged &&
                    !CadSystemVariableService.TrySetValue(SystemVariableNames.Cannoscale, selected.Name))
                {
                    message = "修改标注比例失败：无法设置当前标注比例。";
                    return false;
                }

                dimStyleChanged = CadDatabaseScope.Write(
                    doc,
                    (_, tr) => TryApplyCurrentDimStyleByScale(
                        tr,
                        db,
                        selected,
                        selectedGroupPrefix,
                        preferInnerDimStyle,
                        out matchedDimStyle,
                        out dimStyleName));

                if (matchedDimStyle)
                    CurrentDimStyleSync.TrySyncToSystemVariable(dimStyleName, "F_DE");

                TryRegenAfterScaleOrStyleChange(doc, scaleChanged || annotationScaleChanged || dimStyleChanged);
            }

            var scaleMessage = scaleChanged
                ? $"{scaleSubject}已切换为 {GetScaleDisplayText(selected)}"
                : $"{scaleSubject}已是 {GetScaleDisplayText(selected)}";
            if (dimStyleChanged)
            {
                message = $"{scaleMessage}，当前标注样式已切换为 {dimStyleName}。";
            }
            else if (matchedDimStyle && !string.IsNullOrWhiteSpace(dimStyleName))
            {
                message = $"{scaleMessage}，当前标注样式已是 {dimStyleName}。";
            }
            else
            {
                message = $"{scaleMessage}（未匹配到可切换的标注样式）。";
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DE 修改标注比例失败（无效操作）", ex);
            message = "修改标注比例失败：" + ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DE 修改标注比例失败（参数错误）", ex);
            message = "修改标注比例失败：" + ex.Message;
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DE 修改标注比例失败（CAD）", ex);
            message = "修改标注比例失败：" + ex.Message;
            return false;
        }
    }

    private static bool TryApplyLayoutViewportScale(
        Document doc,
        Database db,
        AnnotationScaleListItem selectedScale,
        out bool scaleChanged,
        out string scaleDisplayText,
        out string message)
    {
        scaleChanged = false;
        scaleDisplayText = GetScaleDisplayText(selectedScale);
        message = "";

        if (!TryGetViewportCustomScale(selectedScale, out var targetCustomScale))
        {
            message = $"无效比例：{selectedScale.Name}。";
            return false;
        }

        const double tolerance = 1e-9;
        var previewData = CadDatabaseScope.Read(
            db,
            (database, tr) =>
            {
                if (!TryResolveLayoutViewportIdForScaleChange(tr, doc, database, out var resolvedViewportId, out var resolveMessage))
                {
                    return (
                        Success: false,
                        ViewportId: ObjectId.Null,
                        NeedsViewportScaleChange: false,
                        TargetIsCurrentFloatingViewport: false,
                        ErrorMessage: resolveMessage);
                }

                if (!CadDatabaseScope.TryOpenAs<Viewport>(tr, resolvedViewportId, OpenMode.ForRead, out var previewViewport) ||
                    previewViewport == null ||
                    previewViewport.Number == 1)
                {
                    return (
                        Success: false,
                        ViewportId: ObjectId.Null,
                        NeedsViewportScaleChange: false,
                        TargetIsCurrentFloatingViewport: false,
                        ErrorMessage: "未找到可用的布局视口。");
                }

                var isCurrentFloatingViewport =
                    TryGetCurrentFloatingViewportId(doc, out var currentViewportId) &&
                    currentViewportId == resolvedViewportId;

                return (
                    Success: true,
                    ViewportId: resolvedViewportId,
                    NeedsViewportScaleChange: Math.Abs(previewViewport.CustomScale - targetCustomScale) > tolerance,
                    TargetIsCurrentFloatingViewport: isCurrentFloatingViewport,
                    ErrorMessage: "");
            });

        if (!previewData.Success)
        {
            message = previewData.ErrorMessage;
            return false;
        }

        var viewportId = previewData.ViewportId;
        var needsViewportScaleChange = previewData.NeedsViewportScaleChange;
        var targetIsCurrentFloatingViewport = previewData.TargetIsCurrentFloatingViewport;

        if (targetIsCurrentFloatingViewport && needsViewportScaleChange)
        {
            try
            {
                doc.Editor.SwitchToPaperSpace();
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_DE 切换到视口外后修改比例失败（无效操作）", ex);
                message = "当前在视口内，无法安全切换比例：" + ex.Message;
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_DE 切换到视口外后修改比例失败（CAD）", ex);
                message = "当前在视口内，无法安全切换比例：" + ex.Message;
                return false;
            }
        }

        var initialScaleDisplayText = scaleDisplayText;
        var writeResult = CadDatabaseScope.Write(
            db,
            (_, tr) =>
            {
                if (!CadDatabaseScope.TryOpenAs<Viewport>(tr, viewportId, OpenMode.ForWrite, out var viewport) ||
                    viewport == null ||
                    viewport.Number == 1)
                {
                    return (
                        Success: false,
                        ScaleChanged: false,
                        ResolvedScaleDisplayText: initialScaleDisplayText,
                        ErrorMessage: "未找到可用的布局视口。");
                }

                var changed = Math.Abs(viewport.CustomScale - targetCustomScale) > tolerance;
                if (changed)
                {
                    var restoreLocked = viewport.Locked;
                    if (restoreLocked)
                        viewport.Locked = false;

                    viewport.CustomScale = targetCustomScale;

                    if (restoreLocked)
                        viewport.Locked = true;
                }

                var resolvedScaleDisplayText = initialScaleDisplayText;
                if (string.IsNullOrWhiteSpace(resolvedScaleDisplayText))
                {
                    var ratioText = FormatViewportScaleRatio(targetCustomScale);
                    resolvedScaleDisplayText = string.IsNullOrWhiteSpace(ratioText)
                        ? selectedScale.Name
                        : ratioText;
                }

                return (
                    Success: true,
                    ScaleChanged: changed,
                    ResolvedScaleDisplayText: resolvedScaleDisplayText,
                    ErrorMessage: "");
            });

        if (!writeResult.Success)
        {
            message = writeResult.ErrorMessage;
            return false;
        }

        scaleChanged = writeResult.ScaleChanged;
        scaleDisplayText = writeResult.ResolvedScaleDisplayText;
        return true;
    }

    private static bool TryGetViewportCustomScale(AnnotationScaleListItem selectedScale, out double customScale)
    {
        customScale = double.NaN;
        if (selectedScale.PaperUnits <= 0.0 ||
            selectedScale.DrawingUnits <= 0.0 ||
            double.IsNaN(selectedScale.PaperUnits) ||
            double.IsInfinity(selectedScale.PaperUnits) ||
            double.IsNaN(selectedScale.DrawingUnits) ||
            double.IsInfinity(selectedScale.DrawingUnits))
            return false;

        customScale = selectedScale.PaperUnits / selectedScale.DrawingUnits;
        return !double.IsNaN(customScale) && !double.IsInfinity(customScale) && customScale > 0.0;
    }

    private static bool TryResolveViewportWindowTargetLayout(
        Document doc,
        Database db,
        string originalLayoutName,
        out string layoutName,
        out string message)
    {
        layoutName = "";
        message = "";

        if (!string.IsNullOrWhiteSpace(originalLayoutName) && !IsModelLayoutName(originalLayoutName))
        {
            layoutName = originalLayoutName;
            return layoutName.Length > 0;
        }

        var paperLayouts = ListPaperLayoutNames(db);
        if (paperLayouts.Count == 0)
        {
            message = "当前图纸没有可用布局，无法执行 F_VW。";
            return false;
        }

        if (paperLayouts.Count == 1)
        {
            layoutName = paperLayouts[0];
            return true;
        }

        var defaultLayout = paperLayouts[0];
        var options = new PromptStringOptions(
            $"\nC_TOOL：输入目标布局名 <{defaultLayout}>（可选：{string.Join("、", paperLayouts)}）：")
        {
            AllowSpaces = true,
            DefaultValue = defaultLayout,
            UseDefaultValue = true
        };

        var result = doc.Editor.GetString(options);
        if (result.Status == PromptStatus.None)
        {
            layoutName = defaultLayout;
            return true;
        }

        if (result.Status != PromptStatus.OK)
        {
            message = "F_VW 已取消。";
            return false;
        }

        var requested = (result.StringResult ?? "").Trim();
        if (requested.Length == 0)
            requested = defaultLayout;

        foreach (var candidate in paperLayouts)
        {
            if (!string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                continue;

            layoutName = candidate;
            return true;
        }

        message = $"未找到布局：{requested}。";
        return false;
    }

    private static List<string> ListPaperLayoutNames(Database db)
    {
        var orderedLayouts = CadDatabaseScope.Read(
            db,
            (database, tr) =>
            {
                var layouts = new List<(int TabOrder, string Name)>();
                var layoutDictionary = CadDatabaseScope.OpenAs<DBDictionary>(tr, database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDictionary)
                {
                    if (!CadDatabaseScope.TryOpenAs<Layout>(tr, entry.Value, OpenMode.ForRead, out var layout) ||
                        layout == null ||
                        layout.ModelType)
                    {
                        continue;
                    }

                    var name = layout.LayoutName ?? entry.Key ?? "";
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    layouts.Add((layout.TabOrder, name));
                }

                return layouts;
            });

        return orderedLayouts
            .OrderBy(item => item.TabOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)
            .ToList();
    }

    private static bool TrySwitchCurrentLayout(string layoutName, string commandName, out string message)
    {
        message = "";
        var normalizedLayout = layoutName?.Trim() ?? "";
        if (normalizedLayout.Length == 0)
        {
            message = $"{commandName} 未指定目标布局。";
            return false;
        }

        try
        {
            var layoutManager = LayoutManager.Current;
            if (!layoutManager.LayoutExists(normalizedLayout))
            {
                message = $"未找到布局或空间“{normalizedLayout}”。";
                return false;
            }

            if (!string.Equals(layoutManager.CurrentLayout, normalizedLayout, StringComparison.OrdinalIgnoreCase))
                layoutManager.CurrentLayout = normalizedLayout;

            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandName} 切换布局失败（无效操作）", ex);
            message = $"{commandName} 切换布局失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandName} 切换布局失败（参数错误）", ex);
            message = $"{commandName} 切换布局失败：{ex.Message}";
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandName} 切换布局失败（CAD）", ex);
            message = $"{commandName} 切换布局失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryEnsurePaperSpace(Document doc, string commandName, out string message)
    {
        message = "";
        if (doc.Database.TileMode || TryGetCvPort() <= 1)
            return true;

        try
        {
            doc.Editor.SwitchToPaperSpace();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandName} 切回纸空间失败（无效操作）", ex);
            message = $"{commandName} 切回纸空间失败：{ex.Message}";
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandName} 切回纸空间失败（CAD）", ex);
            message = $"{commandName} 切回纸空间失败：{ex.Message}";
            return false;
        }
    }

    private static void RestoreViewportWindowLayoutIfNeeded(Document doc, string targetLayoutName, bool restore)
    {
        if (!restore || string.IsNullOrWhiteSpace(targetLayoutName))
            return;

        if (!TrySwitchCurrentLayout(targetLayoutName, PluginCommandIds.ViewportWindow, out _))
            return;

        TryEnsurePaperSpace(doc, PluginCommandIds.ViewportWindow, out _);
    }

    private static bool TryPromptViewportWindowSelection(
        Editor ed,
        out ViewportWindowSelection selection,
        out string message)
    {
        selection = default;
        message = "";

        var firstResult = ed.GetPoint("\nC_TOOL：F_VW 指定模型范围第一角点：");
        if (firstResult.Status != PromptStatus.OK)
        {
            message = "F_VW 已取消。";
            return false;
        }

        var cornerOptions = new PromptCornerOptions("\nC_TOOL：指定模型范围对角点：", firstResult.Value)
        {
            UseDashedLine = true
        };
        var secondResult = ed.GetCorner(cornerOptions);
        if (secondResult.Status != PromptStatus.OK)
        {
            message = "F_VW 已取消。";
            return false;
        }

        selection = new ViewportWindowSelection(firstResult.Value, secondResult.Value);
        if (!selection.IsValid)
        {
            message = "F_VW 框选范围无效，请重新执行。";
            return false;
        }

        return true;
    }

    private static bool TryPromptViewportWindowScale(
        Document doc,
        Database db,
        out double customScale,
        out string scaleDisplayText,
        out string message)
    {
        customScale = double.NaN;
        scaleDisplayText = "";
        message = "";

        var scales = GetAnnotationScales(db);
        var defaultScaleText = ResolveViewportWindowDefaultScaleText(db, scales);
        var options = new PromptStringOptions(
            $"\nC_TOOL：输入视口比例 <{defaultScaleText}>，支持 1:100 / 1/100 / 100：")
        {
            AllowSpaces = true,
            DefaultValue = defaultScaleText,
            UseDefaultValue = true
        };

        var result = doc.Editor.GetString(options);
        string requestedScale;
        if (result.Status == PromptStatus.None)
        {
            requestedScale = defaultScaleText;
        }
        else if (result.Status == PromptStatus.OK)
        {
            requestedScale = (result.StringResult ?? "").Trim();
            if (requestedScale.Length == 0)
                requestedScale = defaultScaleText;
        }
        else
        {
            message = "F_VW 已取消。";
            return false;
        }

        if (TryResolveViewportWindowScale(scales, requestedScale, out customScale, out scaleDisplayText))
            return true;

        message = $"无效比例：{requestedScale}。支持示例：1:100、1/100、100。";
        return false;
    }

    private static string ResolveViewportWindowDefaultScaleText(
        Database db,
        IReadOnlyList<AnnotationScaleListItem> scales)
    {
        var currentScaleName = db.Cannoscale?.Name ?? "";
        if (!string.IsNullOrWhiteSpace(currentScaleName))
        {
            var currentScale = ResolveAnnotationScale(scales, currentScaleName);
            if (currentScale != null && TryGetViewportCustomScale(currentScale, out _))
                return GetScaleDisplayText(currentScale);

            if (TryParseViewportScaleInput(currentScaleName, out _, out var parsedDisplay))
                return parsedDisplay;
        }

        return "1:100";
    }

    private static bool TryResolveViewportWindowScale(
        IReadOnlyList<AnnotationScaleListItem> scales,
        string requestedScale,
        out double customScale,
        out string scaleDisplayText)
    {
        customScale = double.NaN;
        scaleDisplayText = "";

        var selectedScale = ResolveAnnotationScale(scales, requestedScale);
        if (selectedScale != null && TryGetViewportCustomScale(selectedScale, out customScale))
        {
            scaleDisplayText = GetScaleDisplayText(selectedScale);
            return true;
        }

        return TryParseViewportScaleInput(requestedScale, out customScale, out scaleDisplayText);
    }

    private static bool TryParseViewportScaleInput(
        string input,
        out double customScale,
        out string scaleDisplayText)
    {
        customScale = double.NaN;
        scaleDisplayText = "";

        var normalized = NormalizeViewportScaleInput(input);
        if (normalized.Length == 0)
            return false;

        double paperUnits;
        double drawingUnits;
        var separatorIndex = normalized.IndexOf(':');
        if (separatorIndex < 0)
            separatorIndex = normalized.IndexOf('/');

        if (separatorIndex > 0 && separatorIndex < normalized.Length - 1)
        {
            if (!TryParsePositiveScaleNumber(normalized.Substring(0, separatorIndex), out paperUnits) ||
                !TryParsePositiveScaleNumber(normalized.Substring(separatorIndex + 1), out drawingUnits))
            {
                return false;
            }
        }
        else
        {
            paperUnits = 1.0;
            if (!TryParsePositiveScaleNumber(normalized, out drawingUnits))
                return false;
        }

        customScale = paperUnits / drawingUnits;
        if (double.IsNaN(customScale) || double.IsInfinity(customScale) || customScale <= 0.0)
            return false;

        scaleDisplayText = FormatViewportScaleRatio(customScale);
        if (scaleDisplayText.Length == 0)
            scaleDisplayText = $"{FormatScaleNumber(paperUnits)}:{FormatScaleNumber(drawingUnits)}";
        return true;
    }

    private static string NormalizeViewportScaleInput(string input)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0)
            return "";

        var buffer = new char[raw.Length];
        var writeIndex = 0;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            buffer[writeIndex++] = ch switch
            {
                '：' => ':',
                '／' => '/',
                '∶' => ':',
                _ => ch
            };
        }

        return new string(buffer, 0, writeIndex);
    }

    private static bool TryParsePositiveScaleNumber(string text, out double value)
    {
        value = 0.0;
        var normalized = (text ?? "").Trim();
        if (normalized.Length == 0)
            return false;

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return false;
        }

        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
    }

    private static bool TryCreateViewportWindow(
        Document doc,
        Database db,
        string targetLayoutName,
        ViewportWindowSelection selection,
        double customScale,
        Point3d paperCenterPoint,
        string scaleDisplayText,
        out string message)
    {
        message = "";
        if (!selection.IsValid || double.IsNaN(customScale) || double.IsInfinity(customScale) || customScale <= 0.0)
        {
            message = "F_VW 视口参数无效，未生成视口。";
            return false;
        }

        var paperWidth = selection.ModelWidth * customScale;
        var paperHeight = selection.ModelHeight * customScale;
        if (paperWidth <= MinimumViewportSpan || paperHeight <= MinimumViewportSpan)
        {
            message = "F_VW 生成的视口尺寸过小，请重新框选范围或调整比例。";
            return false;
        }

        try
        {
            var createMessage = "";
            var created = CadDatabaseScope.Write(
                doc,
                (database, tr) =>
                {
                    var layoutId = LayoutManager.Current.GetLayoutId(targetLayoutName);
                    if (!CadDatabaseScope.TryOpenAs<Layout>(tr, layoutId, OpenMode.ForRead, out var layout) ||
                        layout == null ||
                        layout.ModelType)
                    {
                        createMessage = $"未找到可写入的布局：{targetLayoutName}。";
                        return false;
                    }

                    if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, layout.BlockTableRecordId, OpenMode.ForWrite, out var owner) ||
                        owner == null)
                    {
                        createMessage = "未找到布局对应的图纸空间。";
                        return false;
                    }

                    var viewport = new Viewport();
                    viewport.SetDatabaseDefaults(database);
                    owner.AppendEntity(viewport);
                    tr.AddNewlyCreatedDBObject(viewport, true);

                    viewport.CenterPoint = new Point3d(paperCenterPoint.X, paperCenterPoint.Y, 0.0);
                    viewport.Width = paperWidth;
                    viewport.Height = paperHeight;
                    viewport.ViewDirection = Vector3d.ZAxis;
                    viewport.ViewCenter = selection.ViewCenter;
                    viewport.ViewHeight = selection.ModelHeight;
                    viewport.CustomScale = customScale;
                    viewport.On = true;
                    viewport.Locked = true;
                    return true;
                },
                requireDocumentLock: true);

            if (!created)
            {
                message = createMessage;
                return false;
            }

            message = $"F_VW 已在布局“{targetLayoutName}”创建视口，模型范围 {FormatScaleNumber(selection.ModelWidth)} x {FormatScaleNumber(selection.ModelHeight)}，比例 {scaleDisplayText}。";
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_VW 写入视口失败（无效操作）", ex);
            message = "F_VW 写入视口失败：" + ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_VW 写入视口失败（参数错误）", ex);
            message = "F_VW 写入视口失败：" + ex.Message;
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_VW 写入视口失败（CAD）", ex);
            message = "F_VW 写入视口失败：" + ex.Message;
            return false;
        }
    }

    private static string GetCurrentLayoutName()
    {
        try
        {
            return LayoutManager.Current.CurrentLayout ?? "";
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取当前布局失败（无效操作）", ex);
            return "";
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取当前布局失败（CAD）", ex);
            return "";
        }
    }

    private static bool IsModelLayoutName(string layoutName)
    {
        return string.Equals(layoutName?.Trim(), ModelLayoutName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetScaleDisplayText(AnnotationScaleListItem scale)
    {
        if (!string.IsNullOrWhiteSpace(scale.RatioDisplay) && !string.Equals(scale.RatioDisplay, "比例信息不可用", StringComparison.Ordinal))
            return scale.RatioDisplay;
        return scale.Name;
    }

    private static bool TryApplyCurrentDimStyleByScale(
        Transaction tr,
        Database db,
        AnnotationScaleListItem selectedScale,
        string? selectedGroupPrefix,
        bool preferInnerDimStyle,
        out bool matchedDimStyle,
        out string dimStyleName)
    {
        matchedDimStyle = false;
        dimStyleName = "";

        if (!TryResolveDimStyleByScale(
                tr,
                db,
                selectedScale,
                selectedGroupPrefix,
                preferInnerDimStyle,
                out var styleId,
                out dimStyleName))
        {
            return false;
        }

        matchedDimStyle = true;
        if (styleId.IsNull)
            return false;

        var styleChanged = db.Dimstyle != styleId;
        if (styleChanged)
            db.Dimstyle = styleId;

        var styleRecord = (DimStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
        db.SetDimstyleData(styleRecord);
        return styleChanged;
    }

    private static bool TryResolveDimStyleByScale(
        Transaction tr,
        Database db,
        AnnotationScaleListItem selectedScale,
        string? selectedGroupPrefix,
        bool preferInnerDimStyle,
        out ObjectId styleId,
        out string styleName)
    {
        styleId = ObjectId.Null;
        styleName = "";

        var selectedRatioKey = NormalizeAnnotationScaleLookupKey(selectedScale.RatioDisplay);
        if (selectedRatioKey.Length == 0)
            selectedRatioKey = NormalizeAnnotationScaleLookupKey(selectedScale.Name);
        if (selectedRatioKey.Length == 0)
            return false;

        var normalizedSelectedPrefix = AnnotationScaleGrouping.NormalizeGroupPrefix(selectedGroupPrefix) ?? "";
        var dimStyleTable = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        var exactInnerMatches = new List<(ObjectId Id, string Name)>();
        var fallbackMatches = new List<(ObjectId Id, string Name)>();

        foreach (ObjectId id in dimStyleTable)
        {
            var record = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var candidateName = record.Name ?? "";
            if (candidateName.Length == 0)
                continue;
            if (!TryExtractDimStyleRatioKey(candidateName, out var candidateRatioKey))
                continue;
            if (!string.Equals(candidateRatioKey, selectedRatioKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidatePrefix = AnnotationScaleGrouping.GetBasePrefix(candidateName) ?? "";
            if (normalizedSelectedPrefix.Length > 0 &&
                !string.Equals(candidatePrefix, normalizedSelectedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var isInnerStyle = HasInnerSuffix(candidateName);
            if (isInnerStyle == preferInnerDimStyle)
                exactInnerMatches.Add((id, candidateName));
            else
                fallbackMatches.Add((id, candidateName));
        }

        if (exactInnerMatches.Count > 0)
        {
            var selected = exactInnerMatches
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .First();
            styleId = selected.Id;
            styleName = selected.Name;
            return !styleId.IsNull && styleName.Length > 0;
        }

        if (fallbackMatches.Count > 0)
        {
            var selected = fallbackMatches
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .First();
            styleId = selected.Id;
            styleName = selected.Name;
            return !styleId.IsNull && styleName.Length > 0;
        }

        return false;
    }

    internal static void SwitchToViewportOutside()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var db = doc.Database;

        if (db.TileMode)
        {
            ed.WriteMessage("\nC_TOOL：当前在模型空间，没有可切换的视口外。");
            return;
        }

        if (TryGetCvPort() <= 1)
        {
            ed.WriteMessage("\nC_TOOL：当前已在视口外（纸空间）。");
            return;
        }

        try
        {
            ed.SwitchToPaperSpace();
            ed.WriteMessage("\nC_TOOL：已切换到视口外（纸空间）。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_AQ 切换到视口外失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：F_AQ 失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_AQ 切换到视口外失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：F_AQ 失败：{ex.Message}");
        }
    }

    private static void SetViewportLocked(bool locked)
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var db = doc.Database;
        var verb = locked ? "锁定" : "解锁";
        var commandName = locked ? PluginCommandIds.ViewportLock : PluginCommandIds.ViewportUnlock;

        if (db.TileMode)
        {
            ed.WriteMessage($"\nC_TOOL：当前在模型空间，{verb}视口请切到布局后执行。");
            return;
        }

        try
        {
            var viewportIds = CollectTargetViewportIds(doc, $"\n选择要{verb}的视口：");
            if (viewportIds.Count == 0)
            {
                ed.WriteMessage($"\nC_TOOL：未选择可{verb}的视口。");
                return;
            }

            var changeSummary = CadDatabaseScope.Write(
                doc,
                (_, tr) =>
                {
                    var changedCount = 0;
                    var unchangedCount = 0;
                    var skippedCount = 0;

                    foreach (var viewportId in viewportIds)
                    {
                        try
                        {
                            if (!CadDatabaseScope.TryOpenAs<Viewport>(tr, viewportId, OpenMode.ForWrite, out var viewport) ||
                                viewport == null ||
                                viewport.Number == 1)
                            {
                                skippedCount++;
                                continue;
                            }

                            if (viewport.Locked == locked)
                            {
                                unchangedCount++;
                                continue;
                            }

                            viewport.Locked = locked;
                            changedCount++;
                        }
                        catch (InvalidOperationException ex)
                        {
                            skippedCount++;
                            C_toolsDiagnostics.LogNonFatal($"{commandName} 处理视口失败（无效操作）", ex);
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            skippedCount++;
                            C_toolsDiagnostics.LogNonFatal($"{commandName} 处理视口失败（CAD）", ex);
                        }
                    }

                    return (Changed: changedCount, Unchanged: unchangedCount, Skipped: skippedCount);
                },
                requireDocumentLock: true);

            var changed = changeSummary.Changed;
            var unchanged = changeSummary.Unchanged;
            var skipped = changeSummary.Skipped;

            if (changed == 0 && unchanged == 0)
            {
                ed.WriteMessage($"\nC_TOOL：未找到可{verb}的有效布局视口。");
                return;
            }

            var parts = new List<string>();
            if (changed > 0)
                parts.Add($"已{verb} {changed} 个视口");
            if (unchanged > 0)
                parts.Add($"{unchanged} 个已是目标状态");
            if (skipped > 0)
                parts.Add($"跳过 {skipped} 个无效对象");

            ed.WriteMessage("\nC_TOOL：" + string.Join("，", parts) + "。");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandName} 执行失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：{commandName} 失败：{ex.Message}");
        }
    }

    private static List<AnnotationScaleListItem> GetAnnotationScales(Database db)
    {
        var result = new List<AnnotationScaleListItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ObjectContextCollection? collection;
        try
        {
            collection = db.ObjectContextManager?.GetContextCollection(AnnotationScaleCollectionName);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取标注比例集合失败（CAD）", ex);
            return result;
        }

        if (collection == null)
            return result;

        foreach (ObjectContext context in collection)
        {
            if (context is not AnnotationScale scale)
                continue;
            if (string.IsNullOrWhiteSpace(scale.Name))
                continue;
            if (!seenNames.Add(scale.Name))
                continue;
            result.Add(new AnnotationScaleListItem(scale.Name, scale.PaperUnits, scale.DrawingUnits));
        }

        result.Sort(CompareAnnotationScaleItem);
        return result;
    }

    private static List<string> ListDimStyleNames(Transaction tr, Database db)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dimStyleTable = CadDatabaseScope.OpenAs<DimStyleTable>(tr, db.DimStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in dimStyleTable)
        {
            if (!CadDatabaseScope.TryOpenAs<DimStyleTableRecord>(tr, id, OpenMode.ForRead, out var record) ||
                record == null)
            {
                continue;
            }

            var name = record.Name ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!seen.Add(name))
                continue;

            names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static int CompareAnnotationScaleItem(AnnotationScaleListItem left, AnnotationScaleListItem right)
    {
        var leftValid = !double.IsNaN(left.ScaleRatio) && !double.IsInfinity(left.ScaleRatio);
        var rightValid = !double.IsNaN(right.ScaleRatio) && !double.IsInfinity(right.ScaleRatio);
        if (leftValid != rightValid)
            return leftValid ? -1 : 1;
        if (leftValid && rightValid)
        {
            var ratioCompare = left.ScaleRatio.CompareTo(right.ScaleRatio);
            if (ratioCompare != 0)
                return ratioCompare;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static AnnotationScaleListItem? ResolveAnnotationScale(
        IEnumerable<AnnotationScaleListItem> scales,
        string requestedScale)
    {
        var exact = requestedScale.Trim();
        if (exact.Length == 0)
            return null;

        var lookupKey = NormalizeAnnotationScaleLookupKey(exact);
        foreach (var scale in scales)
        {
            if (string.Equals(scale.Name, exact, StringComparison.OrdinalIgnoreCase))
                return scale;
            if (string.Equals(NormalizeAnnotationScaleLookupKey(scale.Name), lookupKey, StringComparison.OrdinalIgnoreCase))
                return scale;
            if (string.Equals(NormalizeAnnotationScaleLookupKey(scale.RatioDisplay), lookupKey, StringComparison.OrdinalIgnoreCase))
                return scale;
        }

        return null;
    }

    private static string NormalizeAnnotationScaleLookupKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var buffer = new char[text.Length];
        var writeIndex = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            buffer[writeIndex++] = ch;
        }

        return new string(buffer, 0, writeIndex);
    }

    private static bool TryExtractDimStyleRatioKey(string styleName, out string ratioKey)
    {
        ratioKey = "";
        var trimmed = styleName?.Trim() ?? "";
        if (trimmed.Length == 0)
            return false;

        var match = s_ratioPattern.Match(trimmed);
        if (match.Success)
        {
            ratioKey = NormalizeAnnotationScaleLookupKey(match.Value);
            return ratioKey.Length > 0;
        }

        match = s_styleScalePattern.Match(trimmed);
        if (!match.Success)
            return false;

        var denominator = match.Groups["denominator"].Value;
        if (denominator.Length == 0)
            return false;

        ratioKey = NormalizeAnnotationScaleLookupKey("1:" + denominator);
        return ratioKey.Length > 0;
    }

    private static bool HasInnerSuffix(string styleName)
    {
        var trimmed = styleName?.Trim() ?? "";
        if (trimmed.Length == 0)
            return false;

        var firstDash = trimmed.IndexOf('-');
        if (firstDash < 0)
            return trimmed.EndsWith("内", StringComparison.Ordinal);

        if (firstDash >= trimmed.Length - 1)
            return false;

        var secondDash = trimmed.IndexOf('-', firstDash + 1);
        var segmentEnd = secondDash >= 0 ? secondDash : trimmed.Length;
        return segmentEnd > firstDash + 1 && trimmed[segmentEnd - 1] == '内';
    }

    private static bool TryResolveViewportIdForScaleReport(
        Document doc,
        out ObjectId viewportId,
        out string message,
        out bool isCurrentViewport)
    {
        viewportId = ObjectId.Null;
        message = "";
        isCurrentViewport = false;

        if (TryGetCurrentFloatingViewportId(doc, out viewportId))
        {
            isCurrentViewport = true;
            return true;
        }

        var ed = doc.Editor;
        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null)
        {
            var ids = new HashSet<ObjectId>();
            AddSelectionIds(implied.Value, ids);
            ed.SetImpliedSelection(Array.Empty<ObjectId>());

            if (ids.Count == 1)
            {
                viewportId = ids.First();
                return true;
            }

            if (ids.Count > 1)
            {
                message = "F_GV 一次只支持读取一个布局视口，请仅选择一个视口。";
                return false;
            }
        }

        var options = new PromptEntityOptions("\n选择要读取比例的视口：");
        options.SetRejectMessage("\n请选择布局视口。");
        options.AddAllowedClass(typeof(Viewport), exactMatch: false);
        var result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
            return false;

        viewportId = result.ObjectId;
        return !viewportId.IsNull;
    }

    private static bool TryResolveLayoutViewportIdForScaleChange(
        Transaction tr,
        Document doc,
        Database db,
        out ObjectId viewportId,
        out string message)
    {
        viewportId = ObjectId.Null;
        message = "";

        if (TryGetCurrentFloatingViewportId(doc, out viewportId))
            return true;

        if (TryGetSingleImpliedViewportId(doc, out var impliedViewportId))
        {
            var impliedViewport = tr.GetObject(impliedViewportId, OpenMode.ForRead, false) as Viewport;
            if (impliedViewport != null && impliedViewport.Number != 1)
            {
                viewportId = impliedViewportId;
                return true;
            }
        }

        if (TryGetSingleLayoutViewportId(tr, db, out var singleViewportId))
        {
            viewportId = singleViewportId;
            return true;
        }

        message = "当前在视口外（纸空间），请先选中一个目标视口后再切换比例。";
        return false;
    }

    private static bool TryGetSingleImpliedViewportId(Document doc, out ObjectId viewportId)
    {
        viewportId = ObjectId.Null;
        var implied = doc.Editor.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null)
            return false;

        var ids = new HashSet<ObjectId>();
        AddSelectionIds(implied.Value, ids);
        if (ids.Count != 1)
            return false;

        viewportId = ids.First();
        return !viewportId.IsNull;
    }

    private static bool TryGetSingleLayoutViewportId(Transaction tr, Database db, out ObjectId viewportId)
    {
        viewportId = ObjectId.Null;
        if (db.TileMode)
            return false;

        var currentSpace = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
        if (currentSpace == null)
            return false;

        var matchedCount = 0;
        foreach (ObjectId id in currentSpace)
        {
            if (!string.Equals(id.ObjectClass?.DxfName, "VIEWPORT", StringComparison.OrdinalIgnoreCase))
                continue;

            var viewport = tr.GetObject(id, OpenMode.ForRead, false) as Viewport;
            if (viewport == null || viewport.Number == 1)
                continue;

            matchedCount++;
            viewportId = id;
            if (matchedCount > 1)
                return false;
        }

        return matchedCount == 1 && !viewportId.IsNull;
    }

    private static List<ObjectId> CollectTargetViewportIds(Document doc, string promptMessage)
    {
        var ed = doc.Editor;
        var ids = new HashSet<ObjectId>();

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null)
        {
            AddSelectionIds(implied.Value, ids);
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            if (ids.Count > 0)
                return ids.ToList();
        }

        if (TryGetCurrentFloatingViewportId(doc, out var currentViewportId))
        {
            ids.Add(currentViewportId);
            return ids.ToList();
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = promptMessage
        };
        var filter = new SelectionFilter(
        [
            new TypedValue((int)DxfCode.Start, "VIEWPORT")
        ]);
        var result = ed.GetSelection(options, filter);
        if (result.Status == PromptStatus.OK && result.Value != null)
            AddSelectionIds(result.Value, ids);

        return ids.ToList();
    }

    private static void AddSelectionIds(SelectionSet selectionSet, HashSet<ObjectId> ids)
    {
        foreach (SelectedObject? item in selectionSet)
        {
            if (item == null || item.ObjectId.IsNull)
                continue;
            if (!string.Equals(item.ObjectId.ObjectClass?.DxfName, "VIEWPORT", StringComparison.OrdinalIgnoreCase))
                continue;
            ids.Add(item.ObjectId);
        }
    }

    private static bool TryPromptViewportScaleLabelText(
        Editor ed,
        string defaultText,
        out string labelText)
    {
        labelText = "";
        var trimmedDefault = defaultText?.Trim() ?? "";
        if (trimmedDefault.Length == 0)
            return false;

        var options = new PromptStringOptions("\n当前比例是")
        {
            AllowSpaces = true,
            DefaultValue = trimmedDefault,
            UseDefaultValue = true
        };

        var result = ed.GetString(options);
        if (result.Status == PromptStatus.None)
        {
            labelText = trimmedDefault;
            return true;
        }

        if (result.Status != PromptStatus.OK)
            return false;

        labelText = (result.StringResult ?? "").Trim();
        return labelText.Length > 0;
    }

    private static bool TryInsertViewportScaleLabel(
        Document doc,
        Database db,
        ObjectId viewportId,
        string labelText,
        out string message)
    {
        message = "";
        var trimmedText = labelText?.Trim() ?? "";
        if (trimmedText.Length == 0)
            return false;

        try
        {
            var insertMessage = "";
            var inserted = CadDatabaseScope.Write(
                doc,
                (database, tr) =>
                {
                    if (!CadDatabaseScope.TryOpenAs<Viewport>(tr, viewportId, OpenMode.ForRead, out var viewport) ||
                        viewport == null ||
                        viewport.Number == 1)
                    {
                        insertMessage = "未找到有效的布局视口。";
                        return false;
                    }

                    if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(tr, viewport.OwnerId, OpenMode.ForWrite, out var owner) ||
                        owner == null)
                    {
                        insertMessage = "未找到视口所属的布局空间。";
                        return false;
                    }

                    var centerPoint = viewport.CenterPoint;
                    var text = new DBText();
                    text.SetDatabaseDefaults(database);
                    text.TextString = trimmedText;
                    text.Height = ResolveViewportScaleLabelTextHeight(viewport);
                    text.Justify = AttachmentPoint.MiddleCenter;
                    text.AlignmentPoint = centerPoint;
                    text.Position = centerPoint;
                    if (!database.Textstyle.IsNull)
                        text.TextStyleId = database.Textstyle;

                    owner.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                    text.AdjustAlignment(database);
                    return true;
                },
                requireDocumentLock: true);

            if (!inserted)
            {
                message = insertMessage;
                return false;
            }

            message = $"已在视口中间显示：{trimmedText}。";
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_GV 在视口中间插入比例文字失败（无效操作）", ex);
            message = "在视口中间显示比例失败：" + ex.Message;
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_GV 在视口中间插入比例文字失败（CAD）", ex);
            message = "在视口中间显示比例失败：" + ex.Message;
            return false;
        }
    }

    private static double ResolveViewportScaleLabelTextHeight(Viewport viewport)
    {
        const double minTextHeight = 1e-6;
        const double fallbackRatio = 0.08;

        if (CadSystemVariableService.TryGetPositiveDouble(SystemVariableNames.TextSize, out var textSize))
            return textSize;

        var reference = Math.Min(Math.Abs(viewport.Width), Math.Abs(viewport.Height));
        if (reference > minTextHeight)
            return Math.Max(reference * fallbackRatio, 1.0);

        return 1.0;
    }

    private static string FormatViewportScaleRatio(double customScale)
    {
        if (double.IsNaN(customScale) || double.IsInfinity(customScale) || customScale <= 0.0)
            return "";

        const double tolerance = 1e-9;
        if (Math.Abs(customScale - 1.0) <= tolerance)
            return "1:1";

        if (customScale > 1.0)
            return $"{FormatScaleNumber(customScale)}:1";

        var denominator = 1.0 / customScale;
        return double.IsNaN(denominator) || double.IsInfinity(denominator)
            ? ""
            : $"1:{FormatScaleNumber(denominator)}";
    }

    private static string FormatScaleNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "";

        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static bool TryGetCurrentFloatingViewportId(Document doc, out ObjectId viewportId)
    {
        viewportId = ObjectId.Null;
        if (doc.Database.TileMode || TryGetCvPort() <= 1)
            return false;

        try
        {
            var currentViewportId = doc.Editor.CurrentViewportObjectId;
            if (currentViewportId.IsNull)
                return false;

            viewportId = currentViewportId;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取当前布局视口失败（无效操作）", ex);
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取当前布局视口失败（CAD）", ex);
            return false;
        }
    }

    private static int TryGetCvPort()
    {
        return CadSystemVariableService.TryGetInt32(SystemVariableNames.CvPort, out var cvPort)
            ? cvPort
            : 0;
    }

    private static void TryRegenAfterScaleOrStyleChange(Document doc, bool changed)
    {
        if (!changed)
            return;

        try
        {
            doc.Editor.Regen();
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DE 切换比例后重生成失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DE 切换比例后重生成失败（CAD）", ex);
        }
    }

    private static void TryWriteViewportWindowMessage(Document? doc, string message)
    {
        if (doc == null || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            doc.Editor.WriteMessage("\nC_TOOL：" + message);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 F_VW 消息失败（无效操作）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 F_VW 消息失败", ex);
        }
    }

    private static void TryWriteAnnotationScaleMessage(Document? doc, string message)
    {
        if (doc == null || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            doc.Editor.WriteMessage("\nC_TOOL：" + message);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 F_DE 错误消息失败（无效操作）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 F_DE 错误消息失败", ex);
        }
    }

    private readonly struct ViewportWindowSelection
    {
        internal ViewportWindowSelection(Point3d firstCorner, Point3d secondCorner)
        {
            MinX = Math.Min(firstCorner.X, secondCorner.X);
            MinY = Math.Min(firstCorner.Y, secondCorner.Y);
            MaxX = Math.Max(firstCorner.X, secondCorner.X);
            MaxY = Math.Max(firstCorner.Y, secondCorner.Y);
        }

        private double MinX { get; }

        private double MinY { get; }

        private double MaxX { get; }

        private double MaxY { get; }

        internal double ModelWidth => MaxX - MinX;

        internal double ModelHeight => MaxY - MinY;

        internal Point2d ViewCenter => new((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

        internal bool IsValid => ModelWidth > MinimumViewportSpan && ModelHeight > MinimumViewportSpan;
    }
}
