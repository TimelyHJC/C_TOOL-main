using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace C_toolsDddPlugin;

/// <summary>从当前图纸「预选」中读取单行/多行文字内容（用于 V_DDD 面板）；浮窗取走焦点后 SelectImplied 常为空，故增加快照。</summary>
internal static class DddDrawingSelectionSync
{
    private sealed class DddSelectionSnapshot
    {
        internal ObjectId[] TextEntityIds { get; init; } = [];
        internal ObjectId[] LeaderEntityIds { get; init; } = [];
    }

    private static readonly Dictionary<string, DddSelectionSnapshot> s_snapshots = new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearCapturedTextSelection()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        ClearCapturedTextSelection(doc);
    }

    internal static void ClearCapturedTextSelection(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        s_snapshots.Remove(key);
    }

    /// <summary>记录当前图纸预选中的文字与多重引线，供浮窗焦点在 WPF 上时仍可批量写回。</summary>
    internal static void CaptureImpliedTextSelection(Document doc)
    {
        ClearCapturedTextSelection(doc);
        try
        {
            var ed = doc.Editor;
            var psr = ed.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value == null)
                return;

            var ids = psr.Value.GetObjectIds();
            if (ids.Length == 0)
                return;

            var textList = new List<ObjectId>(ids.Length);
            var leaderList = new List<ObjectId>(ids.Length);
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid)
                        continue;
                    try
                    {
                        var o = tr.GetObject(id, OpenMode.ForRead, false);
                        switch (o)
                        {
                            case MText:
                            case DBText:
                                textList.Add(id);
                                break;
                            case MLeader:
                                leaderList.Add(id);
                                break;
                        }
                    }
                    catch (AcRx.Exception)
                    {
                    }
                }

                tr.Commit();
            }

            if (textList.Count == 0 && leaderList.Count == 0)
                return;

            SaveSnapshot(doc, new DddSelectionSnapshot
            {
                TextEntityIds = textList.ToArray(),
                LeaderEntityIds = leaderList.ToArray()
            });
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("快照预选文字/引线实体", ex);
        }
    }

    internal static string? TryGetSelectedTextContents(Document doc)
    {
        try
        {
            var ed = doc.Editor;
            var psr = ed.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value == null)
                return null;

            var ids = psr.Value.GetObjectIds();
            if (ids.Length == 0)
                return null;

            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid)
                        continue;
                        var o = tr.GetObject(id, OpenMode.ForRead);
                        switch (o)
                        {
                        case MText mt:
                            return DddTextContentHelper.ToEditableText(mt);
                        case DBText dt:
                            return DddTextContentHelper.ToEditableText(dt.TextString, isMText: false);
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取预选文字实体", ex);
        }

        return null;
    }

    internal static bool TryApplyTextToCapturedSelection(Document doc, string newText)
    {
        var snapshot = GetSnapshot(doc);
        if (snapshot == null || snapshot.TextEntityIds.Length == 0)
            return false;
        if (string.IsNullOrWhiteSpace(newText))
            return false;

        try
        {
            var any = false;
            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                foreach (var id in snapshot.TextEntityIds)
                {
                    if (!id.IsValid)
                        continue;
                    try
                    {
                        var o = tr.GetObject(id, OpenMode.ForWrite, false);
                        switch (o)
                        {
                            case MText mt:
                                mt.Contents = DddTextContentHelper.ToMTextContents(newText);
                                any = true;
                                break;
                            case DBText dt:
                                dt.TextString = newText;
                                any = true;
                                break;
                        }
                    }
                    catch (AcRx.Exception)
                    {
                    }
                }

                if (any)
                    tr.Commit();
            }

            return any;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("将列表文字写回快照实体", ex);
            return false;
        }
    }

    internal static bool TryCaptureSelectedTextContents(
        Document doc,
        out IReadOnlyList<string> texts,
        out int selectedCount)
    {
        texts = Array.Empty<string>();
        selectedCount = 0;

        try
        {
            var ed = doc.Editor;
            var psr = ed.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value == null)
                return false;

            return TryCaptureTextContentsFromObjectIds(doc, psr.Value.GetObjectIds(), out texts, out selectedCount);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取预选文字列表", ex);
            texts = Array.Empty<string>();
            selectedCount = 0;
            return false;
        }
    }

    internal static bool TryPromptAndCaptureSelectedTextContents(
        Document doc,
        out IReadOnlyList<string> texts,
        out int selectedCount)
    {
        texts = Array.Empty<string>();
        selectedCount = 0;

        try
        {
            var ed = doc.Editor;
            var promptOptions = new PromptSelectionOptions
            {
                AllowDuplicates = false,
                MessageForAdding = "\nC_TOOL：选择单行文字或多行文字："
            };
            promptOptions.MessageForRemoval = "\nC_TOOL：移除要快改的文字：";

            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Operator, "<OR"),
                new TypedValue((int)DxfCode.Start, "TEXT"),
                new TypedValue((int)DxfCode.Start, "MTEXT"),
                new TypedValue((int)DxfCode.Operator, "OR>")
            });

            var selection = ed.GetSelection(promptOptions, filter);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return false;

            var ids = selection.Value.GetObjectIds();
            if (ids.Length == 0)
                return false;

            ed.SetImpliedSelection(ids);
            return TryCaptureTextContentsFromObjectIds(doc, ids, out texts, out selectedCount);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("交互选择文字列表", ex);
            texts = Array.Empty<string>();
            selectedCount = 0;
            return false;
        }
    }

    internal static bool TryCaptureAndReplaceTextInImpliedSelection(
        Document doc,
        string? findText,
        string? replaceText,
        out int selectedCount,
        out int changedCount)
    {
        CaptureImpliedTextSelection(doc);
        return TryReplaceTextInCapturedSelection(doc, findText, replaceText, out selectedCount, out changedCount);
    }

    internal static bool TryCaptureAndApplyMLeaderStyleToImpliedSelection(
        Document doc,
        string styleName,
        out int changedCount,
        out int skippedCount,
        out string error)
    {
        CaptureImpliedTextSelection(doc);
        return TryApplyMLeaderStyleToCapturedSelection(doc, styleName, out changedCount, out skippedCount, out error);
    }

    internal static bool TryCaptureAndApplyLeaderOverridesToImpliedSelection(
        Document doc,
        MLeaderToolSettingsDto settings,
        out int changedCount,
        out int skippedCount)
    {
        CaptureImpliedTextSelection(doc);
        return TryApplyLeaderOverridesToCapturedSelection(doc, settings, out changedCount, out skippedCount);
    }

    private static bool TryReplaceTextInCapturedSelection(
        Document doc,
        string? findText,
        string? replaceText,
        out int selectedCount,
        out int changedCount)
    {
        selectedCount = 0;
        changedCount = 0;
        var snapshot = GetSnapshot(doc);
        if (snapshot == null || snapshot.TextEntityIds.Length == 0)
            return false;

        var find = findText ?? "";
        var replace = replaceText ?? "";
        var replaceAll = find.Length == 0;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                foreach (var id in snapshot.TextEntityIds)
                {
                    if (!id.IsValid)
                        continue;
                    try
                    {
                        var o = tr.GetObject(id, OpenMode.ForWrite, false);
                        switch (o)
                        {
                            case MText mt:
                            {
                                selectedCount++;
                                var oldText = mt.Contents ?? "";
                                var newText = replaceAll ? replace : DddTextReplaceHelper.ReplaceOrdinal(oldText, find, replace);
                                if (oldText == newText)
                                    break;
                                mt.Contents = newText;
                                changedCount++;
                                break;
                            }
                            case DBText dt:
                            {
                                selectedCount++;
                                var oldText = dt.TextString ?? "";
                                var newText = replaceAll ? replace : DddTextReplaceHelper.ReplaceOrdinal(oldText, find, replace);
                                if (oldText == newText)
                                    break;
                                dt.TextString = newText;
                                changedCount++;
                                break;
                            }
                        }
                    }
                    catch (AcRx.Exception)
                    {
                    }
                }

                if (changedCount > 0)
                    tr.Commit();
            }

            return selectedCount > 0;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("批量替换预选文字实体", ex);
            return false;
        }
    }

    private static bool TryApplyMLeaderStyleToCapturedSelection(
        Document doc,
        string styleName,
        out int changedCount,
        out int skippedCount,
        out string error)
    {
        changedCount = 0;
        skippedCount = 0;
        error = "";
        var snapshot = GetSnapshot(doc);
        if (snapshot == null || snapshot.LeaderEntityIds.Length == 0)
            return false;

        var style = (styleName ?? "").Trim();
        if (style.Length == 0)
        {
            error = "当前未选择多重引线样式。";
            return false;
        }

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var db = doc.Database;
                if (!MLeaderStyleHelper.TryGetMLeaderStyleObjectId(tr, db, style, out var styleId) || styleId.IsNull)
                {
                    error = "当前图纸中未找到多重引线样式「" + style + "」。";
                    return false;
                }

                foreach (var id in snapshot.LeaderEntityIds)
                {
                    if (!id.IsValid)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForWrite, false) is not MLeader ml)
                        {
                            skippedCount++;
                            continue;
                        }

                        ml.MLeaderStyle = styleId;
                        MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
                        try
                        {
                            var mt = ml.MText;
                            if (mt != null)
                            {
                                MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
                                ml.MText = mt;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            C_toolsDiagnostics.LogNonFatal("批量切换引线样式时同步 MText", ex);
                        }

                        try
                        {
                            ml.RecordGraphicsModified(true);
                        }
                        catch
                        {
                        }

                        changedCount++;
                    }
                    catch (AcRx.Exception)
                    {
                        skippedCount++;
                    }
                }

                if (changedCount > 0)
                    tr.Commit();
            }

            return changedCount > 0;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("批量切换预选多重引线样式", ex);
            error = ex.Message;
            return false;
        }
    }

    private static bool TryApplyLeaderOverridesToCapturedSelection(
        Document doc,
        MLeaderToolSettingsDto settings,
        out int changedCount,
        out int skippedCount)
    {
        changedCount = 0;
        skippedCount = 0;
        var snapshot = GetSnapshot(doc);
        if (snapshot == null || snapshot.LeaderEntityIds.Length == 0)
            return false;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var db = doc.Database;
                foreach (var id in snapshot.LeaderEntityIds)
                {
                    if (!id.IsValid)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForWrite, false) is not MLeader ml)
                        {
                            skippedCount++;
                            continue;
                        }

                        MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
                        try
                        {
                            var mt = ml.MText;
                            if (mt != null)
                            {
                                MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
                                ml.MText = mt;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            C_toolsDiagnostics.LogNonFatal("批量应用引线预设时同步 MText", ex);
                        }

                        MLeaderPluginAppearanceApplier.ApplyToMLeader(ml, tr, db, settings);
                        try
                        {
                            ml.RecordGraphicsModified(true);
                        }
                        catch
                        {
                        }

                        changedCount++;
                    }
                    catch (AcRx.Exception)
                    {
                        skippedCount++;
                    }
                }

                if (changedCount > 0)
                    tr.Commit();
            }

            return changedCount > 0;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("批量应用预选多重引线版式", ex);
            return false;
        }
    }

    /// <summary>
    /// 在 CAD 主线程上调用：先按当前 SelectImplied 刷新快照，再写回其中的 MText/DBText。
    /// 用于浮窗内点击列表，使预选在焦点于 WPF 时仍能被识别并只改字、不插引线。
    /// </summary>
    internal static bool TryCaptureAndApplyTextToImpliedSelection(Document doc, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText))
            return false;

        var previousSnapshot = GetSnapshot(doc);
        CaptureImpliedTextSelection(doc);
        var refreshedSnapshot = GetSnapshot(doc);
        if ((refreshedSnapshot == null || refreshedSnapshot.TextEntityIds.Length == 0) &&
            previousSnapshot != null &&
            previousSnapshot.TextEntityIds.Length > 0)
        {
            SaveSnapshot(doc, previousSnapshot);
        }

        return TryApplyTextToCapturedSelection(doc, newText);
    }

    private static bool TryCaptureTextContentsFromObjectIds(
        Document doc,
        IReadOnlyList<ObjectId> ids,
        out IReadOnlyList<string> texts,
        out int selectedCount)
    {
        texts = Array.Empty<string>();
        selectedCount = 0;
        ClearCapturedTextSelection(doc);

        try
        {
            if (ids.Count == 0)
                return false;

            var textIds = new List<ObjectId>(ids.Count);
            var distinctTexts = new List<string>(ids.Count);
            var seenTexts = new HashSet<string>(StringComparer.Ordinal);

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid)
                        continue;

                    try
                    {
                        var entity = tr.GetObject(id, OpenMode.ForRead, false);
                        string? textValue = entity switch
                        {
                            MText mt => DddTextContentHelper.ToEditableText(mt),
                            DBText dt => DddTextContentHelper.ToEditableText(dt.TextString, isMText: false),
                            _ => null
                        };

                        if (!DddTextContentHelper.HasVisibleText(textValue))
                            continue;

                        var normalizedText = textValue!;
                        textIds.Add(id);
                        selectedCount++;

                        if (seenTexts.Add(normalizedText))
                            distinctTexts.Add(normalizedText);
                    }
                    catch (AcRx.Exception)
                    {
                    }
                }

                tr.Commit();
            }

            if (textIds.Count == 0)
                return false;

            SaveSnapshot(doc, new DddSelectionSnapshot
            {
                TextEntityIds = textIds.ToArray(),
                LeaderEntityIds = Array.Empty<ObjectId>()
            });

            texts = distinctTexts;
            return distinctTexts.Count > 0;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("整理选中文字列表", ex);
            texts = Array.Empty<string>();
            selectedCount = 0;
            return false;
        }
    }

    private static DddSelectionSnapshot? GetSnapshot(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return null;

        return s_snapshots.TryGetValue(key, out var snapshot) ? snapshot : null;
    }

    private static void SaveSnapshot(Document doc, DddSelectionSnapshot snapshot)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        s_snapshots[key] = snapshot;
    }

    private static string GetDocumentKey(Document doc)
    {
        try
        {
            return doc.Database.FingerprintGuid.ToString();
        }
        catch
        {
            return doc.Name ?? string.Empty;
        }
    }
}
