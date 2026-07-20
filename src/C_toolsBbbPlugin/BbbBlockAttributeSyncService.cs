using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal sealed class BbbBlockAttributeSyncResult
{
    internal int SelectedBlockDefinitions { get; set; }
    internal int DefinitionsWithoutAttributes { get; set; }
    internal int SyncedBlockReferences { get; set; }
    internal int AddedAttributes { get; set; }
    internal int UpdatedAttributes { get; set; }
    internal int RemovedAttributes { get; set; }
}

internal sealed class BbbAttributeValueSnapshot
{
    internal string TextString { get; set; } = "";
    internal string MTextContents { get; set; } = "";
}

internal static class BbbBlockAttributeSyncService
{
    private const string CommandName = BbbPluginCommandIds.BlockAttributeSync;

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;

        try
        {
            var selectedIds = SelectBlockReferences(editor);
            if (selectedIds == null || selectedIds.Count == 0)
                return;

            var result = CadDatabaseScope.Write(
                doc,
                (_, transaction) => SyncSelectedBlockDefinitions(transaction, selectedIds),
                requireDocumentLock: true);

            editor.Regen();
            editor.WriteMessage(BuildResultMessage(result));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        {
            editor.WriteMessage($"\n{CommandName}：已取消。");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 同步块属性失败", ex);
            editor.WriteMessage($"\n{CommandName}：同步块属性失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<ObjectId>? SelectBlockReferences(Editor editor)
    {
        var implied = editor.SelectImplied();
        var impliedIds = ExtractBlockReferenceIds(implied);
        if (impliedIds.Count > 0)
            return impliedIds;

        var options = new PromptSelectionOptions
        {
            MessageForAdding = $"\n{CommandName}：请选择需要同步属性的块："
        };

        var picked = editor.GetSelection(options, BuildBlockSelectionFilter());
        if (picked.Status == PromptStatus.Cancel)
        {
            editor.WriteMessage($"\n{CommandName}：已取消。");
            return null;
        }

        var pickedIds = ExtractBlockReferenceIds(picked);
        if (pickedIds.Count == 0)
        {
            editor.WriteMessage($"\n{CommandName}：未选择任何块。");
            return null;
        }

        return pickedIds;
    }

    private static List<ObjectId> ExtractBlockReferenceIds(PromptSelectionResult selection)
    {
        if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            return new List<ObjectId>();

        return selection.Value
            .GetObjectIds()
            .Where(x => !x.IsInvalid())
            .Distinct()
            .ToList();
    }

    private static SelectionFilter BuildBlockSelectionFilter()
    {
        return new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "INSERT")
        });
    }

