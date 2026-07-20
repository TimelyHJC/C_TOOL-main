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

internal enum BbbDeviceBlockNameMode
{
    CreateNewBlock,
    RenameSingleBlock,
    RenameAllBlocks
}

internal sealed class BbbDeviceBlockCreateSettings
{
    internal string BlockName { get; set; } = "";
    internal BbbDeviceBlockAnchor Anchor { get; set; } = BbbDeviceBlockAnchor.Center;
    internal BbbDeviceBlockNameMode NameMode { get; set; } = BbbDeviceBlockNameMode.CreateNewBlock;
}

internal sealed class BbbDeviceBlockCreateResult
{
    internal string BlockName { get; set; } = "";
    internal int SourceEntityCount { get; set; }
    internal int ClonedEntityCount { get; set; }
    internal bool ReplacedExisting { get; set; }
    internal bool RenamedBlock { get; set; }
    internal int AffectedBlockReferenceCount { get; set; }
    internal BbbDeviceBlockNameMode NameMode { get; set; } = BbbDeviceBlockNameMode.CreateNewBlock;
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
                if (!TryConfirmReplaceExistingBlock(doc.Database, selectedIds, settings, out var replaceExisting))
                    continue;

                var result = ExecuteDeviceBlockNameCommand(
                    doc,
                    selectedIds,
                    settings,
                    editor.CurrentUserCoordinateSystem,
                    replaceExisting);
                editor.WriteMessage(BuildResultMessage(result));
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

    private static bool TryConfirmReplaceExistingBlock(
        Database database,
        IReadOnlyList<ObjectId> selectedIds,
        BbbDeviceBlockCreateSettings settings,
        out bool replaceExisting)
    {
        replaceExisting = false;
        var existingBlockId = FindBlockDefinitionId(database, settings.BlockName);
        if (existingBlockId.IsNull)
            return true;

        var currentDefinitionId = ResolveCurrentDefinitionIdForMode(database, selectedIds, settings.NameMode);
        if (!currentDefinitionId.IsNull && existingBlockId == currentDefinitionId)
            return true;

        var confirm = MessageBox.Show(
            $"当前图纸已存在名为“{settings.BlockName}”的设备块。\n\n是否删除原先图块并替换？\n选择“否”可返回修改名称。",
            "发现同名设备块",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return false;

        replaceExisting = true;
        return true;
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

    private static ObjectId ResolveCurrentDefinitionIdForMode(
        Database database,
        IReadOnlyList<ObjectId> selectedIds,
        BbbDeviceBlockNameMode mode)
    {
        if (mode == BbbDeviceBlockNameMode.CreateNewBlock)
            return ObjectId.Null;

        return CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                var blockReference = OpenSingleBlockReference(transaction, selectedIds, OpenMode.ForRead);
                return mode == BbbDeviceBlockNameMode.RenameAllBlocks
                    ? ResolveUserVisibleDefinitionId(blockReference)
                    : blockReference.BlockTableRecord;
            });
    }

    private static BbbDeviceBlockCreateResult ExecuteDeviceBlockNameCommand(
        Document doc,
        IReadOnlyList<ObjectId> selectedIds,
        BbbDeviceBlockCreateSettings settings,
        Matrix3d ucsToWorld,
        bool replaceExisting)
    {
        return settings.NameMode switch
        {
            BbbDeviceBlockNameMode.RenameSingleBlock => RenameSelectedBlock(doc, selectedIds, settings, replaceExisting),
            BbbDeviceBlockNameMode.RenameAllBlocks => RenameAllBlockReferences(doc, selectedIds, settings, replaceExisting),
            _ => CreateDeviceBlock(doc, selectedIds, settings, ucsToWorld, replaceExisting)
        };
    }

