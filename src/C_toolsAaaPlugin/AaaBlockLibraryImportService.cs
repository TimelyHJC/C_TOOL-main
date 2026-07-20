using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;

namespace C_toolsAaaPlugin;

internal static class AaaBlockLibraryImportService
{
    internal static AaaImportResult PromptAndImport(Document doc, string targetFolder)
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
                    return ImportBlocks(doc, targetFolder, impliedBlocks, "预选", impliedSelectedCount);
            }

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nV_AAA：请选择要添加到图库的图块（可多选）：",
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
                    ? AaaImportResult.CancelledResult("已取消添加图块。")
                    : AaaImportResult.Failure(UIMessages.Command.NoBlockSelected);
            }

            var selectedBlocks = AaaBlockSelectionReader.ReadSelection(doc.Database, picked.Value, out var selectedCount);
            return selectedBlocks.Count == 0
                ? AaaImportResult.Failure("没有可导入的图块。")
                : ImportBlocks(doc, targetFolder, selectedBlocks, "选中", selectedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图纸图块失败", ex);
            return AaaImportResult.Failure("导入", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图纸图块失败", ex);
            return AaaImportResult.Failure("导入", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图纸图块失败（权限）", ex);
            return AaaImportResult.Failure("导入", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 添加图纸图块失败", ex);
            return AaaImportResult.Failure("导入", ex);
        }
    }

    private static AaaImportResult ImportBlocks(
        Document doc,
        string targetFolder,
        IReadOnlyList<AaaBlockExportItem> blocks,
        string sourceLabel,
        int selectedCount)
    {
        Directory.CreateDirectory(targetFolder);

        var uniqueBlocks = KeepFirstBlockPerDisplayName(blocks, out var duplicateBlockCount);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importedPaths = new List<string>();
        var failedCount = 0;

        using var documentLock = doc.LockDocument();
        foreach (var block in uniqueBlocks)
        {
            try
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
                importedPaths.Add(filePath);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 导出图块失败（{block.DisplayName}）", ex);
            }
            catch (IOException ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 导出图块失败（{block.DisplayName}）", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 导出图块失败（权限，{block.DisplayName}）", ex);
            }
            catch (Exception ex)
            {
                failedCount++;
                C_toolsDiagnostics.LogNonFatal($"V_AAA 导出图块失败（{block.DisplayName}）", ex);
            }
        }

        if (importedPaths.Count == 0)
            return AaaImportResult.Failure("导入失败。");

        var ignoredCount = Math.Max(0, selectedCount - blocks.Count);
        var messageText = $"导入成功，新增 {importedPaths.Count} 个图块。";
        if (duplicateBlockCount > 0)
            messageText += $" 忽略 {duplicateBlockCount} 个重复图块。";
        if (ignoredCount > 0)
            messageText += $" 跳过 {ignoredCount} 个。";
        if (failedCount > 0)
            messageText += $" 失败 {failedCount} 个。";

        doc.Editor.WriteMessage($"\nV_AAA：{messageText}");
        return AaaImportResult.Succeed(messageText, importedPaths.Count);
    }

    private static List<AaaBlockExportItem> KeepFirstBlockPerDisplayName(
        IReadOnlyList<AaaBlockExportItem> blocks,
        out int duplicateBlockCount)
    {
        var uniqueBlocks = new List<AaaBlockExportItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        duplicateBlockCount = 0;

        foreach (var block in blocks)
        {
            var key = (block.DisplayName ?? "").Trim();
            if (key.Length == 0)
                key = block.BlockHandle;

            if (!seenNames.Add(key))
            {
                duplicateBlockCount++;
                continue;
            }

            uniqueBlocks.Add(block);
        }

        return uniqueBlocks;
    }
}

internal sealed class AaaImportResult
{
    public bool Success { get; private set; }
    public bool Cancelled { get; private set; }
    public string Message { get; private set; } = "";
    public int ImportedCount { get; private set; }

    internal static AaaImportResult Succeed(string message, int importedCount) =>
        new() { Success = true, Message = message ?? "", ImportedCount = Math.Max(0, importedCount) };

    internal static AaaImportResult CancelledResult(string message) =>
        new() { Cancelled = true, Message = message ?? "" };

    internal static AaaImportResult Failure(string message) =>
        new() { Message = message ?? "" };

    internal static AaaImportResult Failure(string action, Exception? ex) =>
        new() { Message = UIMessages.Common.BuildSimpleFailure(action, ex) };
}
