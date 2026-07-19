using System.Text;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsBbbPlugin;

internal enum BbbDeviceBlockAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

internal sealed class BbbDeviceBlockCreateSettings
{
    internal string BlockName { get; set; } = "";
    internal BbbDeviceBlockAnchor Anchor { get; set; } = BbbDeviceBlockAnchor.Center;
}

internal sealed class BbbDeviceBlockCreateResult
{
    internal string BlockName { get; set; } = "";
    internal int SourceEntityCount { get; set; }
    internal int ClonedEntityCount { get; set; }
    internal bool ReplacedExisting { get; set; }
    internal bool RenamedBlock { get; set; }
    internal int AffectedReferenceCount { get; set; }
}

internal sealed class BbbSelectedDeviceBlockInfo
{
    internal ObjectId BlockDefinitionId { get; set; } = ObjectId.Null;
    internal string CurrentName { get; set; } = "";
}

internal static class BbbDeviceBlockCreateService
{
    private const string CommandName = BbbPluginCommandIds.DeviceBlockCreate;
    private static readonly char[] s_invalidBlockNameChars = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;

        try
        {
            var selectedIds = SelectTargetIds(editor);
            if (selectedIds == null || selectedIds.Count == 0)
                return;

            var settings = new BbbDeviceBlockCreateSettings
            {
                BlockName = TryGetSelectedBlockDisplayName(doc.Database, selectedIds)
            };
            while (true)
            {
                var window = new BbbDeviceBlockCreateWindow(settings);
                var dialogResult = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                    AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                    window,
                    false);

                settings = window.SavedSettings ?? settings;
                if (window.RequestPickText)
                {
                    var pickedText = PickBlockNameText(doc);
                    if (!string.IsNullOrWhiteSpace(pickedText))
                        settings.BlockName = pickedText!;

                    continue;
                }

                if (dialogResult != true || window.SavedSettings == null)
                {
                    editor.WriteMessage($"\n{CommandName}：{UIMessages.Common.Cancelled}");
                    return;
                }

                if (!TryNormalizeBlockNameInput(settings.BlockName, out var normalizedName, out var error))
                    throw new InvalidOperationException(error);

                settings.BlockName = normalizedName;
                var selectedBlockInfo = ResolveSelectedDeviceBlockInfo(doc.Database, selectedIds);
                var existingBlockId = FindBlockDefinitionId(doc.Database, normalizedName);
                var replaceExisting = false;
                if (!existingBlockId.IsNull &&
                    (selectedBlockInfo == null || existingBlockId != selectedBlockInfo.BlockDefinitionId))
                {
                    var confirm = MessageBox.Show(
                        $"当前图纸已存在名为“{normalizedName}”的设备块。\n\n是否删除原先图块并替换？\n选择“否”可返回修改名称。",
                        "发现同名设备块",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning,
                        MessageBoxResult.No);
                    if (confirm != MessageBoxResult.Yes)
                        continue;

                    replaceExisting = true;
                }

                var result = selectedBlockInfo == null
                    ? CreateDeviceBlock(doc, selectedIds, settings, replaceExisting)
                    : RenameDeviceBlock(doc, selectedBlockInfo, normalizedName, replaceExisting);
                if (result.RenamedBlock)
                {
                    var actionText = result.ReplacedExisting ? "已替换并修改设备块名称" : "已修改设备块名称";
                    editor.WriteMessage($"\n{CommandName}：{actionText}为“{result.BlockName}”，影响 {result.AffectedReferenceCount} 个引用。");
                }
                else
                {
                    var actionText = result.ReplacedExisting ? "已替换设备块" : "已创建设备块";
                    editor.WriteMessage($"\n{CommandName}：{actionText}“{result.BlockName}”，包含 {result.ClonedEntityCount} 个对象。");
                }

                return;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        {
            editor.WriteMessage($"\n{CommandName}：{UIMessages.Common.Cancelled}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 创建设备块失败", ex);
            editor.WriteMessage($"\n{CommandName}：创建设备块失败：{ex.Message}");
        }
    }

    internal static bool TryNormalizeBlockNameInput(string? blockName, out string normalizedName, out string error)
    {
        normalizedName = NormalizeBlockName(blockName);
        error = "";

        if (normalizedName.Length == 0)
        {
            error = "设备块名称不能为空。";
            return false;
        }

        return true;
    }