    private static BbbDeviceBlockCreateResult CreateDeviceBlock(
        Document doc,
        IReadOnlyList<ObjectId> selectedIds,
        BbbDeviceBlockCreateSettings settings,
        Matrix3d ucsToWorld,
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

                var worldToUcs = ucsToWorld.Inverse();
                if (!TryGetCombinedExtents(entities, worldToUcs, out var extents))
                    throw new InvalidOperationException("未能读取所选对象范围，无法计算基点。");

                var basePoint = ResolveAnchorPoint(extents, settings.Anchor).TransformBy(ucsToWorld);
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                var existingBlockId = blockTable.Has(normalizedName) ? blockTable[normalizedName] : ObjectId.Null;
                var replacedExisting = !existingBlockId.IsNull;
                var blockId = existingBlockId.IsNull
                    ? CreateBlockDefinition(blockTable, transaction, normalizedName, basePoint)
                    : existingBlockId;
                var blockDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, blockId, OpenMode.ForWrite);
                if (replacedExisting)
                {
                    if (!replaceExisting)
                        throw new InvalidOperationException($"当前图纸已存在名为“{normalizedName}”的设备块。");

                    ClearBlockDefinitionContents(blockDefinition, transaction);
                }

                blockDefinition.Origin = basePoint;

                var clonedCount = CloneEntitiesIntoBlock(entities, blockDefinition, transaction);
                if (clonedCount == 0)
                    throw new InvalidOperationException("未能复制所选对象到设备块定义。");

                InsertBlockReference(database, transaction, blockId, basePoint);

                foreach (var entity in entities)
                    entity.Erase();

