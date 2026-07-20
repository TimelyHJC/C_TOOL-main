using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockComboLibraryImportService
{
    internal static AaaImportResult PromptAndImportCombo(Document doc, string targetFolder)
    {
        if (doc == null)
            return AaaImportResult.Failure(UIMessages.BlockLibrary.NoActiveDocument);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return AaaImportResult.Failure(UIMessages.BlockLibrary.InvalidFolder);

        try
        {
            var editor = doc.Editor;
            var blocks = ReadComboSelection(doc, editor, out var selectedCount, out var wasCancelled);
            if (wasCancelled)
                return AaaImportResult.CancelledResult("已取消导入。");
            if (blocks.Count < 2)
            {
                return selectedCount == 0
                    ? AaaImportResult.Failure("至少选择 2 个图块。")
                    : AaaImportResult.Failure("至少选择 2 个图块。");
            }

            var comboNameResult = PromptForComboName(editor, blocks.Count);
            if (!comboNameResult.Success)
                return comboNameResult.Result;

            var basePointResult = PromptForComboBasePoint(editor, comboNameResult.ComboName);
            if (!basePointResult.Success)
                return basePointResult.Result;

            return CreateComboDefinition(doc, targetFolder, comboNameResult.ComboName, basePointResult.BasePoint, blocks, selectedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图块组合失败", ex);
            return AaaImportResult.Failure("导入", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图块组合失败", ex);
            return AaaImportResult.Failure("导入", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图块组合失败（权限）", ex);
            return AaaImportResult.Failure("导入", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图块组合失败", ex);
            return AaaImportResult.Failure("导入", ex);
        }
    }

    internal static List<AaaBlockExportItem> ReadComboSelection(
        Document doc,
        Editor editor,
        out int selectedCount,
        out bool wasCancelled)
    {
        selectedCount = 0;
        wasCancelled = false;

        var implied = editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            var impliedBlocks = AaaBlockSelectionReader.ReadSelection(doc.Database, implied.Value, out selectedCount);
            if (impliedBlocks.Count >= 2)
                return impliedBlocks;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nV_AAA：请选择要添加为组合的图块（至少 2 个）：",
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
            wasCancelled = picked.Status == PromptStatus.Cancel;
            return new List<AaaBlockExportItem>();
        }

        return AaaBlockSelectionReader.ReadSelection(doc.Database, picked.Value, out selectedCount);
    }

    internal static (bool Success, string ComboName, AaaImportResult Result) PromptForComboName(Editor editor, int blockCount)
    {
        var options = new PromptStringOptions($"\nV_AAA：输入组合名称（共 {blockCount} 个图块）：")
        {
            AllowSpaces = true
        };
        var result = editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return (false, "", AaaImportResult.CancelledResult("已取消导入。"));

        var comboName = (result.StringResult ?? "").Trim();
        if (comboName.Length == 0)
            comboName = $"组合_{DateTime.Now:MMdd_HHmmss}";

        return (true, comboName, AaaImportResult.Succeed("", 0));
    }

    internal static (bool Success, Point3d BasePoint, AaaImportResult Result) PromptForComboBasePoint(Editor editor, string comboName)
    {
        var options = new PromptPointOptions($"\nV_AAA：指定组合“{comboName}”的插入基点：");
        var result = editor.GetPoint(options);
        if (result.Status != PromptStatus.OK)
            return (false, Point3d.Origin, AaaImportResult.CancelledResult("已取消导入。"));

        return (true, result.Value, AaaImportResult.Succeed("", 0));
    }

    private static AaaImportResult CreateComboDefinition(
        Document doc,
        string targetFolder,
        string comboName,
        Point3d comboBasePoint,
        IReadOnlyList<AaaBlockExportItem> blocks,
        int selectedCount)
    {
        Directory.CreateDirectory(targetFolder);

        var comboPath = AaaBlockComboPackageStore.BuildUniqueDefinitionPath(targetFolder, comboName);

        try
        {
            using var documentLock = doc.LockDocument();
            WriteComboDefinition(doc.Database, targetFolder, comboPath, comboName, comboBasePoint, blocks);

            var ignoredCount = Math.Max(0, selectedCount - blocks.Count);
            var message = "导入成功，新增 1 个组合定义。";
            if (ignoredCount > 0)
                message += $" 跳过 {ignoredCount} 个。";

            doc.Editor.WriteMessage($"\nV_AAA：{message}");
            return AaaImportResult.Succeed(message, 1);
        }
        catch (InvalidOperationException ex)
        {
            AaaBlockComboPackageStore.TryDeleteComboArtifact(comboPath);
            C_toolsDiagnostics.LogNonFatal("V_AAA 创建组合定义失败", ex);
            return AaaImportResult.Failure(ex.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            AaaBlockComboPackageStore.TryDeleteComboArtifact(comboPath);
            throw;
        }
        catch (IOException)
        {
            AaaBlockComboPackageStore.TryDeleteComboArtifact(comboPath);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            AaaBlockComboPackageStore.TryDeleteComboArtifact(comboPath);
            throw;
        }
        catch (ArgumentException)
        {
            AaaBlockComboPackageStore.TryDeleteComboArtifact(comboPath);
            throw;
        }
    }

    internal static void WriteComboDefinition(
        Database sourceDatabase,
        string libraryRootPath,
        string comboPath,
        string comboName,
        Point3d comboBasePoint,
        IReadOnlyList<AaaBlockExportItem> blocks)
    {
        var singleBlocks = AaaComboLibraryReferenceResolver.LoadSingleBlocks(libraryRootPath).ToList();
        var reservedPaths = new HashSet<string>(
            singleBlocks
                .Select(x => x.FullPath)
                .Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        var manifest = new AaaBlockComboManifest
        {
            DisplayName = comboName,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            BasePointX = comboBasePoint.X,
            BasePointY = comboBasePoint.Y,
            BasePointZ = comboBasePoint.Z,
            PreviewRelativePath = ""
        };

        var missingDevices = new List<string>();
        foreach (var block in blocks)
        {
            var deviceName = AaaBlockLibraryNameHelper.GetDeviceName(block.DisplayName);
            var reference = new AaaBlockComboMember
            {
                DisplayName = block.DisplayName,
                DeviceName = deviceName
            };

            var resolvedItem = AaaComboLibraryReferenceResolver.ResolveLibraryBlock(reference, singleBlocks, libraryRootPath) ??
                               ExportBlockToLibrary(sourceDatabase, libraryRootPath, block, reservedPaths);
            if (resolvedItem == null || !File.Exists(resolvedItem.FullPath))
            {
                missingDevices.Add(deviceName.Length == 0 ? block.DisplayName : deviceName);
                continue;
            }

            var offset = block.BasePoint - comboBasePoint;
            manifest.Members.Add(new AaaBlockComboMember
            {
                DisplayName = block.DisplayName,
                DeviceName = deviceName,
                SourceRelativePath = AaaComboLibraryReferenceResolver.BuildRelativePath(libraryRootPath, resolvedItem.FullPath),
                SourceRelativeFolder = resolvedItem.RelativeFolder,
                SourceHandle = block.BlockHandle,
                OffsetX = offset.X,
                OffsetY = offset.Y,
                OffsetZ = offset.Z,
                Rotation = block.Rotation,
                ScaleX = block.ScaleX,
                ScaleY = block.ScaleY,
                ScaleZ = block.ScaleZ,
                LayerName = block.LayerName
            });

            if (!singleBlocks.Any(
                    x => string.Equals(x.FullPath, resolvedItem.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                singleBlocks.Add(resolvedItem);
            }
        }

        if (manifest.Members.Count < 2)
        {
            var details = string.Join("、", missingDevices
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5));
            throw new InvalidOperationException(
                details.Length == 0
                    ? "独立图库中未找到对应图块，无法创建组合定义。"
                    : $"独立图库中未找到对应图块：{details}");
        }

        if (missingDevices.Count > 0)
        {
            var details = string.Join("、", missingDevices
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5));
            throw new InvalidOperationException($"以下图块未在独立图库中找到：{details}");
        }

        AaaBlockComboPackageStore.Save(comboPath, manifest);
    }

    private static AaaBlockCatalogItem ExportBlockToLibrary(
        Database sourceDatabase,
        string libraryRootPath,
        AaaBlockExportItem block,
        ISet<string> reservedPaths)
    {
        Directory.CreateDirectory(libraryRootPath);

        var filePath = AaaPathNamingHelper.BuildUniqueDwgFilePath(
            libraryRootPath,
            block.DisplayName,
            block.BlockHandle,
            reservedPaths);

        var objectIds = new ObjectIdCollection();
        objectIds.Add(block.BlockReferenceId);

        using (var exportDatabase = sourceDatabase.Wblock(objectIds, block.BasePoint))
        {
            exportDatabase.SaveAs(filePath, DwgVersion.Current);
        }

        reservedPaths.Add(filePath);
        return CreateSingleBlockCatalogItem(libraryRootPath, filePath);
    }

    private static AaaBlockCatalogItem CreateSingleBlockCatalogItem(string libraryRootPath, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var relativePath = AaaComboLibraryReferenceResolver.BuildRelativePath(libraryRootPath, fullPath);
        var relativeFolder = Path.GetDirectoryName(relativePath) ?? "";
        relativeFolder = relativeFolder
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return new AaaBlockCatalogItem
        {
            Kind = AaaBlockCatalogItemKind.Dwg,
            LibraryRootPath = libraryRootPath,
            DisplayName = Path.GetFileNameWithoutExtension(fullPath),
            RelativeFolder = relativeFolder,
            FullPath = fullPath,
            PreviewSourcePath = fullPath,
            PreviewLastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath),
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath)
        };
    }
}
