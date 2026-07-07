using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.IO;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal static class BbbBlockHiddenDeviceNameService
{
    private const string CommandName = BbbPluginCommandIds.BlockAssignHiddenDeviceNames;
    private const string WorksheetName = BbbHiddenDeviceNameConfigStore.DefaultWorksheetName;
    private const string ColumnHeader = BbbHiddenDeviceNameConfigStore.DefaultColumnHeader;

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;

        try
        {
            var selectedIds = SelectTargetIds(editor);
            if (selectedIds == null || selectedIds.Count == 0)
                return;

            var capture = CaptureTargets(doc, selectedIds);
            if (capture.Targets.Count == 0)
            {
                editor.WriteMessage($"\n{CommandName}：无块可处理。");
                return;
            }

            if (capture.UnrecognizedDynamicStateMessages.Count > 0)
            {
                editor.WriteMessage($"\n{CommandName}：以下动态块状态未识别，已按通用属性写入：");
                foreach (var message in capture.UnrecognizedDynamicStateMessages)
                    editor.WriteMessage($"\n  - {message}");
            }

            var workbookPath = BbbHiddenDeviceNameConfigStore.LoadWorkbookPath();
            if (!File.Exists(workbookPath))
            {
                editor.WriteMessage($"\n{CommandName}：Excel 模板路径无效，请在 BB 浮窗选择。路径：{workbookPath}");
                return;
            }

            var (deviceNames, messages) = BbbWorkbookReader.ImportDistinctColumnValues(workbookPath, WorksheetName, ColumnHeader);
            if (deviceNames.Count == 0)
            {
                var errorText = messages.Count == 0 ? "未从设备清单读取到可用设备名称。" : string.Join(" ", messages);
                editor.WriteMessage($"\n{CommandName}：{errorText}");
                return;
            }

            var window = new BbbBlockHiddenDeviceNameWindow(
                capture.Targets,
                deviceNames,
                capture.PreselectedDeviceNames,
                workbookPath,
                WorksheetName,
                ColumnHeader,
                capture.OrdinaryBlockCount);

            var dialogResult = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);
            if (dialogResult != true)
            {
                editor.WriteMessage($"\n{CommandName}：{UIMessages.Common.Cancelled}");
                return;
            }

            var selectedNames = window.SelectedDeviceNames;
            if (selectedNames.Count == 0)
            {
                editor.WriteMessage($"\n{CommandName}：请勾选设备名称。");
                return;
            }

            var summary = ApplySelectedNames(doc, capture.Targets, selectedNames);
            if (capture.OrdinaryBlockCount > 0)
                summary += $" 其中普通块 {capture.OrdinaryBlockCount} 个。";
            if (capture.SkippedInvalidCount > 0)
                summary += $" 已跳过 {capture.SkippedInvalidCount} 个失效对象。";

            editor.WriteMessage(summary);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
        {
            editor.WriteMessage($"\n{CommandName}：{UIMessages.Common.Cancelled}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 写入隐形设备名称失败", ex);
            editor.WriteMessage($"\n{CommandName}：执行失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<ObjectId>? SelectTargetIds(Editor editor)
    {
        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\n选择需写入隐形设备名称的块："
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

    private static BbbBlockHiddenDeviceNameCaptureResult CaptureTargets(Document doc, IReadOnlyList<ObjectId> selectedIds)
    {
        return CadDatabaseScope.Read(
            doc.Database,
            (_, transaction) =>
            {
                var result = new BbbBlockHiddenDeviceNameCaptureResult();
                var preselectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var objectId in selectedIds.Distinct())
                {
                    if (objectId.IsInvalid())
                    {
                        result.SkippedInvalidCount++;
                        continue;
                    }

                    if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, objectId, OpenMode.ForRead, out var blockReference) ||
                        blockReference == null)
                    {
                        result.SkippedInvalidCount++;
                        continue;
                    }

                    if (!blockReference.IsDynamicBlock)
                        result.OrdinaryBlockCount++;

                    var currentGroup = BbbDynamicBlockStateHelper.ReadCurrentManagedNames(blockReference, transaction);
                    var currentNames = currentGroup.Entries
                        .Select(x => x.Name)
                        .Where(x => x.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var currentName in currentNames)
                        preselectedNames.Add(currentName);

                    var blockName = GetDisplayBlockName(blockReference, transaction);
                    var stateDisplayText = BbbDynamicBlockStateHelper.GetStateDisplayText(currentGroup.StateInfo);
                    if (blockReference.IsDynamicBlock && !currentGroup.StateInfo.HasState)
                    {
                        var debugText = BbbDynamicBlockStateHelper.BuildDynamicPropertyDebugText(blockReference);
                        result.UnrecognizedDynamicStateMessages.Add(
                            $"句柄 {blockReference.Handle}，块名 {blockName}，动态参数：{debugText}");
                        stateDisplayText = "未识别（按通用属性，见命令行）";
                    }

                    result.Targets.Add(new BbbBlockHiddenDeviceNameTarget
                    {
                        BlockReferenceId = objectId,
                        HandleText = blockReference.Handle.ToString(),
                        BlockName = blockName,
                        StateDisplayText = stateDisplayText,
                        ExistingDeviceNamesText = currentNames.Count == 0 ? "—" : string.Join("；", currentNames)
                    });
                }

                result.PreselectedDeviceNames.AddRange(preselectedNames);
                return result;
            });
    }

    private static string ApplySelectedNames(
        Document doc,
        IReadOnlyList<BbbBlockHiddenDeviceNameTarget> targets,
        IReadOnlyList<string> selectedNames)
    {
        var writeSummary = CadDatabaseScope.Write(
            doc,
            (database, transaction) =>
            {
                var writtenBlocks = 0;
                var updatedAttributes = 0;
                var createdAttributes = 0;
                var clearedAttributes = 0;
                var skippedBlocks = 0;

                foreach (var target in targets)
                {
                    if (target.BlockReferenceId.IsInvalid())
                    {
                        skippedBlocks++;
                        continue;
                    }

                    if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, target.BlockReferenceId, OpenMode.ForWrite, out var blockReference) ||
                        blockReference == null)
                    {
                        skippedBlocks++;
                        continue;
                    }

                    var stateInfo = BbbDynamicBlockStateHelper.GetStateInfo(blockReference);
                    var managedAttributes = BbbDynamicBlockStateHelper.GetManagedAttributesForWrite(blockReference, transaction, stateInfo);
                    var templateAttribute = managedAttributes.FirstOrDefault() ?? GetAnyAttribute(blockReference, transaction);

                    var existingByTag = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
                    var duplicateAttributes = new List<AttributeReference>();
                    foreach (var attributeReference in managedAttributes)
                    {
                        var identityKey = BbbDynamicBlockStateHelper.BuildManagedIdentityKey(attributeReference.Tag);
                        if (identityKey.Length == 0)
                            continue;

                        if (existingByTag.ContainsKey(identityKey))
                            duplicateAttributes.Add(attributeReference);
                        else
                            existingByTag[identityKey] = attributeReference;
                    }

                    var desiredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < selectedNames.Count; index++)
                    {
                        var slotIndex = index + 1;
                        var tag = BbbDynamicBlockStateHelper.BuildManagedTag(slotIndex, stateInfo);
                        var identityKey = BbbDynamicBlockStateHelper.BuildManagedIdentityKey(tag);
                        desiredTags.Add(identityKey);

                        if (existingByTag.TryGetValue(identityKey, out var existingAttribute))
                        {
                            UpdateManagedAttribute(existingAttribute, selectedNames[index]);
                            updatedAttributes++;
                            continue;
                        }

                        CreateManagedAttribute(blockReference, database, transaction, templateAttribute, tag, selectedNames[index]);
                        createdAttributes++;
                    }

                    foreach (var attributeReference in managedAttributes)
                    {
                        if (desiredTags.Contains(BbbDynamicBlockStateHelper.BuildManagedIdentityKey(attributeReference.Tag)))
                            continue;

                        ClearManagedAttribute(attributeReference);
                        clearedAttributes++;
                    }

                    foreach (var duplicateAttribute in duplicateAttributes)
                    {
                        ClearManagedAttribute(duplicateAttribute);
                        clearedAttributes++;
                    }

                    writtenBlocks++;
                }

                return (
                    WrittenBlocks: writtenBlocks,
                    UpdatedAttributes: updatedAttributes,
                    CreatedAttributes: createdAttributes,
                    ClearedAttributes: clearedAttributes,
                    SkippedBlocks: skippedBlocks);
            },
            requireDocumentLock: true);

        var writtenBlocks = writeSummary.WrittenBlocks;
        var updatedAttributes = writeSummary.UpdatedAttributes;
        var createdAttributes = writeSummary.CreatedAttributes;
        var clearedAttributes = writeSummary.ClearedAttributes;
        var skippedBlocks = writeSummary.SkippedBlocks;

        var message = $"\n{CommandName}：已为 {writtenBlocks} 个块写入 {selectedNames.Count} 个隐形设备名称。";
        if (updatedAttributes > 0)
            message += $" 已更新属性 {updatedAttributes} 项。";
        if (createdAttributes > 0)
            message += $" 已新增属性 {createdAttributes} 项。";
        if (clearedAttributes > 0)
            message += $" 已清空多余属性 {clearedAttributes} 项。";
        if (skippedBlocks > 0)
            message += $" 已跳过失效块 {skippedBlocks} 个。";
        return message;
    }

    private static AttributeReference? GetAnyAttribute(BlockReference blockReference, Transaction transaction)
    {
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (attributeId.IsInvalid())
                continue;

            if (CadDatabaseScope.TryOpenAs<AttributeReference>(transaction, attributeId, OpenMode.ForRead, out var attributeReference) &&
                attributeReference != null)
            {
                return attributeReference;
            }
        }

        return null;
    }

    private static void UpdateManagedAttribute(AttributeReference attributeReference, string value)
    {
        attributeReference.Invisible = true;
        ApplyAttributeValue(attributeReference, value);
    }

    private static void ClearManagedAttribute(AttributeReference attributeReference)
    {
        attributeReference.Invisible = true;
        ApplyAttributeValue(attributeReference, "");
    }

    private static void CreateManagedAttribute(
        BlockReference blockReference,
        Database database,
        Transaction transaction,
        AttributeReference? templateAttribute,
        string tag,
        string value)
    {
        var attributeReference = new AttributeReference();
        attributeReference.SetDatabaseDefaults();

        if (templateAttribute != null)
        {
            attributeReference.SetPropertiesFrom(templateAttribute);
            attributeReference.TextStyleId = templateAttribute.TextStyleId;
            attributeReference.Position = templateAttribute.Position;
            attributeReference.Height = templateAttribute.Height > 0 ? templateAttribute.Height : 1.0;
            attributeReference.Rotation = templateAttribute.Rotation;
            attributeReference.Oblique = templateAttribute.Oblique;
            attributeReference.WidthFactor = templateAttribute.WidthFactor;
            attributeReference.HorizontalMode = templateAttribute.HorizontalMode;
            attributeReference.VerticalMode = templateAttribute.VerticalMode;
            attributeReference.Justify = templateAttribute.Justify;
            attributeReference.IsMirroredInX = templateAttribute.IsMirroredInX;
            attributeReference.IsMirroredInY = templateAttribute.IsMirroredInY;
            attributeReference.Normal = templateAttribute.Normal;
            attributeReference.Thickness = templateAttribute.Thickness;

            if (UsesAlignmentPoint(templateAttribute.Justify))
                attributeReference.AlignmentPoint = templateAttribute.AlignmentPoint;
        }
        else
        {
            attributeReference.SetPropertiesFrom(blockReference);
            attributeReference.TextStyleId = database.Textstyle;
            attributeReference.Position = Point3d.Origin.TransformBy(blockReference.BlockTransform);
            attributeReference.Height = 1.0;
            attributeReference.Rotation = blockReference.Rotation;
            attributeReference.Normal = blockReference.Normal;
        }

        attributeReference.Tag = tag;
        attributeReference.Invisible = true;
        ApplyAttributeValue(attributeReference, value);
        blockReference.AttributeCollection.AppendAttribute(attributeReference);
        transaction.AddNewlyCreatedDBObject(attributeReference, true);
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
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取动态图块名称失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取动态图块名称失败（CAD）", ex);
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
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取匿名块名称失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取匿名块名称失败（CAD）", ex);
        }

        var directName = TryGetBlockName(blockReference.BlockTableRecord, transaction);
        return string.IsNullOrWhiteSpace(directName) ? "<匿名动态图块>" : directName;
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
}
