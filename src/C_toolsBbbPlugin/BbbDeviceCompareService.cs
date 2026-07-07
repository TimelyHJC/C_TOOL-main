using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal static class BbbDeviceCompareService
{
    internal static BbbSelectionCaptureResult CaptureImpliedSelectedBlocks(Document doc)
    {
        var editor = doc.Editor;
        var implied = editor.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count == 0)
            return new BbbSelectionCaptureResult();

        return ReadSelection(doc.Database, implied.Value, "预选对象中未找到 F_BXR 写入的隐藏设备名称。", "预选");
    }

    internal static BbbSelectionCaptureResult CaptureSelectedBlocks(Document doc)
    {
        var impliedCapture = CaptureImpliedSelectedBlocks(doc);
        if (impliedCapture.Devices.Count > 0)
            return impliedCapture;

        var editor = doc.Editor;

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\n请选择需要输出到 Excel 的设备块: "
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "INSERT")
        });
        var picked = editor.GetSelection(options, filter);
        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            return new BbbSelectionCaptureResult
            {
                Message = picked.Status == PromptStatus.Cancel ? string.Format(UIMessages.Command.Cancelled, "读取选中块") : UIMessages.Command.NoDeviceBlockSelected
            };
        }

        return ReadSelection(doc.Database, picked.Value, "所选对象中未找到 F_BXR 写入的隐藏设备名称。", "选中");
    }

    internal static BbbMatchResult Match(
        BbbSelectedBlockInfo block,
        IReadOnlyList<BbbExcelDeviceRow> excelRows,
        ISet<int>? reservedExcelRows = null)
    {
        if (!HasComparableValue(block))
        {
            return new BbbMatchResult
            {
                Status = "缺少名称",
                Reason = "未读取到有效的 F_BXR 隐藏设备名称。"
            };
        }

        var allCandidates = BuildCandidates(block, excelRows);
        if (allCandidates.Count == 0)
        {
            return new BbbMatchResult
            {
                Status = "未匹配",
                Reason = "Excel 中未找到可匹配的设备行。"
            };
        }

        var availableCandidates = reservedExcelRows == null
            ? allCandidates
            : allCandidates.Where(x => !reservedExcelRows.Contains(x.ExcelRow.SourceRowNumber)).ToList();

        if (availableCandidates.Count == 0)
        {
            return new BbbMatchResult
            {
                Status = "需确认",
                Reason = "候选 Excel 行已被其他设备占用，建议人工确认。"
            };
        }

        var ordered = availableCandidates
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.ExcelRow.SourceRowNumber)
            .ToList();

        var best = ordered[0];
        var secondScore = ordered.Count > 1 ? ordered[1].Score : int.MinValue;
        var hasTie = ordered.Count > 1 && ordered[1].Score == best.Score;

        var isAccepted =
            (!hasTie && best.ExactName) ||
            (!hasTie && best.Score >= 90 && best.Score >= secondScore + 15) ||
            (!hasTie && best.Score >= 60 && best.Score >= secondScore + 25);

        if (!isAccepted)
        {
            return new BbbMatchResult
            {
                Status = "需确认",
                Reason = hasTie
                    ? $"存在多个同分候选，建议人工确认（优先 Excel 第 {best.ExcelRow.SourceRowNumber} 行）。"
                    : $"匹配结果不够稳定，建议人工确认（优先 Excel 第 {best.ExcelRow.SourceRowNumber} 行）。",
                ExcelRow = best.ExcelRow
            };
        }

        return new BbbMatchResult
        {
            Status = "已匹配",
            Reason = BuildReason(best),
            ExcelRow = best.ExcelRow
        };
    }

    private static BbbSelectionCaptureResult ReadSelection(
        Database database,
        SelectionSet selectionSet,
        string emptyMessage,
        string sourceLabel)
    {
        var devices = ReadBlocks(database, selectionSet, out var blockCount, out var managedBlockCount, out var skippedNoManagedNameCount);
        if (devices.Count == 0)
        {
            return new BbbSelectionCaptureResult
            {
                Devices = devices,
                BlockCount = blockCount,
                ManagedBlockCount = managedBlockCount,
                SkippedNoManagedNameCount = skippedNoManagedNameCount,
                Message = emptyMessage
            };
        }

        var message = $"已从 {managedBlockCount} 个{sourceLabel}块读取 {devices.Count} 条 F_BXR 设备记录。";
        if (skippedNoManagedNameCount > 0)
            message += $" 另有 {skippedNoManagedNameCount} 个块未配置 F_BXR 隐形设备名称。";

        return new BbbSelectionCaptureResult
        {
            Devices = devices,
            BlockCount = blockCount,
            ManagedBlockCount = managedBlockCount,
            SkippedNoManagedNameCount = skippedNoManagedNameCount,
            Message = message
        };
    }

    private static List<BbbSelectedBlockInfo> ReadBlocks(
        Database database,
        SelectionSet selectionSet,
        out int blockCount,
        out int managedBlockCount,
        out int skippedNoManagedNameCount)
    {
        var list = new List<BbbSelectedBlockInfo>();
        blockCount = 0;
        managedBlockCount = 0;
        skippedNoManagedNameCount = 0;

        var countedBlocks = 0;
        var countedManagedBlocks = 0;
        var countedSkippedBlocks = 0;
        list = CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                var blockInfos = new List<BbbSelectedBlockInfo>();
                foreach (SelectedObject? selectedObject in selectionSet)
                {
                    if (selectedObject?.ObjectId.IsNull != false)
                        continue;

                    if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, selectedObject.ObjectId, OpenMode.ForRead, out var blockReference) ||
                        blockReference == null)
                    {
                        continue;
                    }

                    countedBlocks++;
                    var deviceInfos = BuildBlockInfos(blockReference, transaction);
                    if (deviceInfos.Count == 0)
                    {
                        countedSkippedBlocks++;
                        continue;
                    }

                    countedManagedBlocks++;
                    blockInfos.AddRange(deviceInfos);
                }

                return blockInfos;
            });

        blockCount = countedBlocks;
        managedBlockCount = countedManagedBlocks;
        skippedNoManagedNameCount = countedSkippedBlocks;
        return list;
    }

    private static List<BbbSelectedBlockInfo> BuildBlockInfos(BlockReference blockReference, Transaction transaction)
    {
        var group = BbbDynamicBlockStateHelper.ReadCurrentManagedNames(blockReference, transaction);
        if (group.Entries.Count == 0)
            return new List<BbbSelectedBlockInfo>();

        var preview = BuildAttributePreview(group.Entries);
        var stateDisplayText = BbbDynamicBlockStateHelper.GetStateDisplayText(group.StateInfo);
        var blockHandle = blockReference.Handle.ToString();
        var blockName = GetDisplayBlockName(blockReference, transaction);

        return group.Entries
            .OrderBy(x => x.SlotIndex)
            .Select(x => new BbbSelectedBlockInfo
            {
                BlockHandle = blockHandle,
                BlockName = blockName,
                StateDisplayText = stateDisplayText,
                SlotIndex = x.SlotIndex,
                DeviceName = x.Name,
                AttributePreview = preview
            })
            .ToList();
    }

    private static List<BbbMatchCandidate> BuildCandidates(BbbSelectedBlockInfo block, IReadOnlyList<BbbExcelDeviceRow> excelRows)
    {
        var candidates = new List<BbbMatchCandidate>();
        foreach (var excelRow in excelRows)
        {
            var candidate = Score(block, excelRow);
            if (candidate.Score > 0)
                candidates.Add(candidate);
        }

        return candidates;
    }

    private static bool HasComparableValue(BbbSelectedBlockInfo block) =>
        NormalizeValue(block.DeviceName).Length > 0;

    private static BbbMatchCandidate Score(BbbSelectedBlockInfo block, BbbExcelDeviceRow excelRow)
    {
        var score = 0;
        var reasons = new List<string>();

        var exactName = Equivalent(block.DeviceName, excelRow.DeviceName);
        if (exactName)
        {
            score += 120;
            reasons.Add("名称精确匹配");
        }
        else if (ContainsEquivalent(block.DeviceName, excelRow.DeviceName))
        {
            score += 70;
            reasons.Add("名称近似匹配");
        }

        return new BbbMatchCandidate
        {
            ExcelRow = excelRow,
            Score = score,
            ExactName = exactName,
            Reasons = reasons
        };
    }

    private static string BuildReason(BbbMatchCandidate candidate)
    {
        if (candidate.ExactName)
            return $"设备名称精确匹配（Excel 第 {candidate.ExcelRow.SourceRowNumber} 行）。";

        var reasonText = candidate.Reasons.Count == 0
            ? "名称匹配"
            : string.Join("，", candidate.Reasons.Take(2));
        return $"{reasonText}（Excel 第 {candidate.ExcelRow.SourceRowNumber} 行）。";
    }

    private static string BuildAttributePreview(IReadOnlyList<BbbManagedDeviceNameEntry> entries)
    {
        if (entries.Count == 0)
            return "";

        return string.Join(" / ", entries.Select(x => $"{x.SlotIndex}:{x.Name}"));
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
        return string.IsNullOrWhiteSpace(directName) ? "<匿名块>" : directName;
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

    private static bool Equivalent(string? left, string? right)
    {
        var normalizedLeft = NormalizeValue(left);
        var normalizedRight = NormalizeValue(right);
        return normalizedLeft.Length > 0 &&
               normalizedRight.Length > 0 &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static bool ContainsEquivalent(string? left, string? right)
    {
        var normalizedLeft = NormalizeValue(left);
        var normalizedRight = NormalizeValue(right);
        if (normalizedLeft.Length < 2 || normalizedRight.Length < 2 || normalizedLeft == normalizedRight)
            return false;

        return normalizedLeft.IndexOf(normalizedRight, StringComparison.Ordinal) >= 0 ||
               normalizedRight.IndexOf(normalizedLeft, StringComparison.Ordinal) >= 0;
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value!.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/' || ch == '\\' || ch == '(' || ch == ')' || ch == '（' || ch == '）' || ch == '[' || ch == ']' || ch == '【' || ch == '】' || ch == '.' || ch == ',' || ch == '，' || ch == ':' || ch == '：')
                continue;

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}

internal sealed class BbbSelectionCaptureResult
{
    internal List<BbbSelectedBlockInfo> Devices { get; set; } = new();
    internal string Message { get; set; } = "";
    internal int BlockCount { get; set; }
    internal int ManagedBlockCount { get; set; }
    internal int SkippedNoManagedNameCount { get; set; }
}

internal sealed class BbbSelectedBlockInfo
{
    internal string BlockHandle { get; set; } = "";
    internal string BlockName { get; set; } = "";
    internal string StateDisplayText { get; set; } = "";
    internal int SlotIndex { get; set; }
    internal string DeviceName { get; set; } = "";
    internal string AttributePreview { get; set; } = "";
}

internal sealed class BbbMatchResult
{
    internal string Status { get; set; } = "";
    internal string Reason { get; set; } = "";
    internal BbbExcelDeviceRow? ExcelRow { get; set; }
}

internal sealed class BbbMatchCandidate
{
    internal BbbExcelDeviceRow ExcelRow { get; set; } = new();
    internal int Score { get; set; }
    internal bool ExactName { get; set; }
    internal List<string> Reasons { get; set; } = new();
}
