using System.Text;
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

            var settings = new BbbDeviceBlockCreateSettings();
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

                var result = CreateDeviceBlock(doc, selectedIds, settings);
                editor.WriteMessage($"\n{CommandName}：已创建设备块“{result.BlockName}”，包含 {result.ClonedEntityCount} 个对象。");
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
        var options = new PromptEntityOptions($"\n{CommandName}：选择作为设备块名称的文字：");
        options.SetRejectMessage("\n请选择 TEXT 或 MTEXT。");
        options.AddAllowedClass(typeof(DBText), exactMatch: false);
        options.AddAllowedClass(typeof(MText), exactMatch: false);

        var picked = editor.GetEntity(options);
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

        return CadDatabaseScope.Read(
            doc.Database,
            (_, transaction) =>
            {
                if (CadDatabaseScope.TryOpenAs<DBText>(transaction, picked.ObjectId, OpenMode.ForRead, out var dbText) &&
                    dbText != null)
                {
                    return dbText.TextString ?? "";
                }

                if (CadDatabaseScope.TryOpenAs<MText>(transaction, picked.ObjectId, OpenMode.ForRead, out var mText) &&
                    mText != null)
                {
                    return mText.Text ?? "";
                }

                return "";
            });
    }

    private static BbbDeviceBlockCreateResult CreateDeviceBlock(
        Document doc,
        IReadOnlyList<ObjectId> selectedIds,
        BbbDeviceBlockCreateSettings settings)
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
                var blockName = CreateUniqueBlockName(blockTable, normalizedName);
                var blockId = CreateBlockDefinition(blockTable, transaction, blockName);
                var blockDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, blockId, OpenMode.ForWrite);
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
                    ClonedEntityCount = clonedCount
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

    private static string CreateUniqueBlockName(BlockTable blockTable, string baseName)
    {
        var candidate = baseName;
        var suffix = 1;
        while (blockTable.Has(candidate))
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }

        return candidate;
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