    private static BbbBlockAttributeSyncResult SyncSelectedBlockDefinitions(
        Transaction transaction,
        IReadOnlyList<ObjectId> selectedIds)
    {
        var result = new BbbBlockAttributeSyncResult();
        var targetDefinitionIds = ResolveTargetDefinitionIds(transaction, selectedIds);
        result.SelectedBlockDefinitions = targetDefinitionIds.Count;

        foreach (var definitionId in targetDefinitionIds)
        {
            if (!TryPrepareDefinition(transaction, definitionId, out var blockDefinition) ||
                blockDefinition == null)
            {
                continue;
            }

            var definitionAttributes = ReadAttributeDefinitions(transaction, blockDefinition);
            if (definitionAttributes.Count == 0)
            {
                result.DefinitionsWithoutAttributes++;
                continue;
            }

            var referenceIds = CollectBlockReferenceIds(transaction, blockDefinition);
            foreach (var referenceId in referenceIds)
            {
                if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, referenceId, OpenMode.ForWrite, out var blockReference) ||
                    blockReference == null)
                {
                    continue;
                }

                var referenceDefinitions = ReadAttributeDefinitionsForReference(transaction, blockReference, definitionAttributes);
                if (referenceDefinitions.Count == 0)
                    continue;

                var perReferenceResult = SyncBlockReference(transaction, blockReference, referenceDefinitions);
                if (!perReferenceResult.Changed)
                    continue;

                result.SyncedBlockReferences++;
                result.AddedAttributes += perReferenceResult.AddedAttributes;
                result.UpdatedAttributes += perReferenceResult.UpdatedAttributes;
                result.RemovedAttributes += perReferenceResult.RemovedAttributes;
            }
        }

        return result;
    }

    private static List<ObjectId> ResolveTargetDefinitionIds(
        Transaction transaction,
        IReadOnlyList<ObjectId> selectedIds)
    {
        var definitionIds = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();

        foreach (var selectedId in selectedIds)
        {
            if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, selectedId, OpenMode.ForRead, out var blockReference) ||
                blockReference == null)
            {
                continue;
            }

            var definitionId = ResolveTargetDefinitionId(blockReference);
            if (definitionId.IsInvalid() || !seen.Add(definitionId))
                continue;

            definitionIds.Add(definitionId);
        }

        return definitionIds;
    }

    private static ObjectId ResolveTargetDefinitionId(BlockReference blockReference)
    {
        if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsInvalid())
            return blockReference.DynamicBlockTableRecord;

        return blockReference.BlockTableRecord;
    }

    private static bool TryPrepareDefinition(
        Transaction transaction,
        ObjectId definitionId,
        out BlockTableRecord? blockDefinition)
    {
        blockDefinition = null;
        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, definitionId, OpenMode.ForWrite, out var writableDefinition) ||
            writableDefinition == null)
        {
            return false;
        }

        TryUpdateAnonymousBlocks(writableDefinition);
        blockDefinition = writableDefinition;
        return true;
    }

    private static void TryUpdateAnonymousBlocks(BlockTableRecord blockDefinition)
    {
        try
        {
            if (blockDefinition.IsDynamicBlock)
                blockDefinition.UpdateAnonymousBlocks();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 更新动态图块匿名定义失败（CAD）", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 更新动态图块匿名定义失败（无效操作）", ex);
        }
    }

    private static List<AttributeDefinition> ReadAttributeDefinitions(
        Transaction transaction,
        BlockTableRecord blockDefinition)
    {
        var definitions = new List<AttributeDefinition>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ObjectId entityId in blockDefinition)
        {
            if (!CadDatabaseScope.TryOpenAs<AttributeDefinition>(transaction, entityId, OpenMode.ForRead, out var attributeDefinition) ||
                attributeDefinition == null ||
                attributeDefinition.Constant)
            {
                continue;
            }

            var tag = NormalizeTag(attributeDefinition.Tag);
            if (tag.Length == 0 || !seenTags.Add(tag))
                continue;

            definitions.Add(attributeDefinition);
        }

        return definitions;
    }

    private static List<ObjectId> CollectBlockReferenceIds(
        Transaction transaction,
        BlockTableRecord blockDefinition)
    {
        var referenceIds = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();

        AddBlockReferenceIds(blockDefinition, referenceIds, seen);

        if (!blockDefinition.IsDynamicBlock)
            return referenceIds;

        ObjectIdCollection anonymousBlockIds;
        try
        {
            anonymousBlockIds = blockDefinition.GetAnonymousBlockIds();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 读取动态图块匿名定义失败（CAD）", ex);
            return referenceIds;
        }

        foreach (ObjectId anonymousBlockId in anonymousBlockIds)
        {
            if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, anonymousBlockId, OpenMode.ForRead, out var anonymousDefinition) ||
                anonymousDefinition == null)
            {
                continue;
            }

            AddBlockReferenceIds(anonymousDefinition, referenceIds, seen);
        }

        return referenceIds;
    }

    private static void AddBlockReferenceIds(
        BlockTableRecord blockDefinition,
        List<ObjectId> referenceIds,
        HashSet<ObjectId> seen)
    {
        ObjectIdCollection ids;
        try
        {
            ids = blockDefinition.GetBlockReferenceIds(directOnly: false, forceValidity: true);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 读取块参照失败（CAD）", ex);
            return;
        }

        foreach (ObjectId referenceId in ids)
        {
            if (referenceId.IsInvalid() || !seen.Add(referenceId))
                continue;

            referenceIds.Add(referenceId);
        }
    }

    private static List<AttributeDefinition> ReadAttributeDefinitionsForReference(
        Transaction transaction,
        BlockReference blockReference,
        IReadOnlyList<AttributeDefinition> fallbackDefinitions)
    {
        if (CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, blockReference.BlockTableRecord, OpenMode.ForRead, out var referenceDefinition) &&
            referenceDefinition != null)
        {
            var referenceDefinitions = ReadAttributeDefinitions(transaction, referenceDefinition);
            if (referenceDefinitions.Count > 0)
                return referenceDefinitions;
        }

        return fallbackDefinitions.ToList();
    }

    private static BbbBlockReferenceSyncResult SyncBlockReference(
        Transaction transaction,
        BlockReference blockReference,
        IReadOnlyList<AttributeDefinition> attributeDefinitions)
    {
        var result = new BbbBlockReferenceSyncResult();
        var definitionsByTag = attributeDefinitions.ToDictionary(x => NormalizeTag(x.Tag), StringComparer.OrdinalIgnoreCase);
        var existingByTag = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
        var extraAttributes = new List<AttributeReference>();

        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (!CadDatabaseScope.TryOpenAs<AttributeReference>(transaction, attributeId, OpenMode.ForWrite, out var attributeReference) ||
                attributeReference == null)
            {
                continue;
            }

            var tag = NormalizeTag(attributeReference.Tag);
            if (!definitionsByTag.ContainsKey(tag))
            {
                extraAttributes.Add(attributeReference);
                continue;
            }

            if (existingByTag.ContainsKey(tag))
            {
                extraAttributes.Add(attributeReference);
                continue;
            }

            existingByTag[tag] = attributeReference;
        }

        foreach (var attributeDefinition in attributeDefinitions)
        {
            var tag = NormalizeTag(attributeDefinition.Tag);
            if (tag.Length == 0)
                continue;

            if (existingByTag.TryGetValue(tag, out var attributeReference))
            {
                var snapshot = CaptureAttributeValue(attributeReference);
                ApplyDefinitionToReference(attributeReference, attributeDefinition, blockReference);
                RestoreAttributeValue(attributeReference, snapshot);
                result.UpdatedAttributes++;
                continue;
            }

            CreateAttributeReference(transaction, blockReference, attributeDefinition);
            result.AddedAttributes++;
        }

        foreach (var attributeReference in extraAttributes)
        {
            attributeReference.Erase();
            result.RemovedAttributes++;
        }

        return result;
    }

    private static void CreateAttributeReference(
        Transaction transaction,
        BlockReference blockReference,
        AttributeDefinition attributeDefinition)
    {
        using var attributeReference = new AttributeReference();
        ApplyDefinitionToReference(attributeReference, attributeDefinition, blockReference);
        blockReference.AttributeCollection.AppendAttribute(attributeReference);
        transaction.AddNewlyCreatedDBObject(attributeReference, true);
    }

    private static void ApplyDefinitionToReference(
        AttributeReference attributeReference,
        AttributeDefinition attributeDefinition,
        BlockReference blockReference)
    {
        attributeReference.SetAttributeFromBlock(attributeDefinition, blockReference.BlockTransform);
        attributeReference.Position = attributeDefinition.Position.TransformBy(blockReference.BlockTransform);

        if (UsesAlignmentPoint(attributeDefinition.Justify))
            attributeReference.AlignmentPoint = attributeDefinition.AlignmentPoint.TransformBy(blockReference.BlockTransform);
    }

    private static BbbAttributeValueSnapshot CaptureAttributeValue(AttributeReference attributeReference)
    {
        var snapshot = new BbbAttributeValueSnapshot
        {
            TextString = attributeReference.TextString ?? ""
        };

        if (attributeReference.IsMTextAttribute)
        {
            var mText = attributeReference.MTextAttribute;
            snapshot.MTextContents = mText?.Contents ?? snapshot.TextString;
        }

        return snapshot;
    }

    private static void RestoreAttributeValue(
        AttributeReference attributeReference,
        BbbAttributeValueSnapshot snapshot)
    {
        var value = snapshot.MTextContents.Length > 0 ? snapshot.MTextContents : snapshot.TextString;
        attributeReference.TextString = value;

        if (!attributeReference.IsMTextAttribute)
            return;

        var mText = attributeReference.MTextAttribute;
        if (mText == null)
            return;

        mText.Contents = value;
        attributeReference.MTextAttribute = mText;
    }

    private static string NormalizeTag(string? tag)
    {
        return (tag ?? "").Trim();
    }

    private static bool UsesAlignmentPoint(AttachmentPoint attachmentPoint)
    {
        return attachmentPoint != AttachmentPoint.BaseLeft;
    }

    private static string BuildResultMessage(BbbBlockAttributeSyncResult result)
    {
        if (result.SelectedBlockDefinitions == 0)
            return $"\n{CommandName}：未选择任何块。";

        if (result.SyncedBlockReferences == 0)
        {
            return result.DefinitionsWithoutAttributes > 0
                ? $"\n{CommandName}：所选块定义中没有可同步的非固定属性。"
                : $"\n{CommandName}：属性已是最新，无需同步。";
        }

        return $"\n{CommandName}：已同步 {result.SelectedBlockDefinitions} 个块定义、{result.SyncedBlockReferences} 个块参照。新增属性 {result.AddedAttributes} 项，更新属性 {result.UpdatedAttributes} 项，删除多余属性 {result.RemovedAttributes} 项。";
    }

    private sealed class BbbBlockReferenceSyncResult
    {
        internal int AddedAttributes { get; set; }
        internal int UpdatedAttributes { get; set; }
        internal int RemovedAttributes { get; set; }
        internal bool Changed => AddedAttributes > 0 || UpdatedAttributes > 0 || RemovedAttributes > 0;
    }
}
