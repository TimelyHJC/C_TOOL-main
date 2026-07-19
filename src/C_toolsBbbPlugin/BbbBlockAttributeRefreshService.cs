using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal static class BbbBlockAttributeRefreshService
{
    private const string CommandName = BbbPluginCommandIds.BlockAttributeRefresh;

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;

        try
        {
            var selectedIds = SelectTargetIds(editor);
            if (selectedIds == null || selectedIds.Count == 0)
                return;

            var summary = RefreshSelectedBlocks(doc, selectedIds);
            editor.Regen();
            editor.WriteMessage(BuildMessage(summary));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        {
            editor.WriteMessage($"\n{CommandName}：{UIMessages.Common.Cancelled}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 刷新图块增强属性失败", ex);
            editor.WriteMessage($"\n{CommandName}：刷新失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<ObjectId>? SelectTargetIds(Editor editor)
    {
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\n选择需要刷新增强属性文字的图块："
        };

        var picked = editor.GetSelection(options, new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "INSERT")
        }));

        if (picked.Status == PromptStatus.Cancel)
            return null;

        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            editor.WriteMessage($"\n{CommandName}：未选块。");
            return null;
        }

        return picked.Value.GetObjectIds();
    }

    private static AttributeRefreshSummary RefreshSelectedBlocks(Document doc, IReadOnlyList<ObjectId> selectedIds)
    {
        return CadDatabaseScope.Write(
            doc,
            (_, transaction) =>
            {
                var summary = new AttributeRefreshSummary();
                var updatedDynamicDefinitions = new HashSet<ObjectId>();

                foreach (var objectId in selectedIds.Distinct())
                    RefreshBlockReference(transaction, objectId, updatedDynamicDefinitions, summary);

                return summary;
            },
            requireDocumentLock: true);
    }

    private static void RefreshBlockReference(
        Transaction transaction,
        ObjectId objectId,
        HashSet<ObjectId> updatedDynamicDefinitions,
        AttributeRefreshSummary summary)
    {
        if (objectId.IsInvalid())
        {
            summary.SkippedBlocks++;
            return;
        }

        if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, objectId, OpenMode.ForWrite, out var blockReference) ||
            blockReference == null)
        {
            summary.SkippedBlocks++;
            return;
        }

        if (CadDatabaseScope.IsOnLockedLayer(transaction, blockReference))
        {
            summary.LockedLayerBlocks++;
            return;
        }

        var definitionId = ResolveAttributeDefinitionId(blockReference);
        if (definitionId.IsInvalid())
        {
            summary.SkippedBlocks++;
            return;
        }

        UpdateAnonymousBlocksIfNeeded(transaction, blockReference, definitionId, updatedDynamicDefinitions);

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, definitionId, OpenMode.ForRead, out var blockDefinition) ||
            blockDefinition == null)
        {
            summary.SkippedBlocks++;
            return;
        }

        var attributeDefinitions = ReadAttributeDefinitions(transaction, blockDefinition);
        if (attributeDefinitions.Count == 0)
        {
            summary.NoAttributeDefinitionBlocks++;
            return;
        }

        var existingAttributes = ReadExistingAttributes(transaction, blockReference);
        var changed = false;

        foreach (var attributeDefinition in attributeDefinitions)
        {
            var tag = (attributeDefinition.Tag ?? "").Trim();
            if (tag.Length == 0)
                continue;

            if (existingAttributes.TryGetValue(tag, out var attributeReference))
            {
                RefreshExistingAttribute(blockReference, attributeReference, attributeDefinition);
                summary.UpdatedAttributes++;
                changed = true;
                continue;
            }

            CreateAttributeReference(transaction, blockReference, attributeDefinition);
            summary.CreatedAttributes++;
            changed = true;
        }

        if (!changed)
            return;

        blockReference.RecordGraphicsModified(true);
        summary.RefreshedBlocks++;
    }

    private static ObjectId ResolveAttributeDefinitionId(BlockReference blockReference)
    {
        return blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull
            ? blockReference.DynamicBlockTableRecord
            : blockReference.BlockTableRecord;
    }

    private static void UpdateAnonymousBlocksIfNeeded(
        Transaction transaction,
        BlockReference blockReference,
        ObjectId definitionId,
        HashSet<ObjectId> updatedDynamicDefinitions)
    {
        if (!blockReference.IsDynamicBlock || definitionId.IsInvalid() || updatedDynamicDefinitions.Contains(definitionId))
            return;

        try
        {
            if (CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, definitionId, OpenMode.ForWrite, out var definition) &&
                definition != null)
            {
                definition.UpdateAnonymousBlocks();
                updatedDynamicDefinitions.Add(definitionId);
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 更新动态块匿名块失败", ex);
        }
    }

    private static List<AttributeDefinition> ReadAttributeDefinitions(
        Transaction transaction,
        BlockTableRecord blockDefinition)
    {
        var definitions = new List<AttributeDefinition>();

        foreach (ObjectId entityId in blockDefinition)
        {
            if (entityId.IsInvalid())
                continue;

            if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not AttributeDefinition attributeDefinition)
                continue;

            if (attributeDefinition.Constant || string.IsNullOrWhiteSpace(attributeDefinition.Tag))
                continue;

            definitions.Add(attributeDefinition);
        }

        return definitions;
    }

    private static Dictionary<string, AttributeReference> ReadExistingAttributes(
        Transaction transaction,
        BlockReference blockReference)
    {
        var attributes = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);

        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (attributeId.IsInvalid())
                continue;

            if (transaction.GetObject(attributeId, OpenMode.ForWrite, false) is not AttributeReference attributeReference)
                continue;

            var tag = (attributeReference.Tag ?? "").Trim();
            if (tag.Length == 0 || attributes.ContainsKey(tag))
                continue;

            attributes[tag] = attributeReference;
        }

        return attributes;
    }

    private static void RefreshExistingAttribute(
        BlockReference blockReference,
        AttributeReference attributeReference,
        AttributeDefinition attributeDefinition)
    {
        var value = ReadAttributeValue(attributeReference);

        ApplyAttributeDefinitionGeometry(blockReference, attributeReference, attributeDefinition);
        ApplyAttributeValue(attributeReference, value);
        attributeReference.RecordGraphicsModified(true);
    }

    private static void CreateAttributeReference(
        Transaction transaction,
        BlockReference blockReference,
        AttributeDefinition attributeDefinition)
    {
        var attributeReference = new AttributeReference();
        attributeReference.SetDatabaseDefaults();
        ApplyAttributeDefinitionGeometry(blockReference, attributeReference, attributeDefinition);
        ApplyAttributeValue(attributeReference, attributeDefinition.TextString ?? "");

        blockReference.AttributeCollection.AppendAttribute(attributeReference);
        transaction.AddNewlyCreatedDBObject(attributeReference, true);
    }

    private static void ApplyAttributeDefinitionGeometry(
        BlockReference blockReference,
        AttributeReference attributeReference,
        AttributeDefinition attributeDefinition)
    {
        var transform = blockReference.BlockTransform;
        attributeReference.SetAttributeFromBlock(attributeDefinition, transform);
        attributeReference.Position = attributeDefinition.Position.TransformBy(transform);

        if (UsesAlignmentPoint(attributeDefinition.Justify))
            attributeReference.AlignmentPoint = attributeDefinition.AlignmentPoint.TransformBy(transform);
    }

    private static string ReadAttributeValue(AttributeReference attributeReference)
    {
        if (!attributeReference.IsMTextAttribute)
            return attributeReference.TextString ?? "";

        var mText = attributeReference.MTextAttribute;
        return mText?.Text ?? attributeReference.TextString ?? "";
    }

    private static void ApplyAttributeValue(AttributeReference attributeReference, string value)
    {
        attributeReference.TextString = value;
        if (!attributeReference.IsMTextAttribute)
            return;

        var mText = attributeReference.MTextAttribute;
        if (mText == null)
            return;

        mText.Contents = value;
        attributeReference.MTextAttribute = mText;
    }

    private static bool UsesAlignmentPoint(AttachmentPoint attachmentPoint)
    {
        return attachmentPoint != AttachmentPoint.BaseLeft;
    }

    private static string BuildMessage(AttributeRefreshSummary summary)
    {
        var message = $"\n{CommandName}：已刷新 {summary.RefreshedBlocks} 个块。";

        if (summary.CreatedAttributes > 0)
            message += $" 已新增属性文字 {summary.CreatedAttributes} 项。";
        if (summary.UpdatedAttributes > 0)
            message += $" 已同步已有属性 {summary.UpdatedAttributes} 项。";
        if (summary.NoAttributeDefinitionBlocks > 0)
            message += $" {summary.NoAttributeDefinitionBlocks} 个块没有增强属性定义。";
        if (summary.LockedLayerBlocks > 0)
            message += $" {summary.LockedLayerBlocks} 个块在锁定图层，已跳过。";
        if (summary.SkippedBlocks > 0)
            message += $" 已跳过 {summary.SkippedBlocks} 个无效对象。";

        return message;
    }

    private sealed class AttributeRefreshSummary
    {
        internal int RefreshedBlocks { get; set; }
        internal int CreatedAttributes { get; set; }
        internal int UpdatedAttributes { get; set; }
        internal int NoAttributeDefinitionBlocks { get; set; }
        internal int LockedLayerBlocks { get; set; }
        internal int SkippedBlocks { get; set; }
    }
}