                return new BbbDeviceBlockCreateResult
                {
                    BlockName = normalizedName,
                    SourceEntityCount = entities.Count,
                    ClonedEntityCount = clonedCount,
                    ReplacedExisting = replacedExisting,
                    AffectedBlockReferenceCount = replacedExisting ? Math.Max(1, CountBlockReferences(blockDefinition)) : 1,
                    NameMode = BbbDeviceBlockNameMode.CreateNewBlock
                };
            },
            requireDocumentLock: true);
    }

    private static BbbDeviceBlockCreateResult RenameSelectedBlock(
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
                var blockReference = OpenSingleBlockReferenceForWrite(transaction, selectedIds);
                if (blockReference.IsDynamicBlock)
                    throw new InvalidOperationException("改单块名称暂不支持动态块，请使用“改全块名称”。");

                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                var sourceDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(
                    transaction,
                    blockReference.BlockTableRecord,
                    OpenMode.ForWrite);
                ValidateRenamableBlockDefinition(sourceDefinition, "改单块名称");

                var replacedExisting = DeleteConflictingBlockDefinitionIfNeeded(
                    blockTable,
                    transaction,
                    normalizedName,
                    sourceDefinition.ObjectId,
                    replaceExisting);

                var referenceCount = CountBlockReferences(sourceDefinition);
                var currentName = (sourceDefinition.Name ?? "").Trim();
                if (string.Equals(currentName, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    // Name is already correct; keep the selected reference as-is.
                }
                else if (referenceCount <= 1)
                {
                    sourceDefinition.Name = normalizedName;
                }
                else
                {
                    var clonedBlockId = CloneBlockDefinition(blockTable, transaction, sourceDefinition, normalizedName);
                    blockReference.BlockTableRecord = clonedBlockId;
                    blockReference.RecordGraphicsModified(true);
                }

                return new BbbDeviceBlockCreateResult
                {
                    BlockName = normalizedName,
                    ReplacedExisting = replacedExisting,
                    RenamedBlock = true,
                    AffectedBlockReferenceCount = 1,
                    NameMode = BbbDeviceBlockNameMode.RenameSingleBlock
                };
            },
            requireDocumentLock: true);
    }

    private static BbbDeviceBlockCreateResult RenameAllBlockReferences(
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
                var blockReference = OpenSingleBlockReferenceForRead(transaction, selectedIds);
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                var definitionId = ResolveUserVisibleDefinitionId(blockReference);
                var blockDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, definitionId, OpenMode.ForWrite);
                ValidateRenamableBlockDefinition(blockDefinition, "改全块名称");

                var replacedExisting = DeleteConflictingBlockDefinitionIfNeeded(
                    blockTable,
                    transaction,
                    normalizedName,
                    blockDefinition.ObjectId,
                    replaceExisting);

                var referenceCount = CountBlockReferences(blockDefinition);
                var currentName = (blockDefinition.Name ?? "").Trim();
                if (!string.Equals(currentName, normalizedName, StringComparison.OrdinalIgnoreCase))
                    blockDefinition.Name = normalizedName;

                return new BbbDeviceBlockCreateResult
                {
                    BlockName = normalizedName,
                    ReplacedExisting = replacedExisting,
                    RenamedBlock = true,
                    AffectedBlockReferenceCount = Math.Max(1, referenceCount),
                    NameMode = BbbDeviceBlockNameMode.RenameAllBlocks
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

    private static ObjectId CreateBlockDefinition(
        BlockTable blockTable,
        Transaction transaction,
        string blockName,
        Point3d basePoint)
    {
        if (!blockTable.IsWriteEnabled)
            blockTable.UpgradeOpen();

        using var blockDefinition = new BlockTableRecord
        {
            Name = blockName,
            Origin = basePoint
        };

        var blockId = blockTable.Add(blockDefinition);
        transaction.AddNewlyCreatedDBObject(blockDefinition, true);
        return blockId;
    }

    private static ObjectId CloneBlockDefinition(
        BlockTable blockTable,
        Transaction transaction,
        BlockTableRecord sourceDefinition,
        string blockName)
    {
        if (!blockTable.IsWriteEnabled)
            blockTable.UpgradeOpen();

        using var newDefinition = new BlockTableRecord
        {
            Name = blockName,
            Origin = sourceDefinition.Origin
        };

        var newBlockId = blockTable.Add(newDefinition);
        transaction.AddNewlyCreatedDBObject(newDefinition, true);

        foreach (ObjectId entityId in sourceDefinition)
        {
            if (entityId.IsInvalid())
                continue;

            if (!CadDatabaseScope.TryOpenAs<Entity>(transaction, entityId, OpenMode.ForRead, out var entity) ||
                entity == null ||
                entity.Clone() is not Entity clone)
            {
                continue;
            }

            newDefinition.AppendEntity(clone);
            transaction.AddNewlyCreatedDBObject(clone, true);
        }

        return newBlockId;
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

    private static bool DeleteConflictingBlockDefinitionIfNeeded(
        BlockTable blockTable,
        Transaction transaction,
        string blockName,
        ObjectId currentBlockId,
        bool replaceExisting)
    {
        if (!blockTable.Has(blockName))
            return false;

        var existingBlockId = blockTable[blockName];
        if (!currentBlockId.IsNull && existingBlockId == currentBlockId)
            return false;

        if (!replaceExisting)
            throw new InvalidOperationException($"图纸中已存在名为“{blockName}”的块，请换一个名称。");

        var targetDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, existingBlockId, OpenMode.ForWrite);
        ValidateRenamableBlockDefinition(targetDefinition, "同名图块");
        targetDefinition.Name = CreateTemporaryBlockName(blockTable, blockName);
        DeleteBlockDefinitionAndReferences(targetDefinition, transaction);
        return true;
    }

    private static void DeleteBlockDefinitionAndReferences(BlockTableRecord blockDefinition, Transaction transaction)
    {
        ValidateRenamableBlockDefinition(blockDefinition, "同名图块");

        var referenceIds = blockDefinition.GetBlockReferenceIds(directOnly: false, forceValidity: true).Cast<ObjectId>().ToList();
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

        if (blockDefinition.IsAnonymous || (blockDefinition.Name ?? "").StartsWith("*", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}是匿名块，无法直接修改名称。");
    }

    private static int CountBlockReferences(BlockTableRecord blockDefinition)
    {
        try
        {
            return blockDefinition.GetBlockReferenceIds(directOnly: false, forceValidity: true).Count;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return 0;
        }
        catch (InvalidOperationException)
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
        Transaction transaction)
    {
        var clonedCount = 0;
        foreach (var entity in entities)
        {
            if (entity.Clone() is not Entity clone)
                continue;

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

    private static bool TryGetCombinedExtents(
        IReadOnlyList<Entity> entities,
        Matrix3d worldToUcs,
        out Extents3d extents)
    {
        extents = default;
        var hasExtents = false;

        foreach (var entity in entities)
        {
            try
            {
                var entityExtents = TransformExtents(entity.GeometricExtents, worldToUcs);
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

    internal static Point3d ResolveAnchorPointForExtents(
        Extents3d worldExtents,
        BbbDeviceBlockAnchor anchor,
        Matrix3d ucsToWorld)
    {
        var ucsExtents = TransformExtents(worldExtents, ucsToWorld.Inverse());
        return ResolveAnchorPoint(ucsExtents, anchor).TransformBy(ucsToWorld);
    }

    internal static Extents3d TransformExtents(Extents3d source, Matrix3d transform)
    {
        var min = source.MinPoint;
        var max = source.MaxPoint;
        var points = new[]
        {
            new Point3d(min.X, min.Y, min.Z),
            new Point3d(min.X, min.Y, max.Z),
            new Point3d(min.X, max.Y, min.Z),
            new Point3d(min.X, max.Y, max.Z),
            new Point3d(max.X, min.Y, min.Z),
            new Point3d(max.X, min.Y, max.Z),
            new Point3d(max.X, max.Y, min.Z),
            new Point3d(max.X, max.Y, max.Z)
        };

        var first = points[0].TransformBy(transform);
        var transformed = new Extents3d(first, first);
        for (var i = 1; i < points.Length; i++)
            transformed.AddPoint(points[i].TransformBy(transform));

        return transformed;
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

    private static BlockReference OpenSingleBlockReferenceForRead(Transaction transaction, IReadOnlyList<ObjectId> selectedIds)
    {
        return OpenSingleBlockReference(transaction, selectedIds, OpenMode.ForRead);
    }

    private static BlockReference OpenSingleBlockReferenceForWrite(Transaction transaction, IReadOnlyList<ObjectId> selectedIds)
    {
        return OpenSingleBlockReference(transaction, selectedIds, OpenMode.ForWrite);
    }

    private static BlockReference OpenSingleBlockReference(
        Transaction transaction,
        IReadOnlyList<ObjectId> selectedIds,
        OpenMode openMode)
    {
        var objectIds = selectedIds
            .Where(x => !x.IsInvalid())
            .Distinct()
            .ToList();
        if (objectIds.Count != 1)
            throw new InvalidOperationException("修改块名称时请只选择一个块参照。");

        if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, objectIds[0], openMode, out var blockReference) ||
            blockReference == null)
        {
            throw new InvalidOperationException("修改块名称时请选择一个块参照。");
        }

        return blockReference;
    }

    private static ObjectId ResolveUserVisibleDefinitionId(BlockReference blockReference)
    {
        if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsInvalid())
            return blockReference.DynamicBlockTableRecord;

        return blockReference.BlockTableRecord;
    }

    private static string BuildResultMessage(BbbDeviceBlockCreateResult result)
    {
        return result.NameMode switch
        {
            BbbDeviceBlockNameMode.RenameSingleBlock =>
                result.ReplacedExisting
                    ? $"\n{CommandName}：已替换同名块，并将选中单块名称改为“{result.BlockName}”。"
                    : $"\n{CommandName}：已将选中单块名称改为“{result.BlockName}”。",
            BbbDeviceBlockNameMode.RenameAllBlocks =>
                result.ReplacedExisting
                    ? $"\n{CommandName}：已替换同名块，并将全块名称改为“{result.BlockName}”，影响 {result.AffectedBlockReferenceCount} 个块参照。"
                    : $"\n{CommandName}：已将全块名称改为“{result.BlockName}”，影响 {result.AffectedBlockReferenceCount} 个块参照。",
            _ =>
                result.ReplacedExisting
                    ? $"\n{CommandName}：已替换设备块“{result.BlockName}”，包含 {result.ClonedEntityCount} 个对象。"
                    : $"\n{CommandName}：已创建设备块“{result.BlockName}”，包含 {result.ClonedEntityCount} 个对象。"
        };
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