    private static IReadOnlyList<ObjectId>? SelectTargetIds(Editor editor)
    {
        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            return implied.Value.GetObjectIds().Distinct().ToList();

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\n请选择要做成设备块的对象："
        };

        var picked = editor.GetSelection(options);
        if (picked.Status == PromptStatus.Cancel)
        {
            editor.WriteMessage($"\n{CommandName}：{UIMessages.Common.Cancelled}");
            return null;
        }

        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            editor.WriteMessage($"\n{CommandName}：未选择任何对象。");
            return null;
        }

        return picked.Value.GetObjectIds().Distinct().ToList();
    }

    private static string? PickBlockNameText(Document doc)
    {
        var editor = doc.Editor;
        var options = new PromptNestedEntityOptions($"\n{CommandName}：选择作为设备块名称的文字（可选块内文字）：");

        var picked = editor.GetNestedEntity(options);
        if (picked.Status == PromptStatus.Cancel)
        {
            editor.WriteMessage($"\n{CommandName}：已取消选文字。");
            return null;
        }

        if (picked.Status != PromptStatus.OK || picked.ObjectId.IsNull)
        {
            editor.WriteMessage($"\n{CommandName}：未选择文字。");
            return null;
        }

        var text = CadDatabaseScope.Read(
            doc.Database,
            (_, transaction) => ReadTextObjectText(transaction, picked.ObjectId));

        if (string.IsNullOrWhiteSpace(text))
            editor.WriteMessage($"\n{CommandName}：所选对象没有可读取的文字。");

        return text;
    }

    private static string ReadTextObjectText(Transaction transaction, ObjectId objectId)
    {
        if (objectId.IsInvalid())
            return "";

        if (!CadDatabaseScope.TryOpenAs<DBObject>(transaction, objectId, OpenMode.ForRead, out var dbObject) ||
            dbObject == null)
        {
            return "";
        }

        switch (dbObject)
        {
            case AttributeReference attributeReference:
                return ReadAttributeText(attributeReference);
            case DBText dbText:
                return dbText.TextString ?? "";
            case MText mText:
                return mText.Text ?? "";
            default:
                return "";
        }
    }

    private static string ReadAttributeText(AttributeReference attributeReference)
    {
        if (!attributeReference.IsMTextAttribute)
            return attributeReference.TextString ?? "";

        var mText = attributeReference.MTextAttribute;
        return mText?.Text ?? attributeReference.TextString ?? "";
    }

    private static string TryGetSelectedBlockDisplayName(Database database, IReadOnlyList<ObjectId> selectedIds)
    {
        return CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                foreach (var objectId in selectedIds.Distinct())
                {
                    if (objectId.IsInvalid())
                        continue;

                    if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, objectId, OpenMode.ForRead, out var blockReference) ||
                        blockReference == null)
                    {
                        continue;
                    }

                    var blockName = GetDisplayBlockName(blockReference, transaction);
                    if (!string.IsNullOrWhiteSpace(blockName) &&
                        !blockName.StartsWith("*", StringComparison.Ordinal) &&
                        !string.Equals(blockName, "<匿名块>", StringComparison.OrdinalIgnoreCase))
                    {
                        return blockName;
                    }
                }

                return "";
            });
    }

    private static string GetDisplayBlockName(BlockReference blockReference, Transaction transaction)
    {
        try
        {
            if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull)
            {
                var dynamicName = TryGetBlockName(blockReference.DynamicBlockTableRecord, transaction);
                if (!string.IsNullOrWhiteSpace(dynamicName) && !dynamicName.StartsWith("*", StringComparison.Ordinal))
                    return dynamicName;
            }
        }
        catch
        {
        }

        try
        {
            if (!blockReference.AnonymousBlockTableRecord.IsNull)
            {
                var anonymousName = TryGetBlockName(blockReference.AnonymousBlockTableRecord, transaction);
                if (!string.IsNullOrWhiteSpace(anonymousName) && !anonymousName.StartsWith("*", StringComparison.Ordinal))
                    return anonymousName;
            }
        }
        catch
        {
        }

        var directName = TryGetBlockName(blockReference.BlockTableRecord, transaction);
        return string.IsNullOrWhiteSpace(directName) ? "" : directName;
    }

    private static string TryGetBlockName(ObjectId blockTableRecordId, Transaction transaction)
    {
        if (blockTableRecordId.IsInvalid())
            return "";

        return CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, blockTableRecordId, OpenMode.ForRead, out var blockTableRecord) &&
               blockTableRecord != null
            ? (blockTableRecord.Name ?? "").Trim()
            : "";
    }

    private static ObjectId FindBlockDefinitionId(Database database, string blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName))
            return ObjectId.Null;

        return CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                return blockTable.Has(blockName) ? blockTable[blockName] : ObjectId.Null;
            });
    }

    private static BbbSelectedDeviceBlockInfo? ResolveSelectedDeviceBlockInfo(
        Database database,
        IReadOnlyList<ObjectId> selectedIds)
    {
        return CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                var distinctIds = selectedIds.Distinct().ToList();
                BbbSelectedDeviceBlockInfo? blockInfo = null;
                var blockReferenceCount = 0;
                var nonBlockEntityCount = 0;

                foreach (var objectId in distinctIds)
                {
                    if (objectId.IsInvalid())
                        continue;

                    if (!CadDatabaseScope.TryOpenAs<Entity>(transaction, objectId, OpenMode.ForRead, out var entity) ||
                        entity == null)
                    {
                        continue;
                    }

                    if (entity is not BlockReference blockReference)
                    {
                        nonBlockEntityCount++;
                        continue;
                    }

                    blockReferenceCount++;
                    var blockDefinitionId = ResolveRenameBlockDefinitionId(blockReference, transaction);
                    if (blockDefinitionId.IsNull)
                        throw new InvalidOperationException("所选图块无法直接修改名称。");

                    var blockName = TryGetBlockName(blockDefinitionId, transaction);
                    if (blockName.Length == 0 || blockName.StartsWith("*", StringComparison.Ordinal))
                        throw new InvalidOperationException("匿名块无法直接修改名称。");

                    blockInfo = new BbbSelectedDeviceBlockInfo
                    {
                        BlockDefinitionId = blockDefinitionId,
                        CurrentName = blockName
                    };
                }

                if (blockReferenceCount == 0)
                    return null;

                if (blockReferenceCount != 1 || nonBlockEntityCount > 0)
                    throw new InvalidOperationException("修改设备块名称时，请只选择一个已有图块。");

                return blockInfo;
            });
    }

    private static ObjectId ResolveRenameBlockDefinitionId(BlockReference blockReference, Transaction transaction)
    {
        try
        {
            if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull)
            {
                var dynamicName = TryGetBlockName(blockReference.DynamicBlockTableRecord, transaction);
                if (!string.IsNullOrWhiteSpace(dynamicName) && !dynamicName.StartsWith("*", StringComparison.Ordinal))
                    return blockReference.DynamicBlockTableRecord;
            }
        }
        catch
        {
        }

        return blockReference.BlockTableRecord;
    }

    private static BbbDeviceBlockCreateResult RenameDeviceBlock(
        Document doc,
        BbbSelectedDeviceBlockInfo selectedBlockInfo,
        string newName,
        bool replaceExisting)
    {
        return CadDatabaseScope.Write(
            doc,
            (database, transaction) =>
            {
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                var sourceDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(
                    transaction,
                    selectedBlockInfo.BlockDefinitionId,
                    OpenMode.ForWrite);
                ValidateRenamableBlockDefinition(sourceDefinition, "所选图块");

                var targetBlockId = blockTable.Has(newName) ? blockTable[newName] : ObjectId.Null;
                var replacedExisting = false;
                if (!targetBlockId.IsNull && targetBlockId != selectedBlockInfo.BlockDefinitionId)
                {
                    if (!replaceExisting)
                        throw new InvalidOperationException($"当前图纸已存在名为“{newName}”的设备块。");

                    var targetDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, targetBlockId, OpenMode.ForWrite);
                    ValidateRenamableBlockDefinition(targetDefinition, "同名图块");
                    targetDefinition.Name = CreateTemporaryBlockName(blockTable, newName);
                    DeleteBlockDefinitionAndReferences(targetDefinition, transaction);
                    replacedExisting = true;
                }

                if (!string.Equals(sourceDefinition.Name, newName, StringComparison.OrdinalIgnoreCase))
                    sourceDefinition.Name = newName;

                return new BbbDeviceBlockCreateResult
                {
                    BlockName = newName,
                    SourceEntityCount = 1,
                    RenamedBlock = true,
                    ReplacedExisting = replacedExisting,
                    AffectedReferenceCount = CountBlockReferences(sourceDefinition)
                };
            },
            requireDocumentLock: true);
    }

    private static BbbDeviceBlockCreateResult CreateDeviceBlock(
        Document doc,
        IReadOnlyList<ObjectId> selectedIds,
        BbbDeviceBlockCreateSettings settings,
        bool replaceExisting)
    {
        if (!TryNormalizeBlockNameInput(settings.BlockName, out var normalizedName, out var error))
            throw new InvalidOperationException(error);

        return CadDatabaseScope.Write(
            doc,
            (database, transaction) =>
            {
                var entities = OpenTargetEntities(transaction, selectedIds);
                if (entities.Count == 0)
                    throw new InvalidOperationException("所选对象中没有可制块的图形。");

                var lockedLayerCount = entities.Count(entity => CadDatabaseScope.IsOnLockedLayer(transaction, entity));
                if (lockedLayerCount > 0)
                    throw new InvalidOperationException($"所选对象包含 {lockedLayerCount} 个锁定图层对象，请解锁后再执行。");

                if (!TryGetCombinedExtents(entities, out var extents))
                    throw new InvalidOperationException("未能读取所选对象范围，无法计算基点。");

                var basePoint = ResolveAnchorPoint(extents, settings.Anchor);
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                var blockName = normalizedName;
                var existingBlockId = blockTable.Has(blockName) ? blockTable[blockName] : ObjectId.Null;
                var blockId = existingBlockId.IsNull
                    ? CreateBlockDefinition(blockTable, transaction, blockName)
                    : existingBlockId;
                var blockDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, blockId, OpenMode.ForWrite);
                if (!existingBlockId.IsNull)
                {
                    if (!replaceExisting)
                        throw new InvalidOperationException($"当前图纸已存在名为“{blockName}”的设备块。");

                    ClearBlockDefinitionContents(blockDefinition, transaction);
                }

                var clonedCount = CloneEntitiesIntoBlock(entities, blockDefinition, transaction, basePoint);
                if (clonedCount == 0)
                    throw new InvalidOperationException("未能复制所选对象到设备块定义。");

                InsertBlockReference(database, transaction, blockId, basePoint);

                foreach (var entity in entities)
                    entity.Erase();

                return new BbbDeviceBlockCreateResult
                {
                    BlockName = blockName,
                    SourceEntityCount = entities.Count,
                    ClonedEntityCount = clonedCount,
                    ReplacedExisting = !existingBlockId.IsNull
                };
            },
            requireDocumentLock: true);
    }

    private static List<Entity> OpenTargetEntities(Transaction transaction, IReadOnlyList<ObjectId> selectedIds)
    {
        var entities = new List<Entity>();
        foreach (var objectId in selectedIds.Distinct())
        {
            if (objectId.IsInvalid())
                continue;

            if (CadDatabaseScope.TryOpenAs<Entity>(transaction, objectId, OpenMode.ForWrite, out var entity) &&
                entity != null)
            {
                entities.Add(entity);
            }
        }

        return entities;
    }

    private static ObjectId CreateBlockDefinition(BlockTable blockTable, Transaction transaction, string blockName)
    {
        if (!blockTable.IsWriteEnabled)
            blockTable.UpgradeOpen();

        using var blockDefinition = new BlockTableRecord
        {
            Name = blockName,
            Origin = Point3d.Origin
        };

        var blockId = blockTable.Add(blockDefinition);
        transaction.AddNewlyCreatedDBObject(blockDefinition, true);
        return blockId;
    }

    private static void ClearBlockDefinitionContents(BlockTableRecord blockDefinition, Transaction transaction)
    {
        ValidateRenamableBlockDefinition(blockDefinition, "同名图块");

        var childIds = blockDefinition.Cast<ObjectId>().ToList();
        foreach (var childId in childIds)
        {
            if (childId.IsInvalid())
                continue;

            if (CadDatabaseScope.TryOpenAs<DBObject>(transaction, childId, OpenMode.ForWrite, out var child) &&
                child != null &&
                !child.IsErased)
            {
                child.Erase();
            }
        }
    }

    private static void DeleteBlockDefinitionAndReferences(BlockTableRecord blockDefinition, Transaction transaction)
    {
        ValidateRenamableBlockDefinition(blockDefinition, "同名图块");

        var referenceIds = blockDefinition.GetBlockReferenceIds(false, true).Cast<ObjectId>().ToList();
        foreach (var referenceId in referenceIds)
        {
            if (referenceId.IsInvalid())
                continue;

            if (CadDatabaseScope.TryOpenAs<Entity>(transaction, referenceId, OpenMode.ForWrite, out var reference) &&
                reference != null &&
                !reference.IsErased)
            {
                reference.Erase();
            }
        }

        blockDefinition.Erase();
    }

    private static void ValidateRenamableBlockDefinition(BlockTableRecord blockDefinition, string label)
    {
        if (blockDefinition.IsLayout)
            throw new InvalidOperationException($"{label}是布局块，无法替换。");

        if (blockDefinition.IsFromExternalReference || blockDefinition.IsFromOverlayReference)
            throw new InvalidOperationException($"{label}来自外部参照，无法替换。");

        if ((blockDefinition.Name ?? "").StartsWith("*", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}是匿名块，无法直接修改名称。");
    }

    private static int CountBlockReferences(BlockTableRecord blockDefinition)
    {
        try
        {
            return blockDefinition.GetBlockReferenceIds(false, true).Count;
        }
        catch
        {
            return 0;
        }
    }

    private static string CreateTemporaryBlockName(BlockTable blockTable, string baseName)
    {
        var seed = $"__C_TOOL_REPLACED_{baseName}_{Guid.NewGuid():N}";
        var candidate = seed;
        var suffix = 1;
        while (blockTable.Has(candidate))
        {
            candidate = $"{seed}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static int CloneEntitiesIntoBlock(
        IEnumerable<Entity> entities,
        BlockTableRecord blockDefinition,
        Transaction transaction,
        Point3d basePoint)
    {
        var clonedCount = 0;
        var toLocal = Matrix3d.Displacement(Point3d.Origin - basePoint);
        foreach (var entity in entities)
        {
            if (entity.Clone() is not Entity clone)
                continue;

            clone.TransformBy(toLocal);
            blockDefinition.AppendEntity(clone);
            transaction.AddNewlyCreatedDBObject(clone, true);
            clonedCount++;
        }

        return clonedCount;
    }

    private static void InsertBlockReference(
        Database database,
        Transaction transaction,
        ObjectId blockId,
        Point3d basePoint)
    {
        var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(database, transaction);
        using var blockReference = new BlockReference(basePoint, blockId);
        blockReference.SetDatabaseDefaults();
        currentSpace.AppendEntity(blockReference);
        transaction.AddNewlyCreatedDBObject(blockReference, true);
    }

    private static bool TryGetCombinedExtents(IReadOnlyList<Entity> entities, out Extents3d extents)
    {
        extents = default;
        var hasExtents = false;

        foreach (var entity in entities)
        {
            try
            {
                var entityExtents = entity.GeometricExtents;
                if (!hasExtents)
                {
                    extents = entityExtents;
                    hasExtents = true;
                    continue;
                }

                extents.AddExtents(entityExtents);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return hasExtents;
    }

    private static Point3d ResolveAnchorPoint(Extents3d extents, BbbDeviceBlockAnchor anchor)
    {
        var min = extents.MinPoint;
        var max = extents.MaxPoint;
        var centerX = (min.X + max.X) * 0.5;
        var centerY = (min.Y + max.Y) * 0.5;

        var x = anchor switch
        {
            BbbDeviceBlockAnchor.TopLeft or BbbDeviceBlockAnchor.MiddleLeft or BbbDeviceBlockAnchor.BottomLeft => min.X,
            BbbDeviceBlockAnchor.TopRight or BbbDeviceBlockAnchor.MiddleRight or BbbDeviceBlockAnchor.BottomRight => max.X,
            _ => centerX
        };

        var y = anchor switch
        {
            BbbDeviceBlockAnchor.TopLeft or BbbDeviceBlockAnchor.TopCenter or BbbDeviceBlockAnchor.TopRight => max.Y,
            BbbDeviceBlockAnchor.BottomLeft or BbbDeviceBlockAnchor.BottomCenter or BbbDeviceBlockAnchor.BottomRight => min.Y,
            _ => centerY
        };

        return new Point3d(x, y, min.Z);
    }

    private static string NormalizeBlockName(string? blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName))
            return "";

        var builder = new StringBuilder(blockName!.Length);
        foreach (var ch in blockName.Trim())
        {
            if (char.IsControl(ch))
                continue;

            builder.Append(s_invalidBlockNameChars.Contains(ch) ? '_' : ch);
        }

        var normalized = builder.ToString().Trim();
        while (normalized.IndexOf("__", StringComparison.Ordinal) >= 0)
            normalized = normalized.Replace("__", "_");

        return normalized.Trim('_', ' ');
    }
}
