using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsQqqPlugin;

internal enum QqqRecognitionScope
{
    Layout,
    Model,
    All
}

internal sealed class QqqRecognitionTemplateCaptureItem
{
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
}

internal sealed class QqqRecognitionTemplateCaptureResult
{
    public List<QqqRecognitionTemplateCaptureItem> Items { get; set; } = new();
    public int SelectedCount { get; set; }
    public int AcceptedCount { get; set; }
    public string Message { get; set; } = "";
}

internal static class QqqFrameSelectionService
{
    private const string RecognitionByBlockName = "按块名识别";
    private const string RecognitionByLayerName = "按图层识别";
    private const string RecognitionAuto = "自动识别";
    private const int MaxBlockExplodeDepth = 6;

    private static readonly (string Label, double Width, double Height)[] IsoPaperSizes =
    {
        ("ISO A0", 1189d, 841d),
        ("ISO A1", 841d, 594d),
        ("ISO A2", 594d, 420d),
        ("ISO A3", 420d, 297d),
        ("ISO A4", 297d, 210d)
    };

    private static readonly int[] CommonScaleDenominators = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 500 };

    internal static QqqRecognitionTemplateCaptureResult CaptureBlockTemplates(Document doc)
    {
        return CaptureRecognitionTemplates(
            doc,
            "\nV_QQQ：请选择图框图块：",
            new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") }),
            TryCreateBlockTemplateItem,
            "图框图块",
            UIMessages.Command.NoBlockSelected);
    }

    internal static QqqRecognitionTemplateCaptureResult CaptureLayerTemplates(Document doc)
    {
        return CaptureRecognitionTemplates(
            doc,
            "\nV_QQQ：请选择图框图层对象：",
            null,
            TryCreateLayerTemplateItem,
            "图框图层",
            UIMessages.Command.NoObjectSelected);
    }

    internal static QqqFrameSelectionResult RecognizeFramesByBlockNames(
        Document doc,
        IEnumerable<string> blockNames,
        QqqRecognitionScope scope)
    {
        var selectedNames = NormalizeNames(blockNames);
        if (selectedNames.Count == 0)
            return new QqqFrameSelectionResult { Message = "请先勾选图框图块后再读取图纸。" };

        return RecognizeFrames(doc, scope, IsMatchedBlock);

        bool IsMatchedBlock(Entity entity, Transaction transaction)
        {
            if (entity is not BlockReference blockReference)
                return false;

            return selectedNames.Contains(GetBlockDisplayName(blockReference, transaction));
        }
    }

    internal static QqqFrameSelectionResult RecognizeFramesByLayerNames(
        Document doc,
        IEnumerable<string> layerNames,
        QqqRecognitionScope scope)
    {
        var selectedNames = NormalizeNames(layerNames);
        if (selectedNames.Count == 0)
            return new QqqFrameSelectionResult { Message = "请先勾选图框图层后再读取图纸。" };

        return RecognizeFrames(doc, scope, IsMatchedLayer);

        bool IsMatchedLayer(Entity entity, Transaction _)
        {
            var layerName = (entity.Layer ?? "").Trim();
            return layerName.Length > 0 && selectedNames.Contains(layerName);
        }
    }

    internal static QqqFrameSelectionResult CaptureImpliedSelectedFrames(
        Document doc,
        bool includePolylineFrames,
        bool includeBlockFrames)
    {
        if (!includePolylineFrames && !includeBlockFrames)
            return new QqqFrameSelectionResult { Message = "请至少勾选一种图框类型。" };

        using var documentLock = doc.LockDocument();
        var editor = doc.Editor;
        var implied = editor.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count == 0)
            return new QqqFrameSelectionResult();

        return ReadSelection(doc.Database, implied.Value, includePolylineFrames, includeBlockFrames, "预选");
    }

    internal static QqqFrameSelectionResult CaptureSelectedFrames(
        Document doc,
        bool includePolylineFrames,
        bool includeBlockFrames)
    {
        if (!includePolylineFrames && !includeBlockFrames)
            return new QqqFrameSelectionResult { Message = "请至少勾选一种图框类型。" };

        using var documentLock = doc.LockDocument();
        var editor = doc.Editor;
        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            var impliedResult = ReadSelection(doc.Database, implied.Value, includePolylineFrames, includeBlockFrames, "预选");
            if (impliedResult.Frames.Count > 0)
                return impliedResult;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nV_QQQ：请选择要批量打印的闭合线框或图块："
        };
        var filter = BuildSelectionFilter(includePolylineFrames, includeBlockFrames);
        var picked = filter == null ? editor.GetSelection(options) : editor.GetSelection(options, filter);
        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            return new QqqFrameSelectionResult
            {
                Message = picked.Status == PromptStatus.Cancel ? string.Format(UIMessages.Command.Cancelled, "选择图框") : UIMessages.Command.NoFrameSelected
            };
        }

        return ReadSelection(doc.Database, picked.Value, includePolylineFrames, includeBlockFrames, "选中");
    }

    internal static QqqFrameSelectionResult CaptureBlockFramesByNames(
        Document doc,
        IEnumerable<string> blockNames)
    {
        if (doc == null)
            return new QqqFrameSelectionResult { Message = "当前没有活动图纸。" };

        var allowedNames = NormalizeNames(blockNames);
        if (allowedNames.Count == 0)
            return new QqqFrameSelectionResult { Message = "请先在图块列表中添加图块。" };

        using var documentLock = doc.LockDocument();
        var editor = doc.Editor;
        var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            var impliedResult = ReadBlockSelectionByNames(doc.Database, implied.Value, allowedNames, "预选");
            if (impliedResult.Frames.Count > 0)
                return impliedResult;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nV_QQQ：请选择要打印的图块："
        };
        var picked = editor.GetSelection(options, filter);
        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            return new QqqFrameSelectionResult
            {
                Message = picked.Status == PromptStatus.Cancel
                    ? string.Format(UIMessages.Command.CommandCancelled, "选择打印图块")
                    : UIMessages.Command.NoBlockSelected
            };
        }

        return ReadBlockSelectionByNames(doc.Database, picked.Value, allowedNames, "选中");
    }

    internal static QqqFrameSelectionResult CaptureViewportFrames(Document doc)
    {
        using var documentLock = doc.LockDocument();
        var editor = doc.Editor;
        var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "VIEWPORT") });
        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            var impliedResult = ReadViewportSelection(doc.Database, implied.Value, "预选");
            if (impliedResult.Frames.Count > 0)
                return impliedResult;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nV_QQQ：请选择要添加到图纸列表的视口："
        };
        var picked = editor.GetSelection(options, filter);
        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            return new QqqFrameSelectionResult
            {
                Message = picked.Status == PromptStatus.Cancel
                    ? string.Format(UIMessages.Command.CommandCancelled, "选择视口")
                    : string.Format(UIMessages.Command.NoViewportSelected, "添加")
            };
        }

        return ReadViewportSelection(doc.Database, picked.Value, "选中");
    }

    internal static QqqFrameSelectionResult CaptureWindowFrames(Document doc)
    {
        if (doc == null)
            return new QqqFrameSelectionResult { Message = "当前没有活动图纸。" };

        using var documentLock = doc.LockDocument();
        var editor = doc.Editor;

        var firstResult = editor.GetPoint("\nV_QQQ：指定窗口打印第一角点：");
        if (firstResult.Status != PromptStatus.OK)
        {
            return new QqqFrameSelectionResult
            {
                Message = firstResult.Status == PromptStatus.Cancel
                    ? string.Format(UIMessages.Command.CommandCancelled, "框选窗口")
                    : "未指定窗口范围。"
            };
        }

        var cornerOptions = new PromptCornerOptions("\nV_QQQ：指定窗口打印对角点：", firstResult.Value)
        {
            UseDashedLine = true
        };
        var secondResult = editor.GetCorner(cornerOptions);
        if (secondResult.Status != PromptStatus.OK)
        {
            return new QqqFrameSelectionResult
            {
                Message = secondResult.Status == PromptStatus.Cancel
                    ? string.Format(UIMessages.Command.CommandCancelled, "框选窗口")
                    : "未指定完整窗口范围。"
            };
        }

        var firstPoint = TransformPromptPointToWcs(editor, firstResult.Value);
        var secondPoint = TransformPromptPointToWcs(editor, secondResult.Value);
        if (!TryCreateWindowFrame(firstPoint, secondPoint, out var frame, out var errorMessage) || frame == null)
        {
            return new QqqFrameSelectionResult
            {
                SelectedCount = 1,
                AcceptedCount = 0,
                Message = errorMessage.Length == 0 ? "窗口范围无效，未添加到图纸列表。" : errorMessage
            };
        }

        frame.AddedOrder = 1;
        frame.IsSelected = true;
        frame.Status = "待打印";

        return new QqqFrameSelectionResult
        {
            Frames = new List<QqqPlotFrameInfo> { frame },
            SelectedCount = 1,
            AcceptedCount = 1,
            Message = "已添加 1 张窗口图纸。"
        };
    }

    private static SelectionFilter? BuildSelectionFilter(bool includePolylineFrames, bool includeBlockFrames)
    {
        var values = new List<TypedValue>();
        if (includePolylineFrames && includeBlockFrames)
        {
            values.Add(new TypedValue((int)DxfCode.Operator, "<OR"));
            values.Add(new TypedValue((int)DxfCode.Start, "INSERT"));
            values.Add(new TypedValue((int)DxfCode.Start, "LWPOLYLINE"));
            values.Add(new TypedValue((int)DxfCode.Start, "POLYLINE"));
            values.Add(new TypedValue((int)DxfCode.Operator, "OR>"));
            return new SelectionFilter(values.ToArray());
        }

        if (includeBlockFrames)
            return new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });

        if (includePolylineFrames)
        {
            values.Add(new TypedValue((int)DxfCode.Operator, "<OR"));
            values.Add(new TypedValue((int)DxfCode.Start, "LWPOLYLINE"));
            values.Add(new TypedValue((int)DxfCode.Start, "POLYLINE"));
            values.Add(new TypedValue((int)DxfCode.Operator, "OR>"));
            return new SelectionFilter(values.ToArray());
        }

        return null;
    }

    private static Point3d TransformPromptPointToWcs(Editor editor, Point3d point)
    {
        try
        {
            return point.TransformBy(editor.CurrentUserCoordinateSystem);
        }
        catch (InvalidOperationException)
        {
            return point;
        }
        catch (AcadRuntimeException)
        {
            return point;
        }
    }

    private static QqqRecognitionTemplateCaptureResult CaptureRecognitionTemplates(
        Document doc,
        string promptMessage,
        SelectionFilter? filter,
        Func<Entity, Transaction, QqqRecognitionTemplateCaptureItem?> createItem,
        string targetLabel,
        string noSelectionMessage)
    {
        if (doc == null)
            return new QqqRecognitionTemplateCaptureResult { Message = "当前没有活动图纸。" };

        using var documentLock = doc.LockDocument();
        var editor = doc.Editor;
        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            var impliedResult = ReadRecognitionTemplates(doc.Database, implied.Value, createItem, targetLabel, "预选");
            if (impliedResult.Items.Count > 0)
                return impliedResult;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = promptMessage
        };
        var picked = filter == null ? editor.GetSelection(options) : editor.GetSelection(options, filter);
        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            return new QqqRecognitionTemplateCaptureResult
            {
                Message = picked.Status == PromptStatus.Cancel
                    ? $"{targetLabel}选择已取消。"
                    : noSelectionMessage
            };
        }

        return ReadRecognitionTemplates(doc.Database, picked.Value, createItem, targetLabel, "选中");
    }

    private static QqqRecognitionTemplateCaptureResult ReadRecognitionTemplates(
        Database database,
        SelectionSet selectionSet,
        Func<Entity, Transaction, QqqRecognitionTemplateCaptureItem?> createItem,
        string targetLabel,
        string sourceLabel)
    {
        var selectedCount = 0;
        var nameMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        using var transaction = database.TransactionManager.StartTransaction();
        foreach (SelectedObject? item in selectionSet)
        {
            if (item == null || item.ObjectId.IsNull)
                continue;

            selectedCount++;
            if (transaction.GetObject(item.ObjectId, OpenMode.ForRead, false) is not Entity entity)
                continue;

            var templateItem = createItem(entity, transaction);
            if (templateItem == null || string.IsNullOrWhiteSpace(templateItem.Name))
                continue;

            if (!nameMap.TryGetValue(templateItem.Name, out var sizeTexts))
            {
                sizeTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                nameMap[templateItem.Name] = sizeTexts;
            }

            var sizeText = NormalizeTemplateSizeText(templateItem.SizeText);
            if (sizeText.Length > 0)
                sizeTexts.Add(sizeText);
        }

        transaction.Commit();

        var items = nameMap
            .OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static x => new QqqRecognitionTemplateCaptureItem
            {
                Name = x.Key,
                SizeText = MergeTemplateSizeTexts(x.Value)
            })
            .ToList();

        return new QqqRecognitionTemplateCaptureResult
        {
            Items = items,
            SelectedCount = selectedCount,
            AcceptedCount = items.Count,
            Message = items.Count == 0
                ? $"所选对象中未记录到可用的{targetLabel}。"
                : $"已从{sourceLabel}对象中记录 {items.Count} 个{targetLabel}。"
        };
    }

    private static QqqFrameSelectionResult RecognizeFrames(
        Document doc,
        QqqRecognitionScope scope,
        Func<Entity, Transaction, bool> isMatchedEntity)
    {
        if (doc == null)
            return new QqqFrameSelectionResult { Message = "当前没有活动图纸。" };

        var frames = new List<QqqPlotFrameInfo>();
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (doc.LockDocument())
        using (var transaction = doc.Database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockTableRecordId in blockTable)
            {
                if (transaction.GetObject(blockTableRecordId, OpenMode.ForRead, false) is not BlockTableRecord blockTableRecord ||
                    !blockTableRecord.IsLayout ||
                    !ShouldIncludeLayout(blockTableRecord, transaction, scope))
                {
                    continue;
                }

                foreach (ObjectId entityId in blockTableRecord)
                {
                    if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity ||
                        !isMatchedEntity(entity, transaction) ||
                        !TryCreateMatchedFrameInfo(entity, transaction, out var frame) ||
                        frame == null ||
                        !existingKeys.Add(frame.Key))
                    {
                        continue;
                    }

                    frame.AddedOrder = frames.Count + 1;
                    frame.IsSelected = true;
                    frame.Status = "待打印";
                    frames.Add(frame);
                }
            }

            transaction.Commit();
        }

        return new QqqFrameSelectionResult
        {
            Frames = frames,
            SelectedCount = frames.Count,
            AcceptedCount = frames.Count,
            Message = frames.Count == 0
                ? "未读取到符合条件的图纸。"
                : $"已读取 {frames.Count} 张图纸。"
        };
    }

    private static bool TryCreateMatchedFrameInfo(
        Entity entity,
        Transaction transaction,
        out QqqPlotFrameInfo? frame)
    {
        frame = null;

        if (entity is BlockReference blockReference)
        {
            if (!TryGetExtents(blockReference, out var extents))
                return false;

            frame = CreateFrameInfo(
                blockReference,
                transaction,
                "图块",
                GetBlockDisplayName(blockReference, transaction),
                extents,
                "识别列表");
            return true;
        }

        return TryCreatePolylineFrame(entity, transaction, "线框", "闭合图框", "识别列表", out frame) && frame != null;
    }

    private static bool ShouldIncludeLayout(
        BlockTableRecord blockTableRecord,
        Transaction transaction,
        QqqRecognitionScope scope)
    {
        if (scope == QqqRecognitionScope.All)
            return true;

        try
        {
            if (!blockTableRecord.IsLayout ||
                blockTableRecord.LayoutId.IsNull ||
                transaction.GetObject(blockTableRecord.LayoutId, OpenMode.ForRead, false) is not Layout layout)
            {
                return true;
            }

            var isModelLayout = string.Equals(layout.LayoutName, "Model", StringComparison.OrdinalIgnoreCase);
            return scope switch
            {
                QqqRecognitionScope.Layout => !isModelLayout,
                QqqRecognitionScope.Model => isModelLayout,
                _ => true
            };
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (AcadRuntimeException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static HashSet<string> NormalizeNames(IEnumerable<string> names)
    {
        return names
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static QqqRecognitionTemplateCaptureItem? TryCreateBlockTemplateItem(Entity entity, Transaction transaction)
    {
        if (entity is not BlockReference blockReference)
            return null;

        return new QqqRecognitionTemplateCaptureItem
        {
            Name = GetBlockDisplayName(blockReference, transaction),
            SizeText = TryGetExtents(blockReference, out var extents) ? FormatSizeText(extents) : ""
        };
    }

    private static QqqRecognitionTemplateCaptureItem? TryCreateLayerTemplateItem(Entity entity, Transaction transaction)
    {
        var layerName = (entity.Layer ?? "").Trim();
        if (layerName.Length == 0)
            return null;

        return new QqqRecognitionTemplateCaptureItem
        {
            Name = layerName,
            SizeText = TryGetExtents(entity, out var extents) ? FormatSizeText(extents) : ""
        };
    }

    private static string FormatSizeText(Extents3d extents)
    {
        var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
        var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
        if (width <= 0 || height <= 0)
            return "";

        return $"{width:0.##}×{height:0.##}";
    }

    private static string NormalizeTemplateSizeText(string? sizeText)
    {
        var value = (sizeText ?? "").Trim();
        return value == "-" ? "" : value;
    }

    private static string MergeTemplateSizeTexts(IEnumerable<string> sizeTexts)
    {
        var values = sizeTexts
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return values.Count switch
        {
            0 => "-",
            1 => values[0],
            _ => "多尺寸"
        };
    }

    private static QqqFrameSelectionResult ReadSelection(
        Database database,
        SelectionSet selectionSet,
        bool includePolylineFrames,
        bool includeBlockFrames,
        string sourceLabel)
    {
        var frames = new List<QqqPlotFrameInfo>();
        var totalCount = 0;

        using var transaction = database.TransactionManager.StartTransaction();
        foreach (SelectedObject? item in selectionSet)
        {
            if (item == null || item.ObjectId.IsNull)
                continue;

            totalCount++;
            if (transaction.GetObject(item.ObjectId, OpenMode.ForRead, false) is not Entity entity)
                continue;

            if (TryCreateFrameInfo(entity, transaction, includePolylineFrames, includeBlockFrames, out var frame) &&
                frame != null)
            {
                frame.AddedOrder = frames.Count + 1;
                frame.IsSelected = true;
                frame.Status = "待打印";
                frames.Add(frame);
            }
        }

        transaction.Commit();

        if (frames.Count == 0)
        {
            return new QqqFrameSelectionResult
            {
                Frames = frames,
                SelectedCount = totalCount,
                AcceptedCount = 0,
                Message = "所选对象中未识别到可打印的闭合线框或图块。"
            };
        }

        return new QqqFrameSelectionResult
        {
            Frames = frames,
            SelectedCount = totalCount,
            AcceptedCount = frames.Count,
            Message = $"已从{sourceLabel}对象中识别 {frames.Count} 个可打印图框。"
        };
    }

    private static QqqFrameSelectionResult ReadBlockSelectionByNames(
        Database database,
        SelectionSet selectionSet,
        HashSet<string> allowedBlockNames,
        string sourceLabel)
    {
        var frames = new List<QqqPlotFrameInfo>();
        var totalCount = 0;
        var notInListCount = 0;
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var transaction = database.TransactionManager.StartTransaction();
        foreach (SelectedObject? item in selectionSet)
        {
            if (item == null || item.ObjectId.IsNull)
                continue;

            totalCount++;
            if (transaction.GetObject(item.ObjectId, OpenMode.ForRead, false) is not BlockReference blockReference)
                continue;

            var blockName = GetBlockDisplayName(blockReference, transaction);
            if (!allowedBlockNames.Contains(blockName))
            {
                notInListCount++;
                continue;
            }

            if (!TryCreateFrameInfo(blockReference, transaction, includePolylineFrames: false, includeBlockFrames: true, out var frame) ||
                frame == null ||
                !existingKeys.Add(frame.Key))
            {
                continue;
            }

            frame.AddedOrder = frames.Count + 1;
            frame.IsSelected = true;
            frame.Status = "待打印";
            frames.Add(frame);
        }

        transaction.Commit();

        if (frames.Count == 0)
        {
            var reason = notInListCount > 0
                ? "所选图块不在图块列表中，未添加到图纸列表。"
                : "所选对象中未识别到可打印的图块。";

            return new QqqFrameSelectionResult
            {
                Frames = frames,
                SelectedCount = totalCount,
                AcceptedCount = 0,
                Message = reason
            };
        }

        var message = notInListCount > 0
            ? $"已从{sourceLabel}图块中添加 {frames.Count} 张图纸，跳过 {notInListCount} 个不在列表中的图块。"
            : $"已从{sourceLabel}图块中添加 {frames.Count} 张图纸。";

        return new QqqFrameSelectionResult
        {
            Frames = frames,
            SelectedCount = totalCount,
            AcceptedCount = frames.Count,
            Message = message
        };
    }

    private static QqqFrameSelectionResult ReadViewportSelection(
        Database database,
        SelectionSet selectionSet,
        string sourceLabel)
    {
        var frames = new List<QqqPlotFrameInfo>();
        var totalCount = 0;
        var skippedPaperViewportCount = 0;
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var transaction = database.TransactionManager.StartTransaction();
        foreach (SelectedObject? item in selectionSet)
        {
            if (item == null || item.ObjectId.IsNull)
                continue;

            totalCount++;
            if (transaction.GetObject(item.ObjectId, OpenMode.ForRead, false) is not Viewport viewport)
                continue;

            if (viewport.Number <= 1)
            {
                skippedPaperViewportCount++;
                continue;
            }

            if (!TryCreateViewportFrame(viewport, transaction, out var frame) ||
                frame == null ||
                !existingKeys.Add(frame.Key))
            {
                continue;
            }

            frame.AddedOrder = frames.Count + 1;
            frame.IsSelected = true;
            frame.Status = "待打印";
            frames.Add(frame);
        }

        transaction.Commit();

        if (frames.Count == 0)
        {
            var reason = skippedPaperViewportCount > 0
                ? "所选对象中只包含整体布局视口，未添加到图纸列表。"
                : "所选对象中未识别到可添加的视口。";

            return new QqqFrameSelectionResult
            {
                Frames = frames,
                SelectedCount = totalCount,
                AcceptedCount = 0,
                Message = reason
            };
        }

        return new QqqFrameSelectionResult
        {
            Frames = frames,
            SelectedCount = totalCount,
            AcceptedCount = frames.Count,
            Message = $"已从{sourceLabel}视口中识别 {frames.Count} 张图纸。"
        };
    }

    private static bool TryCreateWindowFrame(
        Point3d firstPoint,
        Point3d secondPoint,
        out QqqPlotFrameInfo? frame,
        out string errorMessage)
    {
        frame = null;
        errorMessage = "";

        var minX = Math.Min(firstPoint.X, secondPoint.X);
        var minY = Math.Min(firstPoint.Y, secondPoint.Y);
        var minZ = Math.Min(firstPoint.Z, secondPoint.Z);
        var maxX = Math.Max(firstPoint.X, secondPoint.X);
        var maxY = Math.Max(firstPoint.Y, secondPoint.Y);
        var maxZ = Math.Max(firstPoint.Z, secondPoint.Z);

        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 1e-6 || height <= 1e-6)
        {
            errorMessage = "窗口范围无效，未添加到图纸列表。";
            return false;
        }

        var extents = new Extents3d(
            new Point3d(minX, minY, minZ),
            new Point3d(maxX, maxY, maxZ));
        var guess = GuessPaperAndScale(extents);
        var layoutName = LayoutManager.Current.CurrentLayout;
        var centerX = (minX + maxX) * 0.5d;
        var centerY = (minY + maxY) * 0.5d;
        var frameName = $"窗口-{DateTime.Now:HHmmssfff}";

        frame = new QqqPlotFrameInfo
        {
            Key = BuildWindowFrameKey(layoutName, minX, minY, maxX, maxY),
            LayoutName = layoutName,
            SpaceName = string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase) ? "模型" : layoutName,
            FrameType = "窗口",
            FrameName = frameName,
            BlockName = "",
            RecognitionSource = "窗口打印",
            LayerName = "",
            HandleText = "",
            Width = width,
            Height = height,
            CenterX = centerX,
            CenterY = centerY,
            WcsExtents = extents,
            PaperSize = guess.PaperSize,
            PlotScale = guess.ScaleText,
            Status = "待打印",
            IsSelected = true
        };
        return true;
    }

    private static string BuildWindowFrameKey(string layoutName, double minX, double minY, double maxX, double maxY)
    {
        return $"WINDOW|{layoutName}|{minX:0.###},{minY:0.###}|{maxX:0.###},{maxY:0.###}";
    }

    private static bool TryCreateRecognizedFrameInfo(
        Entity entity,
        Transaction transaction,
        string recognitionMode,
        string blockName,
        string layerName,
        out QqqPlotFrameInfo? frame)
    {
        frame = null;
        var normalizedMode = (recognitionMode ?? "").Trim();

        if (string.Equals(normalizedMode, RecognitionByBlockName, StringComparison.OrdinalIgnoreCase))
        {
            if (entity is not BlockReference blockReference)
                return false;

            var actualBlockName = GetBlockDisplayName(blockReference, transaction);
            if (!string.Equals(actualBlockName, blockName?.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!TryGetExtents(blockReference, out var blockExtents))
                return false;

            frame = CreateFrameInfo(blockReference, transaction, "块图框", actualBlockName, blockExtents, RecognitionByBlockName);
            return true;
        }

        if (string.Equals(normalizedMode, RecognitionByLayerName, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(entity.Layer ?? "", layerName?.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (entity is BlockReference blockOnLayer)
            {
                if (!TryGetExtents(blockOnLayer, out var blockExtents))
                    return false;

                var blockDisplayName = GetBlockDisplayName(blockOnLayer, transaction);
                frame = CreateFrameInfo(blockOnLayer, transaction, "图层图框", blockDisplayName, blockExtents, RecognitionByLayerName);
                return true;
            }

            if (!TryCreatePolylineFrame(entity, transaction, "图层图框", "图层图框", RecognitionByLayerName, out frame))
                return false;

            return true;
        }

        return TryCreateAutoRecognizedFrameInfo(entity, transaction, out frame);
    }

    private static bool TryCreateAutoRecognizedFrameInfo(
        Entity entity,
        Transaction transaction,
        out QqqPlotFrameInfo? frame)
    {
        frame = null;

        if (entity is BlockReference blockReference)
        {
            if (!TryGetExtents(blockReference, out var blockExtents))
                return false;

            var blockName = GetBlockDisplayName(blockReference, transaction);
            var blockGuess = GuessPaperAndScale(blockExtents);
            var hasKeyword = HasFrameKeyword(blockName) || HasFrameKeyword(entity.Layer ?? "");
            if (!hasKeyword && !blockGuess.IsReliable)
                return false;

            frame = CreateFrameInfo(blockReference, transaction, "自动识别", blockName, blockExtents, RecognitionAuto);
            return true;
        }

        if (!TryCreatePolylineFrame(entity, transaction, "自动识别", "自动图框", RecognitionAuto, out frame))
            return false;

        if (frame == null)
            return false;

        var autoGuess = GuessPaperAndScale(frame.WcsExtents);
        var rectangleLike = IsRectangleLike(entity);
        if (!rectangleLike && !HasFrameKeyword(entity.Layer ?? "") && !autoGuess.IsReliable)
        {
            frame = null;
            return false;
        }

        return true;
    }

    private static bool TryCreateFrameInfo(
        Entity entity,
        Transaction transaction,
        bool includePolylineFrames,
        bool includeBlockFrames,
        out QqqPlotFrameInfo? frame)
    {
        frame = null;

        try
        {
            if (includeBlockFrames && entity is BlockReference blockReference)
            {
                if (!TryGetExtents(blockReference, out var extents))
                    return false;

                frame = CreateFrameInfo(
                    blockReference,
                    transaction,
                    "图块",
                    GetBlockDisplayName(blockReference, transaction),
                    extents,
                    "手动选择");
                return true;
            }

            if (includePolylineFrames &&
                TryCreatePolylineFrame(entity, transaction, "线框", "闭合图框", "手动选择", out frame) &&
                frame != null)
            {
                return true;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    private static bool TryCreatePolylineFrame(
        Entity entity,
        Transaction transaction,
        string frameType,
        string frameName,
        string recognitionSource,
        out QqqPlotFrameInfo? frame)
    {
        frame = null;

        if (entity is Polyline polyline && polyline.Closed)
        {
            if (!TryGetExtents(polyline, out var extents))
                return false;

            frame = CreateFrameInfo(polyline, transaction, frameType, frameName, extents, recognitionSource);
            return true;
        }

        if (entity is Polyline2d polyline2d && polyline2d.Closed)
        {
            if (!TryGetExtents(polyline2d, out var extents))
                return false;

            frame = CreateFrameInfo(polyline2d, transaction, frameType, frameName, extents, recognitionSource);
            return true;
        }

        if (entity is Polyline3d polyline3d && polyline3d.Closed)
        {
            if (!TryGetExtents(polyline3d, out var extents))
                return false;

            frame = CreateFrameInfo(polyline3d, transaction, frameType, frameName, extents, recognitionSource);
            return true;
        }

        return false;
    }

    private static bool TryGetExtents(Entity entity, out Extents3d extents)
    {
        if (entity is BlockReference blockReference &&
            TryGetBlockReferenceExtents(blockReference, out extents))
        {
            return true;
        }

        extents = default;
        try
        {
            extents = entity.GeometricExtents;
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetBlockReferenceExtents(BlockReference blockReference, out Extents3d extents)
    {
        try
        {
            if (TryGetPreferredBlockFrameExtents(blockReference, out extents))
                return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        try
        {
            if (TryGetExplodedEntityExtents(blockReference, depth: 0, out extents))
                return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        try
        {
            extents = blockReference.GeometricExtents;
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            extents = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            extents = default;
            return false;
        }
    }

    private static bool TryGetPreferredBlockFrameExtents(BlockReference blockReference, out Extents3d extents)
    {
        extents = default;
        var hasCandidate = false;
        var bestPriority = int.MinValue;
        var bestArea = double.MinValue;

        CollectPreferredClosedFrameExtents(
            blockReference,
            depth: 0,
            ref hasCandidate,
            ref extents,
            ref bestPriority,
            ref bestArea);

        return hasCandidate;
    }

    private static void CollectPreferredClosedFrameExtents(
        Entity entity,
        int depth,
        ref bool hasCandidate,
        ref Extents3d bestExtents,
        ref int bestPriority,
        ref double bestArea)
    {
        if (!IsEntityVisible(entity))
            return;

        try
        {
            if (TryGetClosedFrameCandidate(entity, out var candidateExtents, out var priority, out var candidateArea))
            {
                if (!hasCandidate ||
                    priority > bestPriority ||
                    (priority == bestPriority && candidateArea > bestArea))
                {
                    hasCandidate = true;
                    bestExtents = candidateExtents;
                    bestPriority = priority;
                    bestArea = candidateArea;
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        if (entity is not BlockReference blockReference || depth >= MaxBlockExplodeDepth)
            {
            return;
        }

        DBObjectCollection? exploded = null;
        try
        {
            if (!TryExplodeBlockReference(blockReference, out exploded))
                return;

            foreach (DBObject item in exploded)
            {
                if (item is not Entity childEntity)
                {
                    item.Dispose();
                    continue;
                }

                try
                {
                    CollectPreferredClosedFrameExtents(
                        childEntity,
                        depth + 1,
                        ref hasCandidate,
                        ref bestExtents,
                        ref bestPriority,
                        ref bestArea);
                }
                finally
                {
                    childEntity.Dispose();
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }
        finally
        {
            exploded?.Dispose();
        }
    }

    private static bool TryGetClosedFrameCandidate(
        Entity entity,
        out Extents3d extents,
        out int priority,
        out double area)
    {
        extents = default;
        priority = 0;
        area = 0d;

        try
        {
            if (!IsClosedFrameEntity(entity) || !TryGetExtentsFromRawGeometry(entity, out extents))
                return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
        var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
        area = width * height;
        if (area <= 1e-6)
            return false;

        priority = entity is Polyline polyline && polyline.NumberOfVertices == 4 ? 2 : 1;
        return true;
    }

    private static bool IsClosedFrameEntity(Entity entity)
    {
        return entity switch
        {
            Polyline polyline => polyline.Closed,
            Polyline2d polyline2d => polyline2d.Closed,
            Polyline3d polyline3d => polyline3d.Closed,
            _ => false
        };
    }

    private static bool TryGetExtentsFromRawGeometry(Entity entity, out Extents3d extents)
    {
        extents = default;
        try
        {
            extents = entity.GeometricExtents;
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetExplodedEntityExtents(Entity entity, int depth, out Extents3d extents)
    {
        extents = default;
        if (!IsEntityVisible(entity))
            return false;

        if (entity is BlockReference blockReference &&
            depth < MaxBlockExplodeDepth &&
            TryExplodeBlockReference(blockReference, out var exploded))
        {
            var hasExtents = false;
            try
            {
                foreach (DBObject item in exploded)
                {
                    if (item is not Entity childEntity)
                    {
                        item.Dispose();
                        continue;
                    }

                    try
                    {
                        if (!TryGetExplodedEntityExtents(childEntity, depth + 1, out var childExtents))
                            continue;

                        if (hasExtents)
                            extents.AddExtents(childExtents);
                        else
                        {
                            extents = childExtents;
                            hasExtents = true;
                        }
                    }
                    finally
                    {
                        childEntity.Dispose();
                    }
                }
            }
            finally
            {
                exploded.Dispose();
            }

            if (hasExtents)
                return true;
        }

        try
        {
            extents = entity.GeometricExtents;
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryExplodeBlockReference(BlockReference blockReference, out DBObjectCollection exploded)
    {
        exploded = new DBObjectCollection();
        try
        {
            blockReference.Explode(exploded);
            return exploded.Count > 0;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            exploded.Dispose();
            exploded = new DBObjectCollection();
            return false;
        }
        catch (InvalidOperationException)
        {
            exploded.Dispose();
            exploded = new DBObjectCollection();
            return false;
        }
    }

    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryCreateViewportFrame(
        Viewport viewport,
        Transaction transaction,
        out QqqPlotFrameInfo? frame)
    {
        frame = null;
        if (viewport.Number <= 1 || !TryGetViewportExtents(viewport, out var extents))
            return false;

        var frameName = viewport.Number > 1
            ? $"视口{viewport.Number}"
            : $"视口_{viewport.Handle}";
        frame = CreateFrameInfo(viewport, transaction, "视口", frameName, extents, "选择视口");
        frame.PaperSize = "自动匹配";
        frame.PlotScale = FormatViewportScale(viewport.CustomScale);
        return true;
    }

    private static bool TryGetViewportExtents(Viewport viewport, out Extents3d extents)
    {
        if (TryGetExtents(viewport, out extents))
            return true;

        var width = Math.Abs(viewport.Width);
        var height = Math.Abs(viewport.Height);
        if (width <= 0 || height <= 0)
        {
            extents = default;
            return false;
        }

        var center = viewport.CenterPoint;
        var minPoint = new Point3d(center.X - width * 0.5, center.Y - height * 0.5, center.Z);
        var maxPoint = new Point3d(center.X + width * 0.5, center.Y + height * 0.5, center.Z);
        extents = new Extents3d(minPoint, maxPoint);
        return true;
    }

    private static string FormatViewportScale(double customScale)
    {
        if (double.IsNaN(customScale) || double.IsInfinity(customScale) || customScale <= 0)
            return "自定义";

        if (Math.Abs(customScale - 1d) <= 0.0001d)
            return "1:1";

        return customScale > 1d
            ? $"{customScale:0.###}:1"
            : $"1:{(1d / customScale):0.###}";
    }

    private static QqqPlotFrameInfo CreateFrameInfo(
        Entity entity,
        Transaction transaction,
        string frameType,
        string frameName,
        Extents3d extents,
        string recognitionSource)
    {
        var layoutName = GetLayoutName(entity, transaction);
        var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
        var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
        var handleText = entity.Handle.ToString();
        var guess = GuessPaperAndScale(extents);
        var resolvedFrameName = (frameName ?? "").Trim();
        if (resolvedFrameName.Length == 0)
            resolvedFrameName = $"{frameType}_{handleText}";

        return new QqqPlotFrameInfo
        {
            Key = $"{layoutName}|{handleText}",
            LayoutName = layoutName,
            SpaceName = string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase) ? "模型" : layoutName,
            FrameType = frameType,
            FrameName = resolvedFrameName,
            BlockName = entity is BlockReference blockReference ? GetBlockDisplayName(blockReference, transaction) : "",
            RecognitionSource = recognitionSource,
            LayerName = entity.Layer ?? "",
            HandleText = handleText,
            Width = width,
            Height = height,
            CenterX = (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
            CenterY = (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
            WcsExtents = extents,
            PaperSize = guess.PaperSize,
            PlotScale = guess.ScaleText,
            Status = "待打印",
            IsSelected = true
        };
    }

    private static string GetLayoutName(Entity entity, Transaction transaction)
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

        return LayoutManager.Current.CurrentLayout;
    }

    private static string GetBlockDisplayName(BlockReference blockReference, Transaction transaction)
    {
        try
        {
            var blockId = blockReference.DynamicBlockTableRecord.IsNull
                ? blockReference.BlockTableRecord
                : blockReference.DynamicBlockTableRecord;
            if (transaction.GetObject(blockId, OpenMode.ForRead, false) is BlockTableRecord blockTableRecord)
            {
                var name = (blockTableRecord.Name ?? "").Trim();
                if (name.Length > 0)
                    return name;
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

        return "图块参照";
    }

    private static bool HasFrameKeyword(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return false;

        return text.IndexOf("图框", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("sheet", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("A1", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("A2", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("A4", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRectangleLike(Entity entity)
    {
        if (entity is Polyline polyline)
            return polyline.Closed && polyline.NumberOfVertices == 4;

        return false;
    }

    private static PaperGuess GuessPaperAndScale(Extents3d extents)
    {
        var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
        var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
        if (width <= 0 || height <= 0)
            return PaperGuess.Unknown();

        var best = PaperGuess.Unknown();
        foreach (var (label, paperWidth, paperHeight) in IsoPaperSizes)
        {
            foreach (var denominator in CommonScaleDenominators)
            {
                var candidate = EvaluateGuess(width, height, label, paperWidth, paperHeight, denominator);
                if (candidate.Score < best.Score)
                    best = candidate;
            }
        }

        return best;
    }

    private static PaperGuess EvaluateGuess(
        double width,
        double height,
        string paperLabel,
        double paperWidth,
        double paperHeight,
        int denominator)
    {
        var normalError = CalculateRelativeError(width, height, paperWidth * denominator, paperHeight * denominator);
        var rotatedError = CalculateRelativeError(width, height, paperHeight * denominator, paperWidth * denominator);
        var bestError = Math.Min(normalError, rotatedError);

        return new PaperGuess
        {
            PaperSize = bestError <= 0.16 ? paperLabel : "自动匹配",
            ScaleText = denominator <= 1 ? "1:1" : $"1:{denominator}",
            Score = bestError,
            IsReliable = bestError <= 0.12
        };
    }

    private static double CalculateRelativeError(
        double actualWidth,
        double actualHeight,
        double targetWidth,
        double targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
            return double.MaxValue;

        var widthError = Math.Abs(actualWidth - targetWidth) / targetWidth;
        var heightError = Math.Abs(actualHeight - targetHeight) / targetHeight;
        return widthError + heightError;
    }

    private readonly struct PaperGuess
    {
        internal string PaperSize { get; init; }
        internal string ScaleText { get; init; }
        internal double Score { get; init; }
        internal bool IsReliable { get; init; }

        internal static PaperGuess Unknown()
        {
            return new PaperGuess
            {
                PaperSize = "自动匹配",
                ScaleText = "自定义",
                Score = double.MaxValue,
                IsReliable = false
            };
        }
    }
}
