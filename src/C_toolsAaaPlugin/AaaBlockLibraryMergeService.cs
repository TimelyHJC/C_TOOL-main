using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockLibraryMergeService
{
    internal static AaaImportResult PromptAndImportOrUpdate(
        Document doc,
        string targetFolder,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems)
    {
        if (doc == null)
            return AaaImportResult.Failure(UIMessages.BlockLibrary.NoActiveDocument);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return AaaImportResult.Failure(UIMessages.BlockLibrary.InvalidFolder);

        try
        {
            var editor = doc.Editor;
            var implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                var impliedBlocks = AaaBlockSelectionReader.ReadSelection(doc.Database, implied.Value, out var impliedSelectedCount);
                if (impliedBlocks.Count > 0)
                    return ImportOrUpdateBlocks(doc, targetFolder, libraryItems, impliedBlocks, impliedSelectedCount);
            }

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nV_AAA：请选择要添加/更新到图库的图块（可多选）：",
                SingleOnly = false,
                SinglePickInSpace = false
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT")
            });
            var picked = editor.GetSelection(options, filter);
            if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
            {
                return picked.Status == PromptStatus.Cancel
                    ? AaaImportResult.CancelledResult("已取消添加/更新图块。")
                    : AaaImportResult.Failure(UIMessages.Command.NoBlockSelected);
            }

            var selectedBlocks = AaaBlockSelectionReader.ReadSelection(doc.Database, picked.Value, out var selectedCount);
            return selectedBlocks.Count == 0
                ? AaaImportResult.Failure("没有可添加/更新的图块。")
                : ImportOrUpdateBlocks(doc, targetFolder, libraryItems, selectedBlocks, selectedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新图库图块失败", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新图库图块失败", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新图库图块失败（权限）", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新图库图块失败", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
    }

    private static AaaImportResult ImportOrUpdateBlocks(
        Document doc,
        string targetFolder,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems,
        IReadOnlyList<AaaBlockExportItem> selectedBlocks,
        int selectedCount)
    {
        Directory.CreateDirectory(targetFolder);

        var existingItems = (libraryItems ?? Array.Empty<AaaBlockCatalogItem>())
            .Where(x => !x.IsCombo && !string.IsNullOrWhiteSpace(x.FullPath))
            .ToList();
        var deviceIndex = existingItems
            .GroupBy(x => AaaBlockLibraryNameHelper.GetDeviceName(x.DisplayName), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var reservedPaths = new HashSet<string>(
            existingItems.Select(x => x.FullPath).Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        var addedPaths = new List<string>();
        var updatedPaths = new List<string>();
        var failedCount = 0;
        var duplicateSelectedCount = 0;
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var documentLock = doc.LockDocument();
        foreach (var block in selectedBlocks)
        {
            var deviceName = AaaBlockLibraryNameHelper.GetDeviceName(block.DisplayName);
            var processedKey = deviceName.Length > 0
                ? "D:" + deviceName
                : "N:" + (block.DisplayName ?? "").Trim();
            if (processedKey.Length <= 2)
                processedKey = "H:" + block.BlockHandle;

            if (!processedKeys.Add(processedKey))
            {
                duplicateSelectedCount++;
                continue;
            }

            try
            {
                if (deviceName.Length > 0 &&
                    deviceIndex.TryGetValue(deviceName, out var matches) &&
                    matches.Count > 0)
                {
                    UpdateMatchingBlocks(doc, block, matches, updatedPaths);
                }
                else
                {
                    AddNewBlock(doc, targetFolder, block, reservedPaths, addedPaths);
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 添加/更新图库图块失败（{block.DisplayName}）", ex);
            }
            catch (IOException ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 添加/更新图库图块失败（{block.DisplayName}）", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 添加/更新图库图块失败（权限，{block.DisplayName}）", ex);
            }
            catch (Exception ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 添加/更新图库图块失败（{block.DisplayName}）", ex);
            }
        }

        if (addedPaths.Count == 0 && updatedPaths.Count == 0)
            return AaaImportResult.Failure("添加/更新失败。");

        var ignoredCount = Math.Max(0, selectedCount - selectedBlocks.Count);
        var messageText = $"添加/更新成功，新增 {addedPaths.Count} 个，替换 {updatedPaths.Count} 个。";
        if (duplicateSelectedCount > 0)
            messageText += $" 忽略 {duplicateSelectedCount} 个重复项。";
        if (ignoredCount > 0)
            messageText += $" 跳过 {ignoredCount} 个。";
        if (failedCount > 0)
            messageText += $" 失败 {failedCount} 个。";

        doc.Editor.WriteMessage($"\nV_AAA：{messageText}");
        return AaaImportResult.Succeed(messageText, addedPaths.Count + updatedPaths.Count);
    }

    private static void AddNewBlock(
        Document doc,
        string targetFolder,
        AaaBlockExportItem block,
        ISet<string> reservedPaths,
        ICollection<string> addedPaths)
    {
        var filePath = AaaPathNamingHelper.BuildUniqueDwgFilePath(
            targetFolder,
            block.DisplayName,
            block.BlockHandle,
            reservedPaths);
        var objectIds = new ObjectIdCollection();
        objectIds.Add(block.BlockReferenceId);

        using var exportDatabase = doc.Database.Wblock(objectIds, block.BasePoint);
        exportDatabase.SaveAs(filePath, DwgVersion.Current);

        reservedPaths.Add(filePath);
        addedPaths.Add(filePath);
    }

    private static void UpdateMatchingBlocks(
        Document doc,
        AaaBlockExportItem block,
        IReadOnlyList<AaaBlockCatalogItem> matches,
        ICollection<string> updatedPaths)
    {
        foreach (var folderGroup in matches.GroupBy(x => Path.GetDirectoryName(x.FullPath) ?? ""))
        {
            var folderPath = folderGroup.Key;
            if (folderPath.Length == 0)
                continue;

            Directory.CreateDirectory(folderPath);
            var oldPaths = new HashSet<string>(
                folderGroup.Select(x => x.FullPath).Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            var targetPath = BuildReplacementFilePath(folderPath, block.DisplayName, block.BlockHandle, oldPaths);
            var tempPath = Path.Combine(folderPath, $"~aaa_update_{Guid.NewGuid():N}.dwg");

            try
            {
                var objectIds = new ObjectIdCollection();
                objectIds.Add(block.BlockReferenceId);

                using (var exportDatabase = doc.Database.Wblock(objectIds, block.BasePoint))
                {
                    exportDatabase.SaveAs(tempPath, DwgVersion.Current);
                }

                foreach (var oldPath in oldPaths)
                {
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);
                }

                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                File.Move(tempPath, targetPath);
                updatedPaths.Add(targetPath);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }
    }

    private static string BuildReplacementFilePath(
        string targetFolder,
        string displayName,
        string fallbackStem,
        ISet<string> pathsBeingReplaced)
    {
        var safeStem = AaaPathNamingHelper.SanitizeStem(displayName, fallbackStem);
        var preferred = Path.Combine(targetFolder, safeStem + ".dwg");
        if (!File.Exists(preferred) || pathsBeingReplaced.Contains(preferred))
            return preferred;

        var safeFallback = AaaPathNamingHelper.SanitizeStem(fallbackStem, "item");
        var withFallback = Path.Combine(targetFolder, $"{safeStem}_{safeFallback}.dwg");
        if (!File.Exists(withFallback) || pathsBeingReplaced.Contains(withFallback))
            return withFallback;

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(targetFolder, $"{safeStem}_{index}.dwg");
            if (!File.Exists(candidate) || pathsBeingReplaced.Contains(candidate))
                return candidate;
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 清理临时更新文件失败：{path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 清理临时更新文件失败（权限）：{path}", ex);
        }
    }
}
