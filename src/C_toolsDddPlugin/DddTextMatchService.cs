using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsDddPlugin;

internal static class DddTextMatchService
{
    private const string CommandName = C_toolsCommandIds.Ddd.TextMatch;

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;
        var preselectedTargetIds = GetPreselectedTargetIds(doc, out var hadPreselection);
        if (hadPreselection)
            editor.SetImpliedSelection(Array.Empty<ObjectId>());

        try
        {
            if (!TryPromptSourceText(doc, out var sourceId, out var sourceText))
                return;

            if (!DddTextContentHelper.HasVisibleText(sourceText))
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 来源文字为空，无法匹配。");
                return;
            }

            var targetIds = ExcludeSourceId(preselectedTargetIds, sourceId);
            if (targetIds.Length == 0 &&
                !TryPromptTargetSelection(doc, sourceId, out targetIds))
            {
                return;
            }

            if (targetIds.Length == 0)
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 未选择可匹配的目标文字。");
                return;
            }

            var result = ApplyTextToTargets(doc, targetIds, sourceId, sourceText);
            if (result.ChangedCount > 0)
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 已匹配 {result.ChangedCount} 个目标文字。");
                return;
            }

            if (result.SupportedCount > 0)
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 目标文字内容已一致，无需修改。");
                return;
            }

            editor.WriteMessage($"\nC_TOOL：{CommandName} 所选目标中没有可写入的文字对象。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 匹配文字失败（无效操作）", ex);
            editor.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 匹配文字失败（CAD）", ex);
            editor.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 匹配文字失败", ex);
            editor.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
    }

    private static bool TryPromptSourceText(Document doc, out ObjectId sourceId, out string sourceText)
    {
        sourceId = ObjectId.Null;
        sourceText = "";

        var options = new PromptNestedEntityOptions($"\nC_TOOL：{CommandName} 选择作为内容来源的文字或多重引线文字：");
        var result = doc.Editor.GetNestedEntity(options);
        if (result.Status != PromptStatus.OK || result.ObjectId.IsNull)
            return false;

        if (!DddReadableTextService.TryReadTextFromId(doc, result.ObjectId, out sourceText))
        {
            doc.Editor.WriteMessage($"\nC_TOOL：{CommandName} 来源对象没有可读取的文字内容。");
            return false;
        }

        sourceId = result.ObjectId;
        return true;
    }

    private static bool TryPromptTargetSelection(Document doc, ObjectId sourceId, out ObjectId[] targetIds)
    {
        targetIds = Array.Empty<ObjectId>();

        var options = new PromptSelectionOptions
        {
            AllowDuplicates = false,
            MessageForAdding = $"\nC_TOOL：{CommandName} 选择要匹配内容的目标文字：",
            MessageForRemoval = $"\nC_TOOL：{CommandName} 移除目标文字："
        };

        var result = doc.Editor.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null)
            return false;

        targetIds = ExcludeSourceId(GetSupportedTargetIds(doc, result.Value.GetObjectIds()), sourceId);
        if (targetIds.Length == 0)
            doc.Editor.WriteMessage($"\nC_TOOL：{CommandName} 未选择可匹配的目标文字。");

        return targetIds.Length > 0;
    }

    private static ObjectId[] GetPreselectedTargetIds(Document doc, out bool hadPreselection)
    {
        hadPreselection = false;

        var result = doc.Editor.SelectImplied();
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
            return Array.Empty<ObjectId>();

        hadPreselection = true;
        return GetSupportedTargetIds(doc, result.Value.GetObjectIds());
    }

    private static ObjectId[] GetSupportedTargetIds(Document doc, IReadOnlyList<ObjectId> ids)
    {
        if (ids.Count == 0)
            return Array.Empty<ObjectId>();

        var targetIds = new List<ObjectId>(ids.Count);
        var seen = new HashSet<ObjectId>();

        using var transaction = doc.TransactionManager.StartTransaction();
        foreach (var id in ids)
        {
            if (id.IsNull || !id.IsValid || !seen.Add(id))
                continue;

            try
            {
                var dbObject = transaction.GetObject(id, OpenMode.ForRead, false);
                if (IsSupportedTextTarget(dbObject))
                    targetIds.Add(id);
            }
            catch (AcadRuntimeException)
            {
            }
        }

        return targetIds.Count == 0 ? Array.Empty<ObjectId>() : targetIds.ToArray();
    }

    private static TextMatchResult ApplyTextToTargets(
        Document doc,
        IReadOnlyList<ObjectId> targetIds,
        ObjectId sourceId,
        string sourceText)
    {
        var result = new TextMatchResult();
        var seen = new HashSet<ObjectId>();

        using (doc.LockDocument())
        using (var transaction = doc.TransactionManager.StartTransaction())
        {
            foreach (var targetId in targetIds)
            {
                if (targetId.IsNull || !targetId.IsValid || targetId == sourceId || !seen.Add(targetId))
                    continue;

                try
                {
                    var dbObject = transaction.GetObject(targetId, OpenMode.ForWrite, false);
                    if (!TryWriteEditableText(dbObject, sourceText, out var changed))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    result.SupportedCount++;
                    if (changed)
                        result.ChangedCount++;
                }
                catch (AcadRuntimeException)
                {
                    result.SkippedCount++;
                }
            }

            if (result.ChangedCount > 0)
                transaction.Commit();
        }

        return result;
    }

    private static bool IsSupportedTextTarget(DBObject dbObject)
    {
        return dbObject switch
        {
            AttributeReference => true,
            DBText => true,
            MText => true,
            Dimension => true,
            MLeader leader => TryGetMLeaderMText(leader, out _),
            _ => false
        };
    }

    private static bool TryReadEditableText(DBObject dbObject, out string text)
    {
        text = "";

        switch (dbObject)
        {
            case AttributeReference attribute:
                text = ReadAttributeText(attribute);
                return true;
            case DBText dbText:
                text = DddTextContentHelper.ToEditableText(dbText.TextString, isMText: false);
                return true;
            case MText mText:
                text = DddTextContentHelper.ToEditableText(mText);
                return true;
            case Dimension dimension:
                text = DddTextContentHelper.ToEditableText(dimension.DimensionText, isMText: false);
                return true;
            case MLeader leader when TryReadMLeaderText(leader, out var leaderText):
                text = leaderText;
                return true;
            default:
                return false;
        }
    }

    private static bool TryWriteEditableText(DBObject dbObject, string text, out bool changed)
    {
        changed = false;

        switch (dbObject)
        {
            case AttributeReference attribute:
            {
                var oldText = ReadAttributeText(attribute);
                changed = !string.Equals(oldText, text, StringComparison.Ordinal);
                if (!changed)
                    return true;

                attribute.TextString = text;
                if (attribute.IsMTextAttribute)
                {
                    var mText = attribute.MTextAttribute;
                    if (mText != null)
                    {
                        mText.Contents = DddTextContentHelper.ToMTextContents(text);
                        attribute.MTextAttribute = mText;
                    }
                }

                return true;
            }

            case DBText dbText:
            {
                var oldText = DddTextContentHelper.ToEditableText(dbText.TextString, isMText: false);
                changed = !string.Equals(oldText, text, StringComparison.Ordinal);
                if (changed)
                    dbText.TextString = text;
                return true;
            }

            case MText mText:
            {
                var oldText = DddTextContentHelper.ToEditableText(mText);
                changed = !string.Equals(oldText, text, StringComparison.Ordinal);
                if (changed)
                    mText.Contents = DddTextContentHelper.ToMTextContents(text);
                return true;
            }

            case Dimension dimension:
            {
                var oldText = DddTextContentHelper.ToEditableText(dimension.DimensionText, isMText: false);
                changed = !string.Equals(oldText, text, StringComparison.Ordinal);
                if (changed)
                    dimension.DimensionText = text;
                return true;
            }

            case MLeader leader:
            {
                if (!TryGetMLeaderMText(leader, out var mText) || mText == null)
                    return false;

                var oldText = DddTextContentHelper.ToEditableText(mText);
                changed = !string.Equals(oldText, text, StringComparison.Ordinal);
                if (changed)
                {
                    mText.Contents = DddTextContentHelper.ToMTextContents(text);
                    leader.MText = mText;
                    TryRecordGraphicsModified(leader);
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static string ReadAttributeText(AttributeReference attribute)
    {
        if (!attribute.IsMTextAttribute)
            return DddTextContentHelper.ToEditableText(attribute.TextString, isMText: false);

        var mText = attribute.MTextAttribute;
        if (mText != null)
        {
            var text = DddTextContentHelper.ToEditableText(mText);
            if (text.Length > 0)
                return text;
        }

        return DddTextContentHelper.ToEditableText(attribute.TextString, isMText: false);
    }

    private static bool TryReadMLeaderText(MLeader leader, out string text)
    {
        text = "";
        if (!TryGetMLeaderMText(leader, out var mText) || mText == null)
            return false;

        text = DddTextContentHelper.ToEditableText(mText);
        return true;
    }

    private static bool TryGetMLeaderMText(MLeader leader, out MText? mText)
    {
        mText = null;

        try
        {
            mText = leader.MText;
            return mText != null;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 读取多重引线文字失败（无效操作）", ex);
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 读取多重引线文字失败（CAD）", ex);
            return false;
        }
    }

    private static ObjectId[] ExcludeSourceId(IReadOnlyList<ObjectId> ids, ObjectId sourceId)
    {
        if (ids.Count == 0)
            return Array.Empty<ObjectId>();

        var targets = new List<ObjectId>(ids.Count);
        foreach (var id in ids)
        {
            if (!id.IsNull && id != sourceId)
                targets.Add(id);
        }

        return targets.Count == 0 ? Array.Empty<ObjectId>() : targets.ToArray();
    }

    private static void TryRecordGraphicsModified(Entity entity)
    {
        try
        {
            entity.RecordGraphicsModified(true);
        }
        catch
        {
        }
    }

    private sealed class TextMatchResult
    {
        internal int SupportedCount { get; set; }
        internal int ChangedCount { get; set; }
        internal int SkippedCount { get; set; }
    }
}
