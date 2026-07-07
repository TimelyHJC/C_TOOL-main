using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal static class BbbTextToAttributeService
{
    private const string DefaultAttributeName = "设备名称";
    private const string BlockNamePrefix = "C_TOOL_BBB_ZSX_";
    private static string s_currentAttributeName = DefaultAttributeName;

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;

        try
        {
            var selectedIds = SelectTargetIds(editor);
            if (selectedIds == null || selectedIds.Count == 0)
                return;

            var attributeName = PromptForAttributeName(editor);
            if (attributeName == null)
                return;

            var conversionResult = CadDatabaseScope.Write(
                doc,
                (database, transaction) =>
                {
                    var blockTable = CadDatabaseScope.OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
                    var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(database, transaction);
                    var convertedCount = 0;
                    var skippedCount = 0;

                    foreach (var objectId in selectedIds.Distinct())
                    {
                        if (objectId.IsInvalid())
                        {
                            skippedCount++;
                            continue;
                        }

                        if (!CadDatabaseScope.TryOpenAs<Entity>(transaction, objectId, OpenMode.ForWrite, out var entity) ||
                            entity == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        switch (entity)
                        {
                            case DBText dbText:
                                ConvertDbText(dbText, blockTable, currentSpace, transaction, attributeName);
                                convertedCount++;
                                break;

                            case MText mText:
                                ConvertMText(mText, blockTable, currentSpace, transaction, attributeName);
                                convertedCount++;
                                break;

                            default:
                                skippedCount++;
                                break;
                        }
                    }

                    return (ConvertedCount: convertedCount, SkippedCount: skippedCount);
                },
                requireDocumentLock: true);

            var convertedCount = conversionResult.ConvertedCount;
            var skippedCount = conversionResult.SkippedCount;

            if (convertedCount == 0)
            {
                editor.WriteMessage(skippedCount > 0
                    ? "\nF_zsx：无文字可转（仅支持 TEXT/MTEXT）。"
                    : "\nF_zsx：未选文字。");
                return;
            }

            var message = $"\nF_zsx：已转 {convertedCount} 个文字为属性块，标签“{attributeName}”。";
            if (skippedCount > 0)
                message += $" 跳过 {skippedCount} 个非文字。";

            editor.WriteMessage(message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        {
            editor.WriteMessage($"\n{UIMessages.Prefix_F_zsx}{UIMessages.Common.Cancelled}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_zsx 文字转属性块失败", ex);
            editor.WriteMessage($"\nF_zsx：转换失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<ObjectId>? SelectTargetIds(Editor editor)
    {
        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            return implied.Value.GetObjectIds();

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\n请选择要转为带属性文字的 TEXT/MTEXT："
        };

        var picked = editor.GetSelection(options, BuildTextSelectionFilter());
        if (picked.Status == PromptStatus.Cancel)
        {
            editor.WriteMessage($"\n{UIMessages.Prefix_F_zsx}{UIMessages.Common.Cancelled}");
            return null;
        }

        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            editor.WriteMessage("\nF_zsx：未选择任何文字。");
            return null;
        }

        return picked.Value.GetObjectIds();
    }

    private static string? PromptForAttributeName(Editor editor)
    {
        while (true)
        {
            var keywordOptions = new PromptKeywordOptions($"\n当前标记为：{s_currentAttributeName}，按 S 可修改，回车继续：")
            {
                AllowNone = true
            };
            keywordOptions.Keywords.Add("S");

            var keywordResult = editor.GetKeywords(keywordOptions);
            if (keywordResult.Status == PromptStatus.Cancel)
            {
                editor.WriteMessage($"\n{UIMessages.Prefix_F_zsx}{UIMessages.Common.Cancelled}");
                return null;
            }

            if (keywordResult.Status == PromptStatus.None)
                return s_currentAttributeName;

            if (!string.Equals(keywordResult.StringResult, "S", StringComparison.OrdinalIgnoreCase))
                return s_currentAttributeName;

            var stringOptions = new PromptStringOptions($"\n请输入新的属性标记 <{s_currentAttributeName}>：")
            {
                AllowSpaces = true
            };

            var stringResult = editor.GetString(stringOptions);
            if (stringResult.Status == PromptStatus.Cancel)
            {
                editor.WriteMessage($"\n{UIMessages.Prefix_F_zsx}{UIMessages.Common.Cancelled}");
                return null;
            }

            if (stringResult.Status == PromptStatus.None)
                return s_currentAttributeName;

            var candidate = (stringResult.StringResult ?? "").Trim();
            if (candidate.Length == 0)
            {
                editor.WriteMessage("\nF_zsx：属性标记不能为空。");
                continue;
            }

            s_currentAttributeName = candidate;
            return s_currentAttributeName;
        }
    }

    private static SelectionFilter BuildTextSelectionFilter()
    {
        return new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Operator, "<OR"),
            new TypedValue((int)DxfCode.Start, "TEXT"),
            new TypedValue((int)DxfCode.Start, "MTEXT"),
            new TypedValue((int)DxfCode.Operator, "OR>")
        });
    }

    private static void ConvertDbText(
        DBText source,
        BlockTable blockTable,
        BlockTableRecord currentSpace,
        Transaction transaction,
        string attributeName)
    {
        var basePoint = source.Position;
        var (blockId, attributeDefinitionId) = CreateBlockDefinitionFromDbText(source, blockTable, transaction, basePoint, attributeName);
        InsertAttributeBlock(source, basePoint, blockId, attributeDefinitionId, source.TextString ?? "", null, currentSpace, transaction);
        source.Erase();
    }

    private static void ConvertMText(
        MText source,
        BlockTable blockTable,
        BlockTableRecord currentSpace,
        Transaction transaction,
        string attributeName)
    {
        var basePoint = source.Location;
        var (blockId, attributeDefinitionId) = CreateBlockDefinitionFromMText(source, blockTable, transaction, basePoint, attributeName);
        InsertAttributeBlock(source, basePoint, blockId, attributeDefinitionId, source.Text ?? "", source, currentSpace, transaction);
        source.Erase();
    }

    private static (ObjectId BlockId, ObjectId AttributeDefinitionId) CreateBlockDefinitionFromDbText(
        DBText source,
        BlockTable blockTable,
        Transaction transaction,
        Point3d basePoint,
        string attributeName)
    {
        var blockId = CreateEmptyBlockDefinition(blockTable, transaction);
        var blockDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, blockId, OpenMode.ForWrite);

        using var attributeDefinition = new AttributeDefinition();
        attributeDefinition.SetDatabaseDefaults();
        attributeDefinition.SetPropertiesFrom(source);
        ApplyCommonAttributeSettings(attributeDefinition, attributeName, source.TextString ?? "");
        attributeDefinition.TextStyleId = source.TextStyleId;
        attributeDefinition.Position = ToLocalPoint(source.Position, basePoint);
        attributeDefinition.Height = source.Height;
        attributeDefinition.Rotation = source.Rotation;
        attributeDefinition.Oblique = source.Oblique;
        attributeDefinition.WidthFactor = source.WidthFactor;
        attributeDefinition.HorizontalMode = source.HorizontalMode;
        attributeDefinition.VerticalMode = source.VerticalMode;
        attributeDefinition.Justify = source.Justify;
        attributeDefinition.IsMirroredInX = source.IsMirroredInX;
        attributeDefinition.IsMirroredInY = source.IsMirroredInY;
        attributeDefinition.Normal = source.Normal;
        attributeDefinition.Thickness = source.Thickness;
        if (UsesAlignmentPoint(source.Justify))
            attributeDefinition.AlignmentPoint = ToLocalPoint(source.AlignmentPoint, basePoint);

        blockDefinition.AppendEntity(attributeDefinition);
        transaction.AddNewlyCreatedDBObject(attributeDefinition, true);
        return (blockId, attributeDefinition.ObjectId);
    }

    private static (ObjectId BlockId, ObjectId AttributeDefinitionId) CreateBlockDefinitionFromMText(
        MText source,
        BlockTable blockTable,
        Transaction transaction,
        Point3d basePoint,
        string attributeName)
    {
        var blockId = CreateEmptyBlockDefinition(blockTable, transaction);
        var blockDefinition = CadDatabaseScope.OpenAs<BlockTableRecord>(transaction, blockId, OpenMode.ForWrite);

        using var attributeDefinition = new AttributeDefinition();
        attributeDefinition.SetDatabaseDefaults();
        attributeDefinition.SetPropertiesFrom(source);
        ApplyCommonAttributeSettings(attributeDefinition, attributeName, source.Text ?? "");
        attributeDefinition.TextStyleId = source.TextStyleId;
        attributeDefinition.Position = Point3d.Origin;
        attributeDefinition.Height = source.TextHeight;
        attributeDefinition.Rotation = source.Rotation;
        attributeDefinition.Justify = source.Attachment;
        attributeDefinition.Normal = source.Normal;
        attributeDefinition.IsMTextAttributeDefinition = true;

        // AutoCAD takes over this clone after assignment; disposing it here can invalidate
        // the native MText pointer and crash when converting MTEXT entities.
        var localMText = (MText)source.Clone();
        localMText.Location = ToLocalPoint(source.Location, basePoint);
        attributeDefinition.MTextAttributeDefinition = localMText;

        blockDefinition.AppendEntity(attributeDefinition);
        transaction.AddNewlyCreatedDBObject(attributeDefinition, true);
        return (blockId, attributeDefinition.ObjectId);
    }

    private static ObjectId CreateEmptyBlockDefinition(BlockTable blockTable, Transaction transaction)
    {
        if (!blockTable.IsWriteEnabled)
            blockTable.UpgradeOpen();

        using var blockDefinition = new BlockTableRecord
        {
            Name = CreateUniqueBlockName(blockTable),
            Origin = Point3d.Origin
        };

        var blockId = blockTable.Add(blockDefinition);
        transaction.AddNewlyCreatedDBObject(blockDefinition, true);
        return blockId;
    }

    private static void InsertAttributeBlock(
        Entity source,
        Point3d basePoint,
        ObjectId blockId,
        ObjectId attributeDefinitionId,
        string textValue,
        MText? sourceMText,
        BlockTableRecord currentSpace,
        Transaction transaction)
    {
        using var blockReference = new BlockReference(basePoint, blockId);
        blockReference.SetDatabaseDefaults();
        blockReference.SetPropertiesFrom(source);
        currentSpace.AppendEntity(blockReference);
        transaction.AddNewlyCreatedDBObject(blockReference, true);

        var attributeDefinition = CadDatabaseScope.OpenAs<AttributeDefinition>(transaction, attributeDefinitionId, OpenMode.ForRead);
        using var attributeReference = new AttributeReference();
        attributeReference.SetAttributeFromBlock(attributeDefinition, blockReference.BlockTransform);
        attributeReference.Position = attributeDefinition.Position.TransformBy(blockReference.BlockTransform);
        attributeReference.TextString = textValue;
        if (UsesAlignmentPoint(attributeDefinition.Justify))
            attributeReference.AlignmentPoint = attributeDefinition.AlignmentPoint.TransformBy(blockReference.BlockTransform);

        if (sourceMText != null)
        {
            // Same ownership rule as the definition path above: keep the clone alive after
            // handing it to the attribute reference.
            var absoluteMText = (MText)sourceMText.Clone();
            attributeReference.MTextAttribute = absoluteMText;
            attributeReference.TextString = sourceMText.Text ?? textValue;
        }

        blockReference.AttributeCollection.AppendAttribute(attributeReference);
        transaction.AddNewlyCreatedDBObject(attributeReference, true);
    }

    private static void ApplyCommonAttributeSettings(
        AttributeDefinition attributeDefinition,
        string attributeName,
        string textValue)
    {
        attributeDefinition.Tag = attributeName;
        attributeDefinition.Prompt = attributeName;
        attributeDefinition.TextString = textValue;
        attributeDefinition.Constant = false;
        attributeDefinition.Invisible = false;
        attributeDefinition.Preset = true;
        attributeDefinition.Verifiable = false;
        attributeDefinition.LockPositionInBlock = true;
    }

    private static Point3d ToLocalPoint(Point3d worldPoint, Point3d basePoint)
    {
        return Point3d.Origin + (worldPoint - basePoint);
    }

    private static bool UsesAlignmentPoint(AttachmentPoint attachmentPoint)
    {
        return attachmentPoint != AttachmentPoint.BaseLeft;
    }

    private static string CreateUniqueBlockName(BlockTable blockTable)
    {
        var seed = $"{BlockNamePrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        var candidate = seed;
        var suffix = 1;
        while (blockTable.Has(candidate))
        {
            candidate = $"{seed}_{suffix}";
            suffix++;
        }

        return candidate;
    }
}
