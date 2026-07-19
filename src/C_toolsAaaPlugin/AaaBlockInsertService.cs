using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockInsertService
{
    internal static AaaInsertResult PromptAndInsert(Document doc, AaaBlockCatalogItem item)
    {
        if (doc == null)
            return AaaInsertResult.Failure(UIMessages.BlockLibrary.NoActiveDocument);
        if (item == null)
            return AaaInsertResult.Failure("当前没有可插入的图块/组合。");
        if (!item.ExistsOnDisk)
            return AaaInsertResult.Failure($"资源不存在：{item.FullPath}");

        return item.IsCombo
            ? PromptAndInsertCombo(doc, item)
            : PromptAndInsertDwg(doc, item);
    }

    private static AaaInsertResult PromptAndInsertDwg(Document doc, AaaBlockCatalogItem item)
    {
        var prompt = new PromptPointOptions($"\n{UIMessages.Prefix_V_AAA}指定图块「{item.DisplayName}」的插入点：");
        var pointResult = doc.Editor.GetPoint(prompt);
        if (pointResult.Status != PromptStatus.OK)
            return AaaInsertResult.CancelledResult(UIMessages.BlockLibrary.CancelInsertBlock);

        return InsertDwgAtPoint(doc, item, pointResult.Value);
    }

    private static AaaInsertResult PromptAndInsertCombo(Document doc, AaaBlockCatalogItem item)
    {
        var prompt = new PromptPointOptions($"\n{UIMessages.Prefix_V_AAA}指定组合「{item.DisplayName}」的插入点：");
        var pointResult = doc.Editor.GetPoint(prompt);
        if (pointResult.Status != PromptStatus.OK)
            return AaaInsertResult.CancelledResult(UIMessages.BlockLibrary.CancelInsertCombo);

        return InsertComboAtPoint(doc, item, pointResult.Value);
    }

    private static AaaInsertResult InsertDwgAtPoint(Document doc, AaaBlockCatalogItem item, Point3d insertionPoint)
    {
        try
        {
            using var documentLock = doc.LockDocument();
            var db = doc.Database;
            var insertionPlan = BuildInsertionPlan(db, item.DisplayName, item.FullPath, item.LastWriteTimeUtc);
            CadDatabaseScope.Write(
                db,
                (database, transaction) =>
                {
                    var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(database, transaction);
                    if (!string.IsNullOrWhiteSpace(insertionPlan.LayerName))
                        EnsureLayerExists(database, transaction, insertionPlan.LayerName);

                    using var blockReference = new BlockReference(insertionPoint, insertionPlan.BlockId);
                    blockReference.Rotation = insertionPlan.Rotation;
                    blockReference.ScaleFactors = insertionPlan.Scale;
                    if (!string.IsNullOrWhiteSpace(insertionPlan.LayerName))
                        blockReference.Layer = insertionPlan.LayerName;

                    currentSpace.AppendEntity(blockReference);
                    transaction.AddNewlyCreatedDBObject(blockReference, true);
                    ApplyDynamicPropertySnapshots(blockReference, insertionPlan.DynamicPropertySnapshots);
                    AppendAttributeReferences(blockReference, transaction, insertionPlan.AttributeSnapshots);
                });

            var message = $"已插入图块：{item.DisplayName}";
            doc.Editor.WriteMessage($"\nV_AAA：{message}");
            return AaaInsertResult.Succeed(message);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取图块文件失败", ex);
            return AaaInsertResult.Failure($"读取图块文件失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 插入图块失败", ex);
            return AaaInsertResult.Failure($"插入图块失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 插入图块失败", ex);
            return AaaInsertResult.Failure($"插入图块失败：{ex.Message}");
        }
    }

    private static SingleBlockInsertionPlan BuildInsertionPlan(
        Database targetDatabase,
        string displayName,
        string sourcePath,
        DateTime lastWriteTimeUtc)
    {
        using var sourceDatabase = new Database(false, true);
        sourceDatabase.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, "");

        var sourceInsertion = TryReadSingleBlockInsertion(sourceDatabase, targetDatabase);
        if (sourceInsertion == null)
        {
            var wrappedBlockId = InsertWholeDrawingAsDefinition(sourceDatabase, targetDatabase, displayName, sourcePath, lastWriteTimeUtc);
            return new SingleBlockInsertionPlan
            {
                BlockId = wrappedBlockId,
                Rotation = 0d,
                Scale = new Scale3d(1d),
                LayerName = ""
            };
        }

        return sourceInsertion;
    }

    private static AaaInsertResult InsertComboAtPoint(Document doc, AaaBlockCatalogItem item, Point3d insertionPoint)
    {
        try
        {
            var manifest = AaaBlockComboPackageStore.Load(item.FullPath);
            if (manifest == null || manifest.Members.Count == 0)
                return AaaInsertResult.Failure("组合定义无有效成员，无法插入。");

            var libraryRootPath = ResolveLibraryRootPath(item);
            if (libraryRootPath.Length == 0 || !Directory.Exists(libraryRootPath))
                return AaaInsertResult.Failure("组合定义未关联到有效的独立图库。");

            var singleBlocks = AaaComboLibraryReferenceResolver.LoadSingleBlocks(libraryRootPath);
            var resolvedMembers = ResolveComboMembers(item, manifest, singleBlocks, libraryRootPath, out var missingDevices);
            if (resolvedMembers.Count == 0)
                return AaaInsertResult.Failure("组合定义无有效成员，无法插入。");
            if (missingDevices.Count > 0)
            {
                var details = string.Join("、", missingDevices.Take(5));
                return AaaInsertResult.Failure($"组合成员未在独立图库中找到：{details}");
            }

            using var documentLock = doc.LockDocument();
            var db = doc.Database;
            var definitionIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

            foreach (var resolvedMember in resolvedMembers)
            {
                var cacheKey = resolvedMember.SourcePath + "|" + resolvedMember.LastWriteTimeUtc.Ticks.ToString();
                if (definitionIds.ContainsKey(cacheKey))
                    continue;

                definitionIds[cacheKey] = EnsureBlockDefinition(
                    db,
                    resolvedMember.DisplayName,
                    resolvedMember.SourcePath,
                    resolvedMember.LastWriteTimeUtc);
            }

            CadDatabaseScope.Write(
                db,
                (database, transaction) =>
                {
                    var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(database, transaction);

                    foreach (var resolvedMember in resolvedMembers)
                    {
                        var cacheKey = resolvedMember.SourcePath + "|" + resolvedMember.LastWriteTimeUtc.Ticks.ToString();
                        if (!definitionIds.TryGetValue(cacheKey, out var blockId))
                            continue;

                        using var blockReference = new BlockReference(
                            new Point3d(
                                insertionPoint.X + resolvedMember.Member.OffsetX,
                                insertionPoint.Y + resolvedMember.Member.OffsetY,
                                insertionPoint.Z + resolvedMember.Member.OffsetZ),
                            blockId);

                        blockReference.Rotation = resolvedMember.Member.Rotation;
                        blockReference.ScaleFactors = CreateScale3d(resolvedMember.Member);

                        if (!string.IsNullOrWhiteSpace(resolvedMember.Member.LayerName))
                        {
                            EnsureLayerExists(database, transaction, resolvedMember.Member.LayerName);
                            blockReference.Layer = resolvedMember.Member.LayerName;
                        }

                        currentSpace.AppendEntity(blockReference);
                        transaction.AddNewlyCreatedDBObject(blockReference, true);
                        AppendAttributeReferences(blockReference, transaction);
                    }
                });

            var message = $"已插入组合：{item.DisplayName}";
            doc.Editor.WriteMessage($"\nV_AAA：{message}");
            return AaaInsertResult.Succeed(message);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取组合定义失败", ex);
            return AaaInsertResult.Failure($"读取组合定义失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 插入组合失败", ex);
            return AaaInsertResult.Failure($"插入组合失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 插入组合失败", ex);
            return AaaInsertResult.Failure($"插入组合失败：{ex.Message}");
        }
    }

    private static List<ResolvedComboMember> ResolveComboMembers(
        AaaBlockCatalogItem item,
        AaaBlockComboManifest manifest,
        IReadOnlyList<AaaBlockCatalogItem> singleBlocks,
        string libraryRootPath,
        out List<string> missingDevices)
    {
        var resolvedMembers = new List<ResolvedComboMember>();
        missingDevices = new List<string>();

        foreach (var member in manifest.Members)
        {
            var resolvedItem = AaaComboLibraryReferenceResolver.ResolveLibraryBlock(member, singleBlocks, libraryRootPath);
            if (resolvedItem != null && File.Exists(resolvedItem.FullPath))
            {
                resolvedMembers.Add(new ResolvedComboMember
                {
                    Member = member,
                    DisplayName = resolvedItem.DisplayName,
                    SourcePath = resolvedItem.FullPath,
                    LastWriteTimeUtc = resolvedItem.LastWriteTimeUtc
                });
                continue;
            }

            var legacyMemberPath = AaaBlockComboPackageStore.ResolveMemberPath(item.FullPath, member);
            if (legacyMemberPath.Length > 0 && File.Exists(legacyMemberPath))
            {
                resolvedMembers.Add(new ResolvedComboMember
                {
                    Member = member,
                    DisplayName = member.DisplayName.Length == 0
                        ? Path.GetFileNameWithoutExtension(legacyMemberPath)
                        : member.DisplayName,
                    SourcePath = legacyMemberPath,
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(legacyMemberPath)
                });
                continue;
            }

            var deviceName = (member.DeviceName ?? "").Trim();
            if (deviceName.Length == 0)
                deviceName = AaaBlockLibraryNameHelper.GetDeviceName(member.DisplayName);
            if (deviceName.Length > 0)
                missingDevices.Add(deviceName);
        }

        return resolvedMembers;
    }

    private static Scale3d CreateScale3d(AaaBlockComboMember member)
    {
        var scaleX = Math.Abs(member.ScaleX) < 1e-9 ? 1d : member.ScaleX;
        var scaleY = Math.Abs(member.ScaleY) < 1e-9 ? 1d : member.ScaleY;
        var scaleZ = Math.Abs(member.ScaleZ) < 1e-9 ? 1d : member.ScaleZ;
        return new Scale3d(scaleX, scaleY, scaleZ);
    }

    private static string ResolveLibraryRootPath(AaaBlockCatalogItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.LibraryRootPath))
            return item.LibraryRootPath;

        if (File.Exists(item.FullPath))
            return Path.GetDirectoryName(item.FullPath) ?? "";

        return Directory.Exists(item.FullPath)
            ? Path.GetDirectoryName(item.FullPath) ?? ""
            : "";
    }

    private static void EnsureLayerExists(Database database, Transaction transaction, string layerName)
    {
        var normalizedLayerName = (layerName ?? "").Trim();
        if (normalizedLayerName.Length == 0)
            return;

        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(normalizedLayerName))
            return;

        layerTable.UpgradeOpen();
        using var layerRecord = new LayerTableRecord
        {
            Name = normalizedLayerName
        };
        layerTable.Add(layerRecord);
        transaction.AddNewlyCreatedDBObject(layerRecord, true);
    }

    private static SingleBlockInsertionPlan? TryReadSingleBlockInsertion(Database sourceDatabase, Database targetDatabase)
    {
        return CadDatabaseScope.Read(
            sourceDatabase,
            (_, transaction) =>
            {
                var modelSpace = CadDatabaseScope.OpenAs<BlockTableRecord>(
                    transaction,
                    SymbolUtilityServices.GetBlockModelSpaceId(sourceDatabase),
                    OpenMode.ForRead);

                BlockReference? candidateReference = null;
                foreach (ObjectId entityId in modelSpace)
                {
                    if (!CadDatabaseScope.TryOpenAs<Entity>(transaction, entityId, OpenMode.ForRead, out var entity) ||
                        entity == null ||
                        entity.IsErased)
                    {
                        continue;
                    }

                    if (entity is not BlockReference blockReference)
                        return null;

                    if (candidateReference != null)
                        return null;

                    candidateReference = blockReference;
                }

                if (candidateReference == null)
                    return null;

                var sourceBlockRecordId = ResolvePreferredSourceBlockRecordId(candidateReference);
                if (sourceBlockRecordId.IsNull)
                    return null;

                if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(
                        transaction,
                        sourceBlockRecordId,
                        OpenMode.ForRead,
                        out var sourceBlockRecord) ||
                    sourceBlockRecord == null ||
                    sourceBlockRecord.IsLayout ||
                    sourceBlockRecord.IsFromExternalReference ||
                    sourceBlockRecord.IsFromOverlayReference)
                {
                    return null;
                }

                var importedBlockId = ImportBlockDefinition(sourceDatabase, targetDatabase, sourceBlockRecord.Name);
                return importedBlockId.IsNull
                    ? null
                    : new SingleBlockInsertionPlan
                    {
                        BlockId = importedBlockId,
                        Rotation = candidateReference.Rotation,
                        Scale = candidateReference.ScaleFactors,
                        LayerName = (candidateReference.Layer ?? "").Trim(),
                        AttributeSnapshots = ReadAttributeSnapshots(candidateReference, transaction),
                        DynamicPropertySnapshots = ReadDynamicPropertySnapshots(candidateReference)
                    };
            });
    }

    private static ObjectId ResolvePreferredSourceBlockRecordId(BlockReference blockReference)
    {
        try
        {
            if (blockReference.IsDynamicBlock &&
                !blockReference.DynamicBlockTableRecord.IsNull &&
                !blockReference.DynamicBlockTableRecord.IsInvalid())
            {
                return blockReference.DynamicBlockTableRecord;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块定义失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块定义失败（无效操作）", ex);
        }

        return blockReference.BlockTableRecord;
    }

    private static void AppendAttributeReferences(
        BlockReference blockReference,
        Transaction transaction,
        IReadOnlyList<AttributeValueSnapshot>? attributeSnapshots = null)
    {
        if (HasAttributeReferences(blockReference))
            return;

        if (!CadDatabaseScope.TryOpenAs<BlockTableRecord>(
                transaction,
                blockReference.BlockTableRecord,
                OpenMode.ForRead,
                out var blockDefinition) ||
            blockDefinition == null)
        {
            return;
        }

        var remainingSnapshots = attributeSnapshots == null
            ? new List<AttributeValueSnapshot>()
            : new List<AttributeValueSnapshot>(attributeSnapshots);
        foreach (ObjectId entityId in blockDefinition)
        {
            if (!CadDatabaseScope.TryOpenAs<AttributeDefinition>(transaction, entityId, OpenMode.ForRead, out var attributeDefinition) ||
                attributeDefinition == null ||
                attributeDefinition.Constant)
            {
                continue;
            }

            using var attributeReference = new AttributeReference();
            attributeReference.SetAttributeFromBlock(attributeDefinition, blockReference.BlockTransform);
            attributeReference.Position = attributeDefinition.Position.TransformBy(blockReference.BlockTransform);

            if (UsesAlignmentPoint(attributeDefinition.Justify))
                attributeReference.AlignmentPoint = attributeDefinition.AlignmentPoint.TransformBy(blockReference.BlockTransform);

            var attributeValue = ResolveAttributeValue(attributeDefinition, remainingSnapshots);
            if (attributeValue != null)
                ApplyAttributeValue(attributeReference, attributeValue);

            blockReference.AttributeCollection.AppendAttribute(attributeReference);
            transaction.AddNewlyCreatedDBObject(attributeReference, true);
        }
    }

    private static bool HasAttributeReferences(BlockReference blockReference)
    {
        foreach (ObjectId _ in blockReference.AttributeCollection)
            return true;

        return false;
    }

    private static IReadOnlyList<AttributeValueSnapshot> ReadAttributeSnapshots(
        BlockReference blockReference,
        Transaction transaction)
    {
        var snapshots = new List<AttributeValueSnapshot>();
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (!CadDatabaseScope.TryOpenAs<AttributeReference>(transaction, attributeId, OpenMode.ForRead, out var attributeReference) ||
                attributeReference == null)
            {
                continue;
            }

            snapshots.Add(new AttributeValueSnapshot
            {
                Tag = (attributeReference.Tag ?? "").Trim(),
                TextValue = GetAttributeValue(attributeReference)
            });
        }

        return snapshots;
    }

    private static IReadOnlyList<DynamicPropertySnapshot> ReadDynamicPropertySnapshots(BlockReference blockReference)
    {
        try
        {
            if (!blockReference.IsDynamicBlock)
                return Array.Empty<DynamicPropertySnapshot>();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块参数失败", ex);
            return Array.Empty<DynamicPropertySnapshot>();
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块参数失败（无效操作）", ex);
            return Array.Empty<DynamicPropertySnapshot>();
        }

        var snapshots = new List<DynamicPropertySnapshot>();
        try
        {
            foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
            {
                var propertyName = (property.PropertyName ?? "").Trim();
                if (propertyName.Length == 0 || property.ReadOnly)
                    continue;

                if (!TryCloneDynamicPropertyValue(property.Value, out var propertyValue))
                    continue;

                snapshots.Add(new DynamicPropertySnapshot
                {
                    Name = propertyName,
                    Value = propertyValue
                });
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块参数失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 读取动态图块参数失败（无效操作）", ex);
        }

        return snapshots;
    }

    private static bool TryCloneDynamicPropertyValue(object? value, out object? clonedValue)
    {
        clonedValue = null;
        if (value == null)
            return false;

        if (value is string text)
        {
            clonedValue = text;
            return true;
        }

        var valueType = value.GetType();
        if (!valueType.IsValueType)
            return false;

        clonedValue = value;
        return true;
    }

    private static void ApplyDynamicPropertySnapshots(
        BlockReference blockReference,
        IReadOnlyList<DynamicPropertySnapshot> dynamicPropertySnapshots)
    {
        if (dynamicPropertySnapshots == null || dynamicPropertySnapshots.Count == 0)
            return;

        try
        {
            if (!blockReference.IsDynamicBlock)
                return;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 应用动态图块参数失败", ex);
            return;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 应用动态图块参数失败（无效操作）", ex);
            return;
        }

        var remainingSnapshots = new List<DynamicPropertySnapshot>(dynamicPropertySnapshots);
        try
        {
            foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                    continue;

                var snapshot = ResolveDynamicPropertySnapshot(property, remainingSnapshots);
                if (snapshot == null)
                    continue;

                if (!TryApplyDynamicPropertySnapshot(property, snapshot))
                    continue;

                remainingSnapshots.Remove(snapshot);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 应用动态图块参数失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 应用动态图块参数失败（无效操作）", ex);
        }
    }

    private static DynamicPropertySnapshot? ResolveDynamicPropertySnapshot(
        DynamicBlockReferenceProperty property,
        List<DynamicPropertySnapshot> remainingSnapshots)
    {
        if (remainingSnapshots.Count == 0)
            return null;

        var propertyName = (property.PropertyName ?? "").Trim();
        if (propertyName.Length > 0)
        {
            for (var index = 0; index < remainingSnapshots.Count; index++)
            {
                var snapshot = remainingSnapshots[index];
                if (string.Equals(snapshot.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return snapshot;
            }
        }

        return remainingSnapshots[0];
    }

    private static bool TryApplyDynamicPropertySnapshot(
        DynamicBlockReferenceProperty property,
        DynamicPropertySnapshot snapshot)
    {
        if (snapshot.Value == null)
            return false;

        try
        {
            var currentValue = property.Value;
            property.Value = ConvertDynamicPropertyValue(snapshot.Value, currentValue);
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 应用动态图块参数失败（{snapshot.Name}）", ex);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 应用动态图块参数失败（无效操作，{snapshot.Name}）", ex);
            return false;
        }
        catch (FormatException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 应用动态图块参数失败（格式，{snapshot.Name}）", ex);
            return false;
        }
        catch (OverflowException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 应用动态图块参数失败（溢出，{snapshot.Name}）", ex);
            return false;
        }
    }

    private static object ConvertDynamicPropertyValue(object value, object? currentValue)
    {
        if (value == null)
            return "";

        var targetType = currentValue?.GetType();
        if (targetType == null || targetType == typeof(object))
            return value;

        var valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType))
            return value;

        if (targetType.IsEnum)
        {
            var enumUnderlyingType = Enum.GetUnderlyingType(targetType);
            var numericValue = value is IConvertible
                ? Convert.ChangeType(value, enumUnderlyingType, CultureInfo.InvariantCulture)
                : value;
            return Enum.ToObject(targetType, numericValue!);
        }

        if (targetType == typeof(string))
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        if (value is IConvertible)
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture)!;

        return value;
    }

    private static string? ResolveAttributeValue(
        AttributeDefinition attributeDefinition,
        List<AttributeValueSnapshot> remainingSnapshots)
    {
        if (remainingSnapshots.Count == 0)
            return null;

        var tag = (attributeDefinition.Tag ?? "").Trim();
        if (tag.Length > 0)
        {
            for (var index = 0; index < remainingSnapshots.Count; index++)
            {
                var snapshot = remainingSnapshots[index];
                if (!string.Equals(snapshot.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    continue;

                remainingSnapshots.RemoveAt(index);
                return snapshot.TextValue;
            }
        }

        var fallback = remainingSnapshots[0];
        remainingSnapshots.RemoveAt(0);
        return fallback.TextValue;
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

    private static string GetAttributeValue(AttributeReference attributeReference)
    {
        if (!attributeReference.IsMTextAttribute)
            return attributeReference.TextString ?? "";

        var mText = attributeReference.MTextAttribute;
        return mText?.Text ?? attributeReference.TextString ?? "";
    }

    private static bool UsesAlignmentPoint(AttachmentPoint attachmentPoint)
    {
        return attachmentPoint != AttachmentPoint.BaseLeft;
    }

    private static ObjectId ImportBlockDefinition(Database sourceDatabase, Database targetDatabase, string sourceBlockName)
    {
        if (targetDatabase == null || string.IsNullOrWhiteSpace(sourceBlockName))
            return ObjectId.Null;

        var sourceBlockId = CadDatabaseScope.Read(
            sourceDatabase,
            (_, transaction) =>
            {
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, sourceDatabase.BlockTableId, OpenMode.ForRead);
                return blockTable.Has(sourceBlockName)
                    ? blockTable[sourceBlockName]
                    : ObjectId.Null;
            });
        if (sourceBlockId.IsNull)
            return ObjectId.Null;

        var importedIds = new IdMapping();
        var sourceIds = new ObjectIdCollection(new[] { sourceBlockId });
        sourceDatabase.WblockCloneObjects(
            sourceIds,
            targetDatabase.BlockTableId,
            importedIds,
            DuplicateRecordCloning.Ignore,
            false);

        return CadDatabaseScope.Read(
            targetDatabase,
            (_, transaction) =>
            {
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, targetDatabase.BlockTableId, OpenMode.ForRead);
                return blockTable.Has(sourceBlockName)
                    ? blockTable[sourceBlockName]
                    : ObjectId.Null;
            });
    }

    private static ObjectId EnsureBlockDefinition(
        Database database,
        string displayName,
        string sourcePath,
        DateTime lastWriteTimeUtc)
    {
        using var sourceDatabase = new Database(false, true);
        sourceDatabase.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, "");
        return InsertWholeDrawingAsDefinition(sourceDatabase, database, displayName, sourcePath, lastWriteTimeUtc);
    }

    private static ObjectId InsertWholeDrawingAsDefinition(
        Database sourceDatabase,
        Database database,
        string displayName,
        string sourcePath,
        DateTime lastWriteTimeUtc)
    {
        var blockName = BuildBlockDefinitionName(displayName, sourcePath, lastWriteTimeUtc);

        var existingBlockId = CadDatabaseScope.Read(
            database,
            (currentDatabase, transaction) =>
            {
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, currentDatabase.BlockTableId, OpenMode.ForRead);
                return blockTable.Has(blockName)
                    ? blockTable[blockName]
                    : ObjectId.Null;
            });
        if (!existingBlockId.IsNull)
            return existingBlockId;

        return database.Insert(blockName, sourceDatabase, true);
    }

    private static string BuildBlockDefinitionName(string displayName, string fullPath, DateTime lastWriteTimeUtc)
    {
        var name = SanitizeNamePart(displayName);
        var normalizedPath = Path.GetFullPath(fullPath).Trim().ToUpperInvariant();
        var hash = ComputeHash(normalizedPath + "|" + lastWriteTimeUtc.Ticks.ToString());
        return $"AAA_{name}_{hash}";
    }

    private static string SanitizeNamePart(string text)
    {
        var value = (text ?? "").Trim();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (ch == '_' || ch == '-')
                builder.Append(ch);
            else if (char.IsWhiteSpace(ch))
                builder.Append('_');
        }

        if (builder.Length == 0)
            builder.Append("BLOCK");
        if (builder.Length > 24)
            builder.Length = 24;
        return builder.ToString();
    }

    private static string ComputeHash(string text)
    {
        using var sha1 = SHA1.Create();
        var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(10);
        for (var i = 0; i < 5 && i < bytes.Length; i++)
            builder.Append(bytes[i].ToString("X2"));
        return builder.ToString();
    }

    private sealed class ResolvedComboMember
    {
        internal AaaBlockComboMember Member { get; set; } = new();
        internal string DisplayName { get; set; } = "";
        internal string SourcePath { get; set; } = "";
        internal DateTime LastWriteTimeUtc { get; set; }
    }

    private sealed class SingleBlockInsertionPlan
    {
        internal ObjectId BlockId { get; set; } = ObjectId.Null;
        internal double Rotation { get; set; }
        internal Scale3d Scale { get; set; } = new Scale3d(1d);
        internal string LayerName { get; set; } = "";
        internal IReadOnlyList<AttributeValueSnapshot> AttributeSnapshots { get; set; } = Array.Empty<AttributeValueSnapshot>();
        internal IReadOnlyList<DynamicPropertySnapshot> DynamicPropertySnapshots { get; set; } = Array.Empty<DynamicPropertySnapshot>();
    }

    private sealed class AttributeValueSnapshot
    {
        internal string Tag { get; set; } = "";
        internal string TextValue { get; set; } = "";
    }

    private sealed class DynamicPropertySnapshot
    {
        internal string Name { get; set; } = "";
        internal object? Value { get; set; }
    }
}

internal sealed class AaaInsertResult
{
    public bool Success { get; private set; }
    public bool Cancelled { get; private set; }
    public string Message { get; private set; } = "";

    internal static AaaInsertResult Succeed(string message) =>
        new() { Success = true, Message = message ?? "" };

    internal static AaaInsertResult CancelledResult(string message) =>
        new() { Cancelled = true, Message = message ?? "" };

    internal static AaaInsertResult Failure(string message) =>
        new() { Message = message ?? "" };
}
