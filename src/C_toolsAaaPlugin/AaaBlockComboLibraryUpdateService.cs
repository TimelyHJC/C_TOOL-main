using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockComboLibraryUpdateService
{
    internal static AaaImportResult PromptAndImportOrUpdateCombo(
        Document doc,
        string targetFolder,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems)
    {
        if (doc == null)
            return AaaImportResult.Failure(UIMessages.BlockLibrary.NoActiveDocument);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return AaaImportResult.Failure(UIMessages.BlockLibrary.InvalidFolder);

        var candidates = (libraryItems ?? Array.Empty<AaaBlockCatalogItem>())
            .Where(x => x.IsCombo && !string.IsNullOrWhiteSpace(x.FullPath))
            .ToList();

        try
        {
            var editor = doc.Editor;
            var blocks = AaaBlockComboLibraryImportService.ReadComboSelection(doc, editor, out var selectedCount, out var wasCancelled);
            if (wasCancelled)
                return AaaImportResult.CancelledResult("已取消添加/更新。");
            if (blocks.Count < 2)
            {
                return selectedCount == 0
                    ? AaaImportResult.Failure("至少选择 2 个图块。")
                    : AaaImportResult.Failure("至少选择 2 个图块。");
            }

            var comboNameResult = AaaBlockComboLibraryImportService.PromptForComboName(editor, blocks.Count);
            if (!comboNameResult.Success)
                return AaaImportResult.CancelledResult("已取消添加/更新。");

            var basePointResult = AaaBlockComboLibraryImportService.PromptForComboBasePoint(editor, comboNameResult.ComboName);
            if (!basePointResult.Success)
                return AaaImportResult.CancelledResult("已取消添加/更新。");

            var deviceName = AaaBlockLibraryNameHelper.GetDeviceName(comboNameResult.ComboName);
            var hasMatches = deviceName.Length > 0 &&
                             candidates.Any(x => string.Equals(
                                 AaaBlockLibraryNameHelper.GetDeviceName(x.DisplayName),
                                 deviceName,
                                 StringComparison.OrdinalIgnoreCase));

            return hasMatches
                ? UpdateComboDefinitions(
                    doc,
                    targetFolder,
                    candidates,
                    comboNameResult.ComboName,
                    basePointResult.BasePoint,
                    blocks,
                    selectedCount)
                : AaaBlockComboLibraryImportService.CreateComboDefinition(
                    doc,
                    targetFolder,
                    comboNameResult.ComboName,
                    basePointResult.BasePoint,
                    blocks,
                    selectedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新组合图库失败", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新组合图库失败", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新组合图库失败（权限）", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加/更新组合图库失败", ex);
            return AaaImportResult.Failure("添加/更新", ex);
        }
    }

    internal static AaaImportResult PromptAndUpdateCombo(
        Document doc,
        string targetFolder,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems)
    {
        if (doc == null)
            return AaaImportResult.Failure(UIMessages.BlockLibrary.NoActiveDocument);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return AaaImportResult.Failure(UIMessages.BlockLibrary.InvalidFolder);

        var candidates = (libraryItems ?? Array.Empty<AaaBlockCatalogItem>())
            .Where(x => x.IsCombo && !string.IsNullOrWhiteSpace(x.FullPath))
            .ToList();
        if (candidates.Count == 0)
            return AaaImportResult.Failure("没有可更新的组合定义。");

        try
        {
            var editor = doc.Editor;
            var blocks = AaaBlockComboLibraryImportService.ReadComboSelection(doc, editor, out var selectedCount, out var wasCancelled);
            if (wasCancelled)
                return AaaImportResult.CancelledResult("已取消更新。");
            if (blocks.Count < 2)
            {
                return selectedCount == 0
                    ? AaaImportResult.Failure("至少选择 2 个图块。")
                    : AaaImportResult.Failure("至少选择 2 个图块。");
            }

            var comboNameResult = AaaBlockComboLibraryImportService.PromptForComboName(editor, blocks.Count);
            if (!comboNameResult.Success)
                return AaaImportResult.CancelledResult("已取消更新。");

            var basePointResult = AaaBlockComboLibraryImportService.PromptForComboBasePoint(editor, comboNameResult.ComboName);
            if (!basePointResult.Success)
                return AaaImportResult.CancelledResult("已取消更新。");

            return UpdateComboDefinitions(
                doc,
                targetFolder,
                candidates,
                comboNameResult.ComboName,
                basePointResult.BasePoint,
                blocks,
                selectedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新组合图库失败", ex);
            return AaaImportResult.Failure("更新", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新组合图库失败", ex);
            return AaaImportResult.Failure("更新", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新组合图库失败（权限）", ex);
            return AaaImportResult.Failure("更新", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 更新组合图库失败", ex);
            return AaaImportResult.Failure("更新", ex);
        }
    }

    private static AaaImportResult UpdateComboDefinitions(
        Document doc,
        string libraryRootPath,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems,
        string comboName,
        Point3d comboBasePoint,
        IReadOnlyList<AaaBlockExportItem> selectedBlocks,
        int selectedCount)
    {
        var deviceName = AaaBlockLibraryNameHelper.GetDeviceName(comboName);
        if (deviceName.Length == 0)
            return AaaImportResult.Failure("组合名称无效。");

        var matches = libraryItems
            .Where(x => string.Equals(
                AaaBlockLibraryNameHelper.GetDeviceName(x.DisplayName),
                deviceName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return AaaImportResult.Failure("更新失败：未找到可更新项。");

        var updatedDefinitionPaths = new List<string>();
        var failedFolders = new List<string>();
        var replacedDefinitionCount = 0;

        foreach (var folderGroup in matches.GroupBy(
                     x => Path.GetDirectoryName(x.FullPath) ?? "",
                     StringComparer.OrdinalIgnoreCase))
        {
            var parentFolder = folderGroup.Key;
            if (parentFolder.Length == 0)
                continue;

            Directory.CreateDirectory(parentFolder);
            var oldPaths = new HashSet<string>(
                folderGroup
                    .Select(x => x.FullPath)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            if (oldPaths.Count == 0)
                continue;

            var targetPath = BuildReplacementComboPath(parentFolder, comboName, "组合", oldPaths);
            var tempPath = Path.Combine(parentFolder, $"~aaa_combo_update_{Guid.NewGuid():N}{AaaBlockComboPackageStore.PackageSuffix}");

            try
            {
                using var documentLock = doc.LockDocument();
                AaaBlockComboLibraryImportService.WriteComboDefinition(
                    doc.Database,
                    libraryRootPath,
                    tempPath,
                    comboName,
                    comboBasePoint,
                    selectedBlocks);

                foreach (var oldPath in oldPaths)
                    AaaBlockComboPackageStore.TryDeleteComboArtifact(oldPath);

                AaaBlockComboPackageStore.TryDeleteComboArtifact(targetPath);
                File.Move(tempPath, targetPath);
                updatedDefinitionPaths.Add(targetPath);
                replacedDefinitionCount += oldPaths.Count;
            }
            catch (InvalidOperationException ex)
            {
                failedFolders.Add(parentFolder);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新组合定义失败（{deviceName}）", ex);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                failedFolders.Add(parentFolder);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新组合定义失败（{deviceName}）", ex);
            }
            catch (IOException ex)
            {
                failedFolders.Add(parentFolder);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新组合定义失败（{deviceName}）", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                failedFolders.Add(parentFolder);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新组合定义失败（权限，{deviceName}）", ex);
            }
            catch (Exception ex)
            {
                failedFolders.Add(parentFolder);
                C_toolsDiagnostics.LogNonFatal($"V_AAA 更新组合定义失败（{deviceName}）", ex);
            }
            finally
            {
                AaaBlockComboPackageStore.TryDeleteComboArtifact(tempPath);
            }
        }

        if (updatedDefinitionPaths.Count == 0)
            return AaaImportResult.Failure("更新失败。");

        var ignoredCount = Math.Max(0, selectedCount - selectedBlocks.Count);
        var messageText = $"更新成功，替换 {replacedDefinitionCount} 个组合定义。";
        if (ignoredCount > 0)
            messageText += $" 跳过 {ignoredCount} 个。";
        if (failedFolders.Count > 0)
            messageText += $" 失败 {failedFolders.Count} 个。";

        doc.Editor.WriteMessage($"\nV_AAA：{messageText}");
        return AaaImportResult.Succeed(messageText, updatedDefinitionPaths.Count);
    }

    private static string BuildReplacementComboPath(
        string targetFolder,
        string displayName,
        string fallbackStem,
        ISet<string> pathsBeingReplaced)
    {
        var safeStem = AaaPathNamingHelper.SanitizeStem(displayName, fallbackStem);
        var preferred = Path.Combine(targetFolder, safeStem + AaaBlockComboPackageStore.PackageSuffix);
        if (!ComboArtifactExists(preferred) || pathsBeingReplaced.Contains(preferred))
            return preferred;

        var safeFallback = AaaPathNamingHelper.SanitizeStem(fallbackStem, "combo");
        var withFallback = Path.Combine(targetFolder, $"{safeStem}_{safeFallback}{AaaBlockComboPackageStore.PackageSuffix}");
        if (!ComboArtifactExists(withFallback) || pathsBeingReplaced.Contains(withFallback))
            return withFallback;

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(targetFolder, $"{safeStem}_{index}{AaaBlockComboPackageStore.PackageSuffix}");
            if (!ComboArtifactExists(candidate) || pathsBeingReplaced.Contains(candidate))
                return candidate;
        }
    }

    private static bool ComboArtifactExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }
}
