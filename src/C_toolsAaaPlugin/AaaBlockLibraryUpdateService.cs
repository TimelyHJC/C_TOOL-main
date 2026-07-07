using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockLibraryUpdateService
{
    internal static AaaImportResult PromptAndUpdate(
        Document doc,
        string targetFolder,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems)
    {
        if (doc == null)
            return AaaImportResult.Failure(UIMessages.BlockLibrary.NoActiveDocument);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return AaaImportResult.Failure(UIMessages.BlockLibrary.InvalidFolder);

        var candidates = (libraryItems ?? Array.Empty<AaaBlockCatalogItem>())
            .Where(x => !x.IsCombo && !string.IsNullOrWhiteSpace(x.FullPath))
            .ToList();
        if (candidates.Count == 0)
            return AaaImportResult.Failure("没有可更新的图块。");

        try
        {
            var editor = doc.Editor;
            var implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                var impliedBlocks = AaaBlockSelectionReader.ReadSelection(doc.Database, implied.Value, out var impliedSelectedCount);
                if (impliedBlocks.Count > 0)
                    return UpdateBlocks(doc, candidates, impliedBlocks, "预选", impliedSelectedCount);
            }

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nV_AAA：请选择要更新到图库的图块（可多选）：",
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
                    ? AaaImportResult.CancelledResult("已取消更新图库图块。")
                    : AaaImportResult.Failure(UIMessages.Command.NoBlockSelected);
            }

            var selectedBlocks = AaaBlockSelectionReader.ReadSelection(doc.Database, picked.Value, out var selectedCount);
            return selectedBlocks.Count == 0
                ? AaaImportResult.Failure("没有可更新的图块。")
                : UpdateBlocks(doc, candidates, selectedBlocks, "选中", selectedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新图库图块失败", ex);
            return AaaImportResult.Failure("更新", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新图库图块失败", ex);
            return AaaImportResult.Failure("更新", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新图库图块失败（权限）", ex);
            return AaaImportResult.Failure("更新", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新图库图块失败", ex);
            return AaaImportResult.Failure("更新", ex);
        }
    }

    private static AaaImportResult UpdateBlocks(
        Document doc,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems,
        IReadOnlyList<AaaBlockExportItem> selectedBlocks,
        string sourceLabel,
        int selectedCount)
    {
        var deviceIndex = libraryItems
            .GroupBy(x => AaaBlockLibraryNameHelper.GetDeviceName(x.DisplayName), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

        var updatedPaths = new List<string>();
        var missingDevices = new List<string>();
        var failedDevices = new List<string>();
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateSelectedCount = 0;

        using var documentLock = doc.LockDocument();
        foreach (var block in selectedBlocks)
        {
            var deviceName = AaaBlockLibraryNameHelper.GetDeviceName(block.DisplayName);
            if (deviceName.Length == 0)
            {
                missingDevices.Add(block.DisplayName);
                continue;
            }

            if (!processedKeys.Add(deviceName))
            {
                duplicateSelectedCount++;
                continue;
            }

            if (!deviceIndex.TryGetValue(deviceName, out var matches) || matches.Count == 0)
            {
                missingDevices.Add(deviceName);
                continue;
            }

            try
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
                        if (File.Exists(tempPath))
                        {
                            try
                            {
                                File.Delete(tempPath);
                            }
                            catch (IOException ex)
                            {
                                C_toolsDiagnostics.LogNonFatal($"V_AAA 清理临时更新文件失败：{tempPath}", ex);
                            }
                            catch (UnauthorizedAccessException ex)
                            {
                                C_toolsDiagnostics.LogNonFatal($"V_AAA 清理临时更新文件失败（权限）：{tempPath}", ex);
                            }
                        }
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                failedDevices.Add(deviceName);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新图库图块失败（{deviceName}）", ex);
            }
            catch (IOException ex)
            {
                failedDevices.Add(deviceName);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新图库图块失败（{deviceName}）", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                failedDevices.Add(deviceName);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新图库图块失败（权限，{deviceName}）", ex);
            }
            catch (Exception ex)
            {
                failedDevices.Add(deviceName);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新图库图块失败（{deviceName}）", ex);
            }
        }

        if (updatedPaths.Count == 0)
        {
            if (missingDevices.Count > 0)
                return AaaImportResult.Failure("更新失败：未找到可更新项。");

            return AaaImportResult.Failure("更新失败。");
        }

        var ignoredCount = Math.Max(0, selectedCount - selectedBlocks.Count);
        var messageText = $"更新成功，替换 {updatedPaths.Count} 个图块。";
        if (ignoredCount > 0)
            messageText += $" 跳过 {ignoredCount} 个。";
        if (duplicateSelectedCount > 0)
            messageText += $" 忽略 {duplicateSelectedCount} 个重复项。";
        if (missingDevices.Count > 0)
            messageText += $" 未匹配 {missingDevices.Count} 个。";
        if (failedDevices.Count > 0)
            messageText += $" 失败 {failedDevices.Count} 个。";

        doc.Editor.WriteMessage($"\nV_AAA：{messageText}");
        return AaaImportResult.Succeed(messageText, updatedPaths.Count);
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
}
