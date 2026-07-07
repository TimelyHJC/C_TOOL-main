using System.Collections.Concurrent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// F_DD：根据所选对象所在图层关闭其他图层显示；再次执行时恢复上次关闭的图层。
/// </summary>
internal static class LayerVisibilityToggleService
{
    private static readonly ConcurrentDictionary<Document, LayerIsolationSnapshot> s_snapshots = new();
    private static readonly ConcurrentDictionary<Document, byte> s_trackedDocuments = new();
    private static int s_documentCleanupRegistered;

    internal static void Toggle()
    {
        CadCommandContext.ExecuteInActiveDocument("执行 F_DD", (doc, ed) =>
        {
            EnsureUndoTracking(doc);

            if (s_snapshots.TryGetValue(doc, out var snapshot))
            {
                Restore(doc, snapshot);
                s_snapshots.TryRemove(doc, out _);
                return;
            }

            if (!TryCollectSelectedLayerNames(doc, out var keepLayerNames, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_DD_Cancelled}"
                    : $"\n{UIMessages.Prefix_C_TOOL}所选对象未找到可保留显示的图层。");
                return;
            }

            var createdSnapshot = Isolate(doc, keepLayerNames, out var message);
            ed.WriteMessage(message);
            if (createdSnapshot != null)
                s_snapshots[doc] = createdSnapshot;
        });
    }

    internal static void Terminate()
    {
        foreach (var doc in s_trackedDocuments.Keys)
        {
            try
            {
                doc.CommandWillStart -= OnDocumentCommandWillStart;
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_DD 注销撤销监听失败（无效操作）", ex);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_DD 注销撤销监听失败（CAD）", ex);
            }
        }

        UnregisterDocumentCleanup();
        s_trackedDocuments.Clear();
        s_snapshots.Clear();
    }

    private static void EnsureUndoTracking(Document doc)
    {
        if (!s_trackedDocuments.TryAdd(doc, 0))
            return;

        try
        {
            RegisterDocumentCleanup();
            doc.CommandWillStart += OnDocumentCommandWillStart;
        }
        catch (InvalidOperationException ex)
        {
            s_trackedDocuments.TryRemove(doc, out _);
            C_toolsDiagnostics.LogNonFatal("F_DD 注册撤销监听失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            s_trackedDocuments.TryRemove(doc, out _);
            C_toolsDiagnostics.LogNonFatal("F_DD 注册撤销监听失败（CAD）", ex);
        }
    }

    private static void RegisterDocumentCleanup()
    {
        if (Interlocked.Exchange(ref s_documentCleanupRegistered, 1) == 1)
            return;

        var documentManager = AcAp.DocumentManager;
        documentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
    }

    private static void UnregisterDocumentCleanup()
    {
        if (Interlocked.Exchange(ref s_documentCleanupRegistered, 0) == 0)
            return;

        try
        {
            var documentManager = AcAp.DocumentManager;
            documentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DD 注销图纸清理监听失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DD 注销图纸清理监听失败（CAD）", ex);
        }
    }

    private static void OnDocumentCommandWillStart(object? sender, CommandEventArgs e)
    {
        if (sender is not Document doc)
            return;

        var commandName = (e.GlobalCommandName ?? "").Trim();
        if (!IsUndoCommand(commandName))
            return;

        s_snapshots.TryRemove(doc, out _);
    }

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        ClearDocumentState(e.Document);
    }

    private static void ClearDocumentState(Document doc)
    {
        s_snapshots.TryRemove(doc, out _);
        if (!s_trackedDocuments.TryRemove(doc, out _))
            return;

        try
        {
            doc.CommandWillStart -= OnDocumentCommandWillStart;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DD 清理图纸撤销监听失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DD 清理图纸撤销监听失败（CAD）", ex);
        }
    }

    private static bool IsUndoCommand(string commandName)
    {
        var normalizedCommandName = commandName.TrimStart('.', '_');
        return string.Equals(normalizedCommandName, "UNDO", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedCommandName, "U", StringComparison.OrdinalIgnoreCase);
    }

    private static LayerIsolationSnapshot? Isolate(
        Document doc,
        HashSet<string> keepLayerNames,
        out string message)
    {
        var result = CadDatabaseScope.Write(doc, (db, tr) =>
        {
            var hiddenLayerStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var hiddenCount = 0;
            var skippedCount = 0;
            var lt = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
            var currentLayer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, db.Clayer, OpenMode.ForRead);
            var originalCurrentLayerName = (currentLayer.Name ?? "").Trim();

            if (originalCurrentLayerName.Length == 0)
            {
                return (
                    Snapshot: (LayerIsolationSnapshot?)null,
                    Message: "\nC_TOOL：当前图纸未找到有效当前图层，无法执行 F_DD。");
            }

            if (!keepLayerNames.Contains(originalCurrentLayerName))
            {
                var replacementCurrentLayerId = FindReplacementCurrentLayerId(tr, lt, keepLayerNames);
                if (replacementCurrentLayerId.IsNull)
                {
                    return (
                        Snapshot: (LayerIsolationSnapshot?)null,
                        Message: "\nC_TOOL：未找到可作为当前层的所选图层，无法执行 F_DD。");
                }

                db.Clayer = replacementCurrentLayerId;
            }

            foreach (ObjectId layerId in lt)
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                var layerName = (layer.Name ?? "").Trim();
                if (layerName.Length == 0 || keepLayerNames.Contains(layerName) || layer.IsOff)
                    continue;

                try
                {
                    if (!layer.IsWriteEnabled)
                        layer.UpgradeOpen();

                    hiddenLayerStates[layerName] = layer.IsOff;
                    layer.IsOff = true;
                    hiddenCount++;
                }
                catch (InvalidOperationException ex)
                {
                    skippedCount++;
                    C_toolsDiagnostics.LogNonFatal($"F_DD 关闭图层 {layerName} 失败（无效操作）", ex);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    skippedCount++;
                    C_toolsDiagnostics.LogNonFatal($"F_DD 关闭图层 {layerName} 失败（CAD）", ex);
                }
            }

            if (hiddenCount == 0)
            {
                return (
                    Snapshot: (LayerIsolationSnapshot?)null,
                    Message: $"\nC_TOOL：当前已仅显示所选图层（{keepLayerNames.Count} 个）。");
            }

            var resultMessage = $"\nC_TOOL：已保留 {keepLayerNames.Count} 个图层，关闭 {hiddenCount} 个其他图层。再次执行 F_DD 可恢复。";
            if (skippedCount > 0)
                resultMessage += $" 跳过 {skippedCount} 个无法修改的图层。";

            return (
                Snapshot: new LayerIsolationSnapshot(originalCurrentLayerName, hiddenLayerStates),
                Message: resultMessage);
        }, requireDocumentLock: true);

        message = result.Message;
        return result.Snapshot;
    }

    private static void Restore(Document doc, LayerIsolationSnapshot snapshot)
    {
        var ed = doc.Editor;
        var result = CadDatabaseScope.Write(doc, (db, tr) =>
        {
            var restoredCount = 0;
            var skippedCount = 0;
            var missingCount = 0;
            var lt = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);

            foreach (var kv in snapshot.HiddenLayerOffStates)
            {
                if (!lt.Has(kv.Key))
                {
                    missingCount++;
                    continue;
                }

                try
                {
                    var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, lt[kv.Key], OpenMode.ForWrite);
                    if (layer.IsOff != kv.Value)
                    {
                        layer.IsOff = kv.Value;
                        restoredCount++;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    skippedCount++;
                    C_toolsDiagnostics.LogNonFatal($"F_DD 恢复图层 {kv.Key} 失败（无效操作）", ex);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    skippedCount++;
                    C_toolsDiagnostics.LogNonFatal($"F_DD 恢复图层 {kv.Key} 失败（CAD）", ex);
                }
            }

            var currentLayerRestored = TryRestoreCurrentLayer(tr, db, lt, snapshot.OriginalCurrentLayerName);
            return (RestoredCount: restoredCount, SkippedCount: skippedCount, MissingCount: missingCount, CurrentLayerRestored: currentLayerRestored);
        }, requireDocumentLock: true);

        var parts = new List<string>();
        if (result.RestoredCount > 0)
            parts.Add($"恢复 {result.RestoredCount} 个图层");
        if (result.CurrentLayerRestored)
            parts.Add("恢复原当前层");
        if (result.MissingCount > 0)
            parts.Add($"缺少 {result.MissingCount} 个图层");
        if (result.SkippedCount > 0)
            parts.Add($"跳过 {result.SkippedCount} 个图层");

        if (parts.Count == 0)
            ed.WriteMessage("\nC_TOOL：F_DD 没有需要恢复的图层状态。");
        else
            ed.WriteMessage("\nC_TOOL：已恢复图层显示，" + string.Join("，", parts) + "。");
    }

    private static bool TryCollectSelectedLayerNames(
        Document doc,
        out HashSet<string> keepLayerNames,
        out bool cancelled)
    {
        keepLayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cancelled = false;

        var ed = doc.Editor;
        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            keepLayerNames = ResolveLayerNames(doc, implied.Value);
            return keepLayerNames.Count > 0;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nC_TOOL：选择要保留显示的图层对象："
        };
        var result = ed.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null)
        {
            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }

        keepLayerNames = ResolveLayerNames(doc, result.Value);
        return keepLayerNames.Count > 0;
    }

    private static HashSet<string> ResolveLayerNames(Document doc, SelectionSet selectionSet)
    {
        var keepLayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CadDatabaseScope.Read(doc, (_, tr) =>
        {
            foreach (SelectedObject? item in selectionSet)
            {
                if (item == null || item.ObjectId.IsNull)
                    continue;

                try
                {
                    if (!CadDatabaseScope.TryOpenAs<Entity>(tr, item.ObjectId, OpenMode.ForRead, out var entity) ||
                        entity == null)
                    {
                        continue;
                    }

                    var layerName = (entity.Layer ?? "").Trim();
                    if (layerName.Length > 0)
                        keepLayerNames.Add(layerName);
                }
                catch (InvalidOperationException ex)
                {
                    C_toolsDiagnostics.LogNonFatal("F_DD 读取所选对象图层失败（无效操作）", ex);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("F_DD 读取所选对象图层失败（CAD）", ex);
                }
            }
        });

        return keepLayerNames;
    }

    private static ObjectId FindReplacementCurrentLayerId(
        Transaction tr,
        LayerTable lt,
        IEnumerable<string> keepLayerNames)
    {
        foreach (var layerName in keepLayerNames)
        {
            if (!lt.Has(layerName))
                continue;

            var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, lt[layerName], OpenMode.ForRead);
            if (layer.IsOff || layer.IsFrozen)
                continue;

            return layer.ObjectId;
        }

        return ObjectId.Null;
    }

    private static bool TryRestoreCurrentLayer(
        Transaction tr,
        Database db,
        LayerTable lt,
        string originalCurrentLayerName)
    {
        if (string.IsNullOrWhiteSpace(originalCurrentLayerName) || !lt.Has(originalCurrentLayerName))
            return false;

        try
        {
            var layerId = lt[originalCurrentLayerName];
            var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead);
            if (layer.IsOff || layer.IsFrozen)
                return false;

            db.Clayer = layerId;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_DD 恢复当前层 {originalCurrentLayerName} 失败（无效操作）", ex);
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_DD 恢复当前层 {originalCurrentLayerName} 失败（CAD）", ex);
            return false;
        }
    }

    private sealed class LayerIsolationSnapshot
    {
        internal LayerIsolationSnapshot(
            string originalCurrentLayerName,
            Dictionary<string, bool> hiddenLayerOffStates)
        {
            OriginalCurrentLayerName = originalCurrentLayerName;
            HiddenLayerOffStates = hiddenLayerOffStates;
        }

        internal string OriginalCurrentLayerName { get; }

        internal IReadOnlyDictionary<string, bool> HiddenLayerOffStates { get; }
    }
}
